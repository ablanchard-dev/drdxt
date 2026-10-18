using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DrDxt.GpuNative.PawnIo;

/// <summary>
/// La frontière PawnIO pour les registres Intel (MSR) et la fenêtre MCHBAR, dans le projet natif commun : lue par la
/// détection (température, limites de puissance) et écrite par le helper élevé (B1 : la limite de puissance
/// PL1 montée à chaud). Une seule copie des modules, des empreintes et des appels, jamais un double qui diverge.
///
/// PawnIO n'est jamais installé par DrDXT (un pilote noyau : le choix du joueur) ; sans lui rien n'est lu ni écrit. Les
/// modules sont ceux de namazso/PawnIO.Modules, publication 0.2.11 (LGPL-2.1+, third_party/pawnio-modules), embarqués
/// tels quels et figés par leur empreinte : un module changé sans relecture ne se charge pas. IntelMSR n'accepte d'écrire
/// que ses registres de puissance et de tension (sa propre liste) ; IntelMCHBAR est en lecture seule.
/// </summary>
public static class PawnIoIntel
{
    public const string EmpreinteMsr = "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";
    public const string EmpreinteMchbar = "3f82b832d99b4aac37d2a20fdb7c9baa2a3bc0488612c9019c9484eb0e8a6eae";

    public const uint MsrTemperatureTarget = 0x1A2;
    public const uint MsrPackageThermStatus = 0x1B1;
    public const uint MsrThermStatus = 0x19C;
    public const uint MsrRaplPowerUnit = 0x606;
    public const uint MsrPkgPowerLimit = 0x610;
    public const uint MsrPkgPowerInfo = 0x614;
    /// <summary>PACKAGE_RAPL_LIMIT dans la fenêtre MCHBAR (Intel datasheet 12e gén. vol. 2, décalage 59A0h) : la copie de
    /// 0x610 que la carte mère tient ; même format 64 bits. La limite effective est la plus basse des deux.</summary>
    public const uint DecalageMchbarLimite = 0x59A0;

    private static readonly string Lib = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

    private static readonly object Verrou = new();
    private static bool _resolveurPose;
    // Une poignée par module, ouverte une fois par processus ; une panne la ferme, l'appel suivant rouvre.
    private static nint _poigneeMsr;
    private static nint _poigneeMchbar;

    /// <summary>PawnIO est-il installé ici ? (sa bibliothèque est là ; DrDXT ne l'installe pas).</summary>
    public static bool Installe() => File.Exists(Lib);

    /// <summary>Les modules ne connaissent que les registres Intel : ailleurs, rien n'est lu.</summary>
    public static bool ProcesseurIntel()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("VendorIdentifier") as string == "GenuineIntel";
        }
        catch (Exception) { return false; }
    }

    /// <summary>Un registre MSR, ou null (PawnIO absent, pas Intel, registre hors liste du module, panne).</summary>
    public static ulong? LireMsr(uint msr)
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecMsr(h => (object?)LireMsr(h, msr)) is ulong v ? v : null;
    }

    /// <summary>Un registre par cœur (0x19C) n'a de sens que lu sur un cœur connu. Le fil est posé sur le
    /// premier cœur le temps de la lecture, puis rendu à son affinité d'avant ; s'il ne s'y trouve pas, rien n'est lu.</summary>
    public static ulong? LireMsrPremierCoeur(uint msr)
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        var avant = SetThreadAffinityMask(GetCurrentThread(), 1);
        if (avant == 0) return null;
        try { return GetCurrentProcessorNumber() == 0 ? LireMsr(msr) : null; }
        finally { SetThreadAffinityMask(GetCurrentThread(), avant); }
    }

    /// <summary>Les trois registres bruts de la limite de puissance : unités (0x606), limite (0x610), puissances (0x614).</summary>
    public static RegistresDeLimite? LireRegistres()
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecMsr(h =>
        {
            var unites = LireMsr(h, MsrRaplPowerUnit);
            var limites = LireMsr(h, MsrPkgPowerLimit);
            var info = LireMsr(h, MsrPkgPowerInfo);
            return unites is null || limites is null || info is null ? null : new RegistresDeLimite(unites.Value, limites.Value, info.Value);
        });
    }

    /// <summary>La copie MMIO brute de la limite (MCHBAR + 0x59A0), ou null si la fenêtre ne s'ouvre pas (module refusé sur
    /// ce processeur, MCHBAR désactivée, PawnIO absent).</summary>
    public static ulong? LireCopieMmioLimite()
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecMchbar(h =>
        {
            var sortie = new ulong[1];
            return pawnio_execute(h, "ioctl_read_qword", [DecalageMchbarLimite], 1, sortie, 1, out _) == 0 ? (object)sortie[0] : null;
        }) is ulong v ? v : null;
    }

    /// <summary>Écrit la valeur entière de 0x610 et la relit : vrai seulement si le registre porte ce qui a été écrit. Un
    /// verrou (bit 63) ou une carte qui refuse se voient ici, jamais sur parole.</summary>
    public static bool EcrireLimites(ulong valeur)
    {
        if (!Installe() || !ProcesseurIntel()) return false;
        var relu = AvecMsr(h =>
        {
            if (pawnio_execute(h, "ioctl_write_msr", [MsrPkgPowerLimit, valeur], 2, [], 0, out _) != 0) return null;
            return (object?)LireMsr(h, MsrPkgPowerLimit);
        });
        return relu is ulong v && v == valeur;
    }


    public const uint MsrBoiteOc = 0x150;

    /// <summary>Un échange avec la boîte aux lettres OC (MSR 0x150) : la commande écrite, puis la réponse relue dès
    /// que le bit 63 (occupé) retombe, 50 ms au plus ; null sur toute panne. Appelé par le seul helper élevé, qui borne les
    /// commandes (lecture 0x10, écriture 0x11 du décalage de tension).</summary>
    public static ulong? EchangerBoiteOc(ulong commande)
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecMsr(h =>
        {
            if (pawnio_execute(h, "ioctl_write_msr", [MsrBoiteOc, commande], 2, [], 0, out _) != 0) return null;
            for (var i = 0; i < 50; i++)
            {
                if (LireMsr(h, MsrBoiteOc) is not { } v) return null;
                if ((v >> 63) == 0) return (object?)v;
                Thread.Sleep(1);
            }
            return null;
        }) is ulong r ? r : null;
    }

    /// <summary>Diagnostic (tests Machine) : chaque étape de l'accès MCHBAR avec son code de retour, en une ligne.</summary>
    public static string DiagnostiquerMchbar()
    {
        lock (Verrou)
        {
            try
            {
                AssurerResolveur();
                var module = Module("IntelMCHBAR", EmpreinteMchbar);
                if (module is null) return "module missing or hash mismatch";
                var ouvert = pawnio_open(out var h);
                if (ouvert != 0) return $"pawnio_open={ouvert}";
                try
                {
                    var charge = pawnio_load(h, module, (nuint)module.Length);
                    if (charge != 0) return $"pawnio_load=0x{charge:X8}";
                    var adr = new ulong[1];
                    var r1 = pawnio_execute(h, "ioctl_get_mchbar_addr", [], 0, adr, 1, out _);
                    var qw = new ulong[1];
                    var r3 = pawnio_execute(h, "ioctl_read_qword", [DecalageMchbarLimite], 1, qw, 1, out _);
                    return $"get_addr=0x{r1:X8} base=0x{adr[0]:X} ; read_qword=0x{r3:X8} 0x{qw[0]:X16}";
                }
                finally { pawnio_close(h); }
            }
            catch (Exception ex) { return "exception : " + ex.Message; }
        }
    }

    /// <summary>Les trois registres bruts ; leur arithmétique (unités, PL1, PL2, verrou, maximum) vit dans le cœur
    /// (<c>RegistreDeLimiteCpu</c>), que ce projet feuille ne référence pas.</summary>
    public sealed record RegistresDeLimite(ulong RaplPowerUnit, ulong PkgPowerLimit, ulong PkgPowerInfo);

    /// <summary>La bibliothèque n'est pas à côté de l'App : elle se résout vers l'installation de PawnIO, et nulle part
    /// ailleurs (jamais de repli sur la recherche par défaut, dossier de l'App ou PATH). Posé au premier usage, sous le try
    /// de l'appelant : un second résolveur sur l'assembly ne ferait pas tomber l'App.</summary>
    internal static void AssurerResolveur()
    {
        lock (Verrou)
        {
            if (_resolveurPose) return;
            NativeLibrary.SetDllImportResolver(typeof(PawnIoIntel).Assembly,
                (nom, _, _) => nom == "PawnIOLib.dll" ? NativeLibrary.Load(Lib) : IntPtr.Zero);
            _resolveurPose = true;
        }
    }

    private static T? AvecMsr<T>(Func<nint, T?> lire) where T : class => Avec(ref _poigneeMsr, "IntelMSR", EmpreinteMsr, lire);
    private static T? AvecMchbar<T>(Func<nint, T?> lire) where T : class => Avec(ref _poigneeMchbar, "IntelMCHBAR", EmpreinteMchbar, lire);

    private static T? Avec<T>(ref nint poignee, string nom, string empreinte, Func<nint, T?> lire) where T : class
    {
        lock (Verrou)
        {
            try
            {
                AssurerResolveur();
                if (poignee == 0)
                {
                    var module = Module(nom, empreinte);
                    if (module is null) return null;
                    if (pawnio_open(out var h) != 0) return null;
                    if (pawnio_load(h, module, (nuint)module.Length) != 0) { pawnio_close(h); return null; }
                    poignee = h;
                }
                var lu = lire(poignee);
                if (lu is null) Fermer(ref poignee);
                return lu;
            }
            catch (Exception) { Fermer(ref poignee); return null; }
        }
    }

    /// <summary>Ferme les deux poignées. Le service ne garde pas PawnIO ouvert sans demande (un pilote de
    /// lecture de registres chargé pendant toutes les parties est ce que des anti-triches refusent) ; le prochain appel
    /// rouvre.</summary>
    public static void FermerTout()
    {
        lock (Verrou)
        {
            Fermer(ref _poigneeMsr);
            Fermer(ref _poigneeMchbar);
        }
    }

    /// <summary>Une poignée est-elle ouverte ? (pour la fermeture sur inactivité du service).</summary>
    public static bool Ouvert()
    {
        lock (Verrou) return _poigneeMsr != 0 || _poigneeMchbar != 0;
    }

    private static void Fermer(ref nint poignee)
    {
        if (poignee == 0) return;
        try { pawnio_close(poignee); } catch (Exception) { /* fermeture au mieux : la poignée est oubliée dans tous les cas */ }
        poignee = 0;
    }

    /// <summary>Le module embarqué, seulement si son empreinte est celle relue à l'embarquement.</summary>
    public static byte[]? Module(string nom, string empreinte)
    {
        using var flux = typeof(PawnIoIntel).Assembly.GetManifestResourceStream($"DrDxt.GpuNative.PawnIo.{nom}.bin");
        if (flux is null) return null;
        using var ms = new MemoryStream();
        flux.CopyTo(ms);
        var octets = ms.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(octets)) == empreinte ? octets : null;
    }

    private static ulong? LireMsr(nint h, uint msr)
    {
        var sortie = new ulong[1];
        return pawnio_execute(h, "ioctl_read_msr", [msr], 1, sortie, 1, out _) == 0 ? sortie[0] : null;
    }

    [DllImport("PawnIOLib.dll")] internal static extern int pawnio_open(out nint h);
    [DllImport("PawnIOLib.dll")] internal static extern int pawnio_load(nint h, byte[] blob, nuint size);
    [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi)]
    internal static extern int pawnio_execute(nint h, string name, ulong[] inb, nuint inSize, ulong[] outb, nuint outSize, out nuint ret);
    [DllImport("PawnIOLib.dll")] internal static extern int pawnio_close(nint h);
    [DllImport("kernel32.dll")] private static extern nuint SetThreadAffinityMask(nint thread, nuint mask);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentProcessorNumber();
}
