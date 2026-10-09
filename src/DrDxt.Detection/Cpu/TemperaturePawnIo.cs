using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace DrDxt.Detection.Cpu;

/// <summary>
/// PAWN1 (09/10/2026) : la température du processeur lue dans ses propres registres (MSR) par PawnIO, quand PawnIO est
/// déjà installé sur la machine. DrDXT n'installe jamais PawnIO (un pilote noyau : c'est le choix du joueur, voir le guide) ;
/// sans lui, rien n'est lu et rien n'est dit. Lecture seule : le module ne reçoit que des numéros de registres à lire.
///
/// Intel seulement pour l'instant : IA32_TEMPERATURE_TARGET (0x1A2, bits 23:16 = TjMax) et IA32_PACKAGE_THERM_STATUS
/// (0x1B1, bits 22:16 = écart sous TjMax). Température = TjMax − écart. Source : Intel SDM vol. 4. AMD : non lu (les
/// adresses SMN changent par famille ; sans machine pour les relever, « non lu » plutôt qu'un chiffre deviné).
///
/// Le module IntelMSR.bin est celui de namazso/PawnIO.Modules (LGPL-2.1+, licence dans third_party/pawnio-modules),
/// embarqué tel quel et figé par son empreinte : un module changé sans relecture ne se charge pas.
/// </summary>
public static class TemperaturePawnIo
{
    public const string Libelle = "PawnIO (registres du processeur)";
    public const string Empreinte = "d6ed85d65ab17a22f813ef98207d6d537155ee2ded5976a21cb48413c9b92e5f";
    private const uint MsrTemperatureTarget = 0x1A2;
    private const uint MsrPackageThermStatus = 0x1B1;

    private static readonly string Lib = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO", "PawnIOLib.dll");

    private static readonly object Verrou = new();
    private static bool _resolveurPose;
    // Revue du 09/10/2026 : une poignée ouverte et un module chargé une fois par processus, pas à chaque échantillon ; une
    // lecture qui échoue ferme la poignée, la suivante rouvre.
    private static nint _poignee;

    /// <summary>La bibliothèque n'est pas à côté de l'App : elle se résout vers l'installation de PawnIO, et nulle part
    /// ailleurs (revue du 09/10 : jamais de repli sur la recherche par défaut, dossier de l'App ou PATH). Posé au premier
    /// usage, sous le try de l'appelant : un second résolveur sur l'assembly ne ferait pas tomber l'App.</summary>
    private static void AssurerResolveur()
    {
        lock (Verrou)
        {
            if (_resolveurPose) return;
            NativeLibrary.SetDllImportResolver(typeof(TemperaturePawnIo).Assembly,
                (nom, _, _) => nom == "PawnIOLib.dll" ? NativeLibrary.Load(Lib) : IntPtr.Zero);
            _resolveurPose = true;
        }
    }

    /// <summary>Lit avec le module chargé (ouvert une fois) ; toute panne ferme la poignée et rend null.</summary>
    private static T? AvecModule<T>(Func<nint, T?> lire) where T : class
    {
        lock (Verrou)
        {
            try
            {
                AssurerResolveur();
                if (_poignee == 0)
                {
                    var module = Module();
                    if (module is null) return null;
                    if (pawnio_open(out var h) != 0) return null;
                    if (pawnio_load(h, module, (nuint)module.Length) != 0) { pawnio_close(h); return null; }
                    _poignee = h;
                }
                var lu = lire(_poignee);
                if (lu is null) Fermer();
                return lu;
            }
            catch (Exception) { Fermer(); return null; }
        }
    }

    private static void Fermer()
    {
        if (_poignee == 0) return;
        try { pawnio_close(_poignee); } catch (Exception) { }
        _poignee = 0;
    }

    /// <summary>PawnIO est-il installé ici ? (sa bibliothèque est là ; DrDXT ne l'installe pas).</summary>
    public static bool Installe() => File.Exists(Lib);

    /// <summary>Le module ne connaît que les registres Intel : ailleurs, rien n'est lu.</summary>
    public static bool ProcesseurIntel()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return k?.GetValue("VendorIdentifier") as string == "GenuineIntel";
        }
        catch (Exception) { return false; }
    }

    public static TemperatureHwinfo.Lecture? Lire()
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecModule(h =>
        {
            var tjmax = LireMsr(h, MsrTemperatureTarget);
            var pkg = LireMsr(h, MsrPackageThermStatus);
            return tjmax is null || pkg is null ? null : Decoder(tjmax.Value, pkg.Value);
        });
    }

    private const uint MsrRaplPowerUnit = 0x606;
    private const uint MsrPkgPowerLimit = 0x610;
    private const uint MsrPkgPowerInfo = 0x614;

    /// <summary>J-MECA (09/10/2026) : PL1, PL2 et la puissance de base du processeur Intel, lus dans ses registres. Mêmes
    /// conditions que la température : PawnIO installé, processeur Intel, lecture seule ; sinon null.</summary>
    public static DrDxt.Core.LimiteDePuissanceLue? LireLimites()
    {
        if (!Installe() || !ProcesseurIntel()) return null;
        return AvecModule(h =>
        {
            var unites = LireMsr(h, MsrRaplPowerUnit);
            var limites = LireMsr(h, MsrPkgPowerLimit);
            var info = LireMsr(h, MsrPkgPowerInfo);
            return unites is null || limites is null || info is null ? null : DecoderLimites(unites.Value, limites.Value, info.Value);
        });
    }

    /// <summary>Pur (Intel SDM vol. 4) : 0x606 bits 3:0 = unité de puissance (1/2^n W) ; 0x610 bits 14:0 = PL1, 15 = PL1
    /// activée, 46:32 = PL2, 63 = verrou ; 0x614 bits 14:0 = puissance de base. Une base nulle ou une PL1 nulle : registres
    /// vides, null. Revue du 09/10 : une PL1 au maximum du registre (0x7FFF ⇒ 4 095,875 W) n'est pas une lecture ratée,
    /// c'est la carte qui dit « sans limite » ; elle est rendue telle quelle, le constat la juge.</summary>
    public static DrDxt.Core.LimiteDePuissanceLue? DecoderLimites(ulong raplPowerUnit, ulong pkgPowerLimit, ulong pkgPowerInfo)
    {
        var unite = 1.0 / (1 << (int)(raplPowerUnit & 0xF));
        var pl1Brut = pkgPowerLimit & 0x7FFF;
        var pl1 = pl1Brut * unite;
        var pl2 = ((pkgPowerLimit >> 32) & 0x7FFF) * unite;
        var nominale = (pkgPowerInfo & 0x7FFF) * unite;
        if (pl1 <= 0 || nominale <= 0 || nominale > 2000) return null;
        return new DrDxt.Core.LimiteDePuissanceLue(pl1, pl2, nominale, ((pkgPowerLimit >> 63) & 1) == 1,
            Pl1Active: ((pkgPowerLimit >> 15) & 1) == 1, RegistreAuMaximum: pl1Brut == 0x7FFF);
    }

    /// <summary>Pur : TjMax (0x1A2) et l'état thermique du paquet (0x1B1) ⇒ la température, ou null si les registres ne
    /// disent rien de plausible (TjMax hors 50-120 °C, ou écart au-dessus de TjMax).</summary>
    public static TemperatureHwinfo.Lecture? Decoder(ulong temperatureTarget, ulong packageThermStatus)
    {
        var tjmax = (int)((temperatureTarget >> 16) & 0xFF);
        var ecart = (int)((packageThermStatus >> 16) & 0x7F);
        if (tjmax is < 50 or > 120 || ecart > tjmax) return null;
        return new TemperatureHwinfo.Lecture(tjmax - ecart, Libelle);
    }

    private static byte[]? Module()
    {
        using var flux = typeof(TemperaturePawnIo).Assembly.GetManifestResourceStream("DrDxt.Detection.Cpu.PawnIO.IntelMSR.bin");
        if (flux is null) return null;
        using var ms = new MemoryStream();
        flux.CopyTo(ms);
        var octets = ms.ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(octets)) == Empreinte ? octets : null;
    }

    private static ulong? LireMsr(nint h, uint msr)
    {
        var sortie = new ulong[1];
        return pawnio_execute(h, "ioctl_read_msr", [msr], 1, sortie, 1, out _) == 0 ? sortie[0] : null;
    }

    [DllImport("PawnIOLib.dll")] private static extern int pawnio_open(out nint h);
    [DllImport("PawnIOLib.dll")] private static extern int pawnio_load(nint h, byte[] blob, nuint size);
    [DllImport("PawnIOLib.dll", CharSet = CharSet.Ansi)]
    private static extern int pawnio_execute(nint h, string name, ulong[] inb, nuint inSize, ulong[] outb, nuint outSize, out nuint ret);
    [DllImport("PawnIOLib.dll")] private static extern int pawnio_close(nint h);
}
