using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// Les fréquences qu'un écran tient à une résolution, lues dans les timings détaillés
/// de son EDID (bloc de base et extensions CTA-861). Le descripteur « Range Limits » n'est pas lu : il couvre toutes les
/// résolutions, et une fréquence qu'il annonce peut n'exister qu'en dessous de la résolution du joueur. Un écran qui ne
/// détaille ses modes rapides qu'en DisplayID rend null ici : on se tait plutôt que de deviner.
/// </summary>
public static class EdidFrequences
{
    private const int Bloc = 128;
    private static readonly byte[] Entete = [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    /// <summary>La plus haute fréquence détaillée à <paramref name="largeur"/>×<paramref name="hauteur"/>, arrondie au
    /// hertz ; null si l'EDID est illisible ou ne détaille pas cette résolution.</summary>
    public static int? MaxA(ReadOnlySpan<byte> edid, int largeur, int hauteur)
    {
        if (edid.Length < Bloc || !edid[..8].SequenceEqual(Entete)) return null;
        double? max = null;
        void Voir(ReadOnlySpan<byte> d)
        {
            if (Frequence(d, largeur, hauteur) is { } f && (max is null || f > max)) max = f;
        }

        foreach (var off in new[] { 54, 72, 90, 108 }) Voir(edid.Slice(off, 18));
        for (var i = 1; i <= edid[126] && Bloc * (i + 1) <= edid.Length; i++)
        {
            var ext = edid.Slice(Bloc * i, Bloc);
            if (ext[0] != 0x02 || ext[2] < 4) continue;
            for (var p = (int)ext[2]; p + 18 <= Bloc - 1; p += 18) Voir(ext.Slice(p, 18));
        }
        return max is { } m ? (int)Math.Round(m) : null;
    }

    /// <summary>La résolution native : celle du premier timing détaillé, que la norme réserve au mode préféré (VESA
    /// E-EDID 1.4, §3.10.1). null si l'EDID est illisible ou que ce descripteur n'est pas un timing.</summary>
    public static (int Largeur, int Hauteur)? Natif(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < Bloc || !edid[..8].SequenceEqual(Entete)) return null;
        var d = edid.Slice(54, 18);
        if ((d[0] | d[1]) == 0) return null;
        return (d[2] | ((d[4] & 0xF0) << 4), d[5] | ((d[7] & 0xF0) << 4));
    }

    // Un descripteur de timing détaillé (VESA E-EDID 1.4, §3.10.2) : horloge pixel en 10 kHz, actif et blanking sur 12 bits.
    // Horloge nulle = descripteur d'affichage (nom, limites), pas un timing ; un mode entrelacé (bit 7 de l'octet 17) est
    // laissé.
    private static double? Frequence(ReadOnlySpan<byte> d, int largeur, int hauteur)
    {
        var horloge = (d[0] | (d[1] << 8)) * 10_000.0;
        if (horloge == 0 || (d[17] & 0x80) != 0) return null;
        int ha = d[2] | ((d[4] & 0xF0) << 4), hb = d[3] | ((d[4] & 0x0F) << 8);
        int va = d[5] | ((d[7] & 0xF0) << 4), vb = d[6] | ((d[7] & 0x0F) << 8);
        if (ha != largeur || va != hauteur || ha + hb == 0 || va + vb == 0) return null;
        return horloge / ((ha + hb) * (double)(va + vb));
    }
}

/// <summary>
/// L'écran annonce plus que ce que Windows propose. La fréquence se règle dans Windows ;
/// quand Windows ne la propose même pas, quelque chose bride : d'abord le menu de l'écran (version DisplayPort ou HDMI,
/// overclock coupé), puis le câble, le port ou le pilote. La version du câble ne se lit pas : on dit
/// comment Windows voit le branchement, pas le câble. Une dalle de portable n'a pas de câble ; des écrans dupliqués
/// n'offrent que les modes communs ; un EDID illisible est un « non lu » compté.
/// </summary>
public static class EcranBride
{
    // Arrondis de part et d'autre (143,98 Hz lu « 143 » par Windows) : un écart d'un hertz n'est pas une bride.
    private const int Tolerance = 2;
    // D3DKMDT_VIDEO_OUTPUT_TECHNOLOGY (d3dkmdt.h) : 11 = DisplayPort embarqué ; le bit haut = sortie interne (dalle).
    private const uint Interne = 0x80000000;

    /// <summary>La décision complète pour un écran : EDID lu, résolution native, fréquence que Windows propose à cette
    /// résolution. null = rien à dire.</summary>
    public static RepairFinding? Comparer(byte[]? edid, Func<int, int, int> maxWindowsA, uint? sortie, bool duplique)
    {
        if (duplique) return null;
        if (edid is null || EdidFrequences.Natif(edid) is not { } natif)
            return NonLu(Docteur.T("EB.EdidNonLu"));
        var (l, h) = natif;
        if (EdidFrequences.MaxA(edid, l, h) is not { } max)
            return NonLu(Docteur.T("EB.EdidSansFrequences", l, h));
        return Juger(maxWindowsA(l, h), max, sortie, $"{l}×{h}");
    }

    private static RepairFinding NonLu(string pourquoi)
        => new("DISPLAY", "fréquence annoncée par l'écran", null, null, RepairStatus.Unknown, pourquoi);

    /// <param name="resolution">La résolution comparée, dite telle quelle (« 1920×1080 ») ; null = « à cette résolution ».</param>
    public static RepairFinding? Juger(int maxWindows, int? maxEdid, uint? sortie, string? resolution = null)
    {
        if (maxWindows <= 0 || maxEdid is not { } edid || edid - maxWindows < Tolerance) return null;
        var ou = resolution is null ? Docteur.T("EB.ACetteResolution") : Docteur.T("EB.EnResolution", resolution);
        var dalle = sortie is { } so && (so == 11 || (so & Interne) != 0);
        return new RepairFinding("DISPLAY", "fréquence annoncée par l'écran", Docteur.T("EB.Valeur", edid, maxWindows),
            $"{edid} Hz", RepairStatus.Suspicious,
            dalle
                ? Docteur.T("EB.Dalle", edid, ou, maxWindows)
                : Docteur.T("EB.Ecran", edid, ou, maxWindows)
                  + (Nom(sortie) is { } n ? Docteur.T("EB.Branche", n) : "")
                  + Docteur.T("EB.Conseil"),
            RepairAction.Recommandation);
    }

    private static string? Nom(uint? sortie) => sortie switch
    {
        0 => "VGA",
        4 => "DVI",
        5 => "HDMI",
        10 => "DisplayPort",
        _ => null,
    };
}
