using System.Runtime.InteropServices;
using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// PHASE 18 F2 — HAGS tel que Windows l'APPLIQUE, pas tel que le registre le raconte.
///
/// Le produit lisait la presence de `HwSchMode`. Mesure du 10/09/2026 sur la machine de
/// reference : cle absente, HAGS actif (`0x0B` par D3DKMT). Le gate lisait donc « inconnu » la ou
/// l'etat etait connaissable, et l'ancien gate (`!= On`) aurait meme propose d'activer un
/// ordonnancement deja actif — un placebo.
/// </summary>
public interface IHagsEffectiveReader
{
    /// <summary>On / Off lus sur l'adaptateur qui affiche ; Unknown sinon. Ne leve jamais.</summary>
    TriState Lire();
}

/// <summary>Ce que D3DKMT rend pour un adaptateur. <paramref name="Lu"/> = la requete a reussi.</summary>
public readonly record struct AdaptateurHags(uint Sources, bool Lu, uint Caps);

/// <summary>
/// `D3DKMTEnumAdapters2` → `D3DKMTQueryAdapterInfo(KMTQAITYPE_WDDM_2_7_CAPS = 70, 4 octets)` →
/// `D3DKMTCloseAdapter`. Sequence reprise de la sonde lecture seule qui a rendu `0x0B` sur la
/// machine de reference le 10/09/2026 (audit externe du 10/09).
///
/// La structure `D3DKMT_WDDM_2_7_CAPS` est documentee (d3dkmthk.h) mais marquee « reserved for
/// system use » : c'est une lecture de BONNE FOI, jamais utilisee pour ecrire, et toute reponse
/// qu'on ne sait pas lire devient Unknown.
/// </summary>
public sealed class D3dkmtHagsReader : IHagsEffectiveReader
{
    private const uint Wddm27Caps = 70;       // KMTQAITYPE_WDDM_2_7_CAPS
    private const uint HwSchSupporte = 0x1;   // HwSchSupported
    private const uint HwSchActive = 0x2;     // HwSchEnabled

    public TriState Lire()
    {
        try { return Decider(Enumerer()); }
        catch { return TriState.Unknown; }
    }

    /// <summary>
    /// Pur. L'adaptateur qui compte est celui qui AFFICHE (au moins une source) : un adaptateur
    /// sans sortie (rendu hors ecran, adaptateur logiciel) ne dit rien de ce que le jeu obtient.
    /// Rien de lisible, ou HAGS non supporte ⇒ Unknown : « on ne peut pas l'activer » n'est pas
    /// « il est eteint ». Deux adaptateurs qui affichent et se contredisent ⇒ Unknown : choisir
    /// l'un serait deviner lequel porte le jeu.
    /// </summary>
    public static TriState Decider(IReadOnlyList<AdaptateurHags> adaptateurs)
    {
        var retenus = adaptateurs
            .Where(a => a.Lu && a.Sources > 0 && (a.Caps & HwSchSupporte) != 0)
            .ToList();
        if (retenus.Count == 0) return TriState.Unknown;

        var actifs = retenus.Count(a => (a.Caps & HwSchActive) != 0);
        if (actifs == retenus.Count) return TriState.On;
        return actifs == 0 ? TriState.Off : TriState.Unknown;
    }

    private static List<AdaptateurHags> Enumerer()
    {
        var resultat = new List<AdaptateurHags>();
        var e = new EnumAdapters2();
        if (D3DKMTEnumAdapters2(ref e) != 0 || e.NumAdapters == 0) return resultat;

        var taille = Marshal.SizeOf<AdapterInfo>();
        e.pAdapters = Marshal.AllocHGlobal(taille * (int)e.NumAdapters);
        try
        {
            if (D3DKMTEnumAdapters2(ref e) != 0) return resultat;
            for (var i = 0; i < e.NumAdapters; i++)
            {
                var a = Marshal.PtrToStructure<AdapterInfo>(e.pAdapters + i * taille);
                var caps = Marshal.AllocHGlobal(4);
                try
                {
                    Marshal.WriteInt32(caps, 0);
                    var q = new QueryAdapterInfo
                    {
                        hAdapter = a.hAdapter, Type = Wddm27Caps,
                        pPrivateDriverData = caps, PrivateDriverDataSize = 4,
                    };
                    var lu = D3DKMTQueryAdapterInfo(ref q) == 0;
                    resultat.Add(new AdaptateurHags(a.NumOfSources, lu, lu ? (uint)Marshal.ReadInt32(caps) : 0));
                }
                finally
                {
                    Marshal.FreeHGlobal(caps);
                    var c = new CloseAdapter { hAdapter = a.hAdapter };
                    D3DKMTCloseAdapter(ref c);
                }
            }
        }
        finally { Marshal.FreeHGlobal(e.pAdapters); }
        return resultat;
    }

    [StructLayout(LayoutKind.Sequential)] private struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] private struct AdapterInfo { public uint hAdapter; public Luid AdapterLuid; public uint NumOfSources; public int bPresentMoveRegionsPreferred; }
    [StructLayout(LayoutKind.Sequential)] private struct EnumAdapters2 { public uint NumAdapters; public IntPtr pAdapters; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryAdapterInfo { public uint hAdapter; public uint Type; public IntPtr pPrivateDriverData; public uint PrivateDriverDataSize; }
    [StructLayout(LayoutKind.Sequential)] private struct CloseAdapter { public uint hAdapter; }

    [DllImport("gdi32.dll")] private static extern int D3DKMTEnumAdapters2(ref EnumAdapters2 p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryAdapterInfo p);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref CloseAdapter p);
}
