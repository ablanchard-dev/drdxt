using System.Runtime.Versioning;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Display;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>Carte graphique et écran : pilotes, lectures AMD, températures et fréquence de l'écran.</summary>
public sealed partial class RepairAuditService
{
    // Les cartes vues par Windows, avec la mémoire qu'il leur prête (AdapterRAM plafonne à 4 Go : assez pour séparer une
    // puce intégrée d'une carte dédiée).
    private IReadOnlyList<GpuInfo> CartesLues()
    {
        var r = _wmi.Query("SELECT Name, AdapterRAM FROM Win32_VideoController");
        return r.IsOk
            ? r.Value!.Where(l => l.GetString("Name") is not null)
                .Select(l => new GpuInfo(l.GetString("Name")!, "", Convert.ToInt64(l.Properties.GetValueOrDefault("AdapterRAM") ?? 0L,
                    CultureInfo.InvariantCulture), "", TriState.Unknown)).ToList()
            : [];
    }

    // Les constats AMD détaillés, appelés seulement porte ouverte (LecturesAmdProfondes.Juger). Les registres lus sont ceux
    // que le kit de validation lit (canaux UMC, S5_RESET_STATUS) : un rapport validé couvre ce qu'il ouvre. La table PM n'est
    // pas lue ici : le kit ne la lit pas encore, son constat dit « non lue ».
    private IEnumerable<RepairFinding> LecturesAmdDetaillees(GenerationAmd.Ligne generation)
    {
        var barrettes = _wmi.QueryInNamespace(@"\\.\root\cimv2", "SELECT SMBIOSMemoryType, Speed FROM Win32_PhysicalMemory");
        var types = barrettes.IsOk ? barrettes.Value!.Select(b => b.GetInt("SMBIOSMemoryType") ?? 0).ToList() : [];
        var vitesseKit = barrettes.IsOk ? barrettes.Value!.Select(b => b.GetInt("Speed") ?? 0).DefaultIfEmpty(0).Min() : 0;
        // Le réglé contre l'appliqué : le BIOS nommé au dernier scan, contre les timings du contrôleur.
        var bios = DrDxt.Detection.Cpu.ChaleurEnPartie.LireBios(DossierDonnees.Chemin(ChaleurEnPartie.FichierBios),
            DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur)?.Reglages;
        return ConstatsAmdProfonds(generation, types, vitesseKit, bios, WmiFixe(RequeteCarte, "Product"),
            WmiFixe(RequeteBios, "SMBIOSBIOSVersion"), LecturesAmd.CanauxMemoire, DrDxt.GpuNative.PawnIo.PawnIoAmd.EtatDuReset);
    }

    /// <summary>Couture de test : les constats depuis leurs entrées, les registres par <paramref name="canaux"/> et
    /// <paramref name="reset"/> (PawnIO en vrai, null = non lu).</summary>
    internal static IEnumerable<RepairFinding> ConstatsAmdProfonds(GenerationAmd.Ligne generation, IReadOnlyList<int> typesSmbios,
        int vitesseKit, IReadOnlyList<BiosSettingReading>? bios, string? carte, string? versionBios,
        Func<IReadOnlyList<(int Canal, IReadOnlyDictionary<uint, uint> Registres)>?> canaux, Func<uint?> reset)
    {
        var lus = canaux();
        var appliques = TimingsMemoireAmd.Type(typesSmbios) is { } type && lus is not null ? TimingsMemoireAmd.Decoder(type, lus) : null;
        return [.. TimingsMemoireAmd.Juger(generation, typesSmbios, lus),
            .. RegleContreApplique.Juger(generation, bios, appliques, vitesseKit),
            .. TablePmAmd.Juger(generation, bridage: null),
            .. TablesBiosAmd.Juger(generation, carte, versionBios),
            .. ResetAmd.Juger(generation, reset())];
    }

    // Les pilotes installés (Win32_PnPSignedDriver) ; null = non lu. Une date ou une signature illisible reste null.
    private IReadOnlyList<InventaireDesPilotes.Pilote>? PilotesLus()
    {
        var r = _wmi.Query("SELECT DeviceClass, DeviceName, DriverProviderName, DriverDate, IsSigned, InfName FROM Win32_PnPSignedDriver");
        if (!r.IsOk) return null;
        return r.Value!.Select(l => new InventaireDesPilotes.Pilote(l.GetString("DeviceClass") ?? "?", l.GetString("DeviceName") ?? "?",
            l.GetString("DriverProviderName"), InventaireDesPilotes.DateDmtf(l.GetString("DriverDate")),
            l.Properties.GetValueOrDefault("IsSigned") is bool b ? b : null, l.GetString("InfName"))).ToList();
    }

    // Chaque carte d'affichage avec ses identifiants matériels et compatibles ; null = non lu.
    private IReadOnlyList<InstallationDePilote.Carte>? CartesEtIdentifiants()
    {
        var r = _wmi.Query("SELECT Name, PNPDeviceID, HardwareID, CompatibleID FROM Win32_PnPEntity WHERE PNPClass = 'Display'");
        if (!r.IsOk) return null;
        static IEnumerable<string> Ids(object? v) => v is Array a ? a.Cast<object?>().Select(x => x?.ToString()).OfType<string>() : [];
        return r.Value!.Select(l => new InstallationDePilote.Carte(l.GetString("Name") ?? "?",
            [.. Ids(l.Properties.GetValueOrDefault("HardwareID")), .. Ids(l.Properties.GetValueOrDefault("CompatibleID"))],
            l.GetString("PNPDeviceID"))).ToList();
    }

    // ── Affichage ──────────────────────────────────────────────────────────────────────────
    // Une resolution non native n'est pas une erreur. Beaucoup de joueurs competitifs la choisissent
    // deliberement, pour gagner des FPS ou pour l'etirement de l'image. Le dire autrement serait
    // accuser un choix.
    /// <summary>Pur : la fréquence de l'écran de jeu contre son maximum. Une fréquence non lue (0 : Win32 rend la valeur par
    /// défaut du matériel) se dit non lue : comparée au maximum, elle donnait « tourne à 0 Hz » avec une recommandation.</summary>
    internal static RepairFinding FrequenceDeLEcran(DrDxt.Application.Tweaks.Hardware.DisplayInfo e)
        => e.RefreshHz <= 0
            ? new RepairFinding("DISPLAY", "frequence", null, e.MaxRefreshHz > 0 ? $"{e.MaxRefreshHz} Hz" : null, RepairStatus.Unknown,
                Docteur.T("RA.Ecran.FrequenceNonLue"))
            : e.MaxRefreshHz == 0
                ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", null, RepairStatus.Observed,
                    Docteur.T("RA.Ecran.MaxNonLu"))
                : e.RefreshHz >= e.MaxRefreshHz
                    ? new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                        RepairStatus.ExpectedDefault, Docteur.T("RA.Ecran.AuMax"))
                    : new RepairFinding("DISPLAY", "frequence", $"{e.RefreshHz} Hz", $"{e.MaxRefreshHz} Hz",
                        RepairStatus.NonDefault,
                        Docteur.T("RA.Ecran.Bride", e.MaxRefreshHz, e.RefreshHz),
                        RepairAction.Recommandation);

    private IEnumerable<RepairFinding> Display()
    {
        var ecran = new Win32DisplayInfo().GetPrimaryGameDisplay();
        if (!ecran.IsOk || ecran.Value is null)
        {
            yield return new RepairFinding("DISPLAY", "ecran principal", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Ecran.NonLu"));
            yield break;
        }

        var e = ecran.Value;
        yield return FrequenceDeLEcran(e);

        // Ce que l'écran annonce dans son EDID, contre ce que Windows propose.
        // Comparé à la résolution native : c'est elle que le câble transporte, même quand le joueur joue en étiré (1728×1080
        // relevé le 06/10 sur l'écran principal de la machine de référence, que l'EDID ne détaille pas).
        if (e.DeviceName is { } nom
            && EcranBride.Comparer(EdidDuMoniteur.Lire(e.NomInterface), (l, h) => Win32DisplayInfo.MaxRefreshAt(nom, l, h),
                Sortie(EdidDuMoniteur.Instance(e.NomInterface)), Win32DisplayInfo.Duplique(nom)) is { } bride)
            yield return bride;
    }

    // Le branchement du moniteur (HDMI, DisplayPort…), lu dans WmiMonitorConnectionParams ; null = non lu.
    private uint? Sortie(string? instance)
    {
        if (instance is null) return null;
        var r = _wmi.QueryInNamespace(@"\\.\root\wmi", "SELECT InstanceName, VideoOutputTechnology FROM WmiMonitorConnectionParams");
        if (!r.IsOk) return null;
        var ligne = r.Value!.FirstOrDefault(l => l.GetString("InstanceName")?.StartsWith(instance, StringComparison.OrdinalIgnoreCase) == true);
        return ligne?.Properties.TryGetValue("VideoOutputTechnology", out var v) == true && v is not null
            ? Convert.ToUInt32(v, System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    // ── Pilote GPU ─────────────────────────────────────────────────────────────────────────
    private static IEnumerable<RepairFinding> GpuDriver()
    {
        var ull = Dword(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode");
        yield return ull is null
            ? new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode", Docteur.T("RA.Nv.Absent"), Docteur.T("RA.Nv.Absent"),
                RepairStatus.ExpectedDefault, Docteur.T("RA.Nv.AbsentExplication"))
            : new RepairFinding("GPU DRIVER", "Valeur de registre NVTweak LowLatencyMode",
                ull switch { 0 => "Off", 1 => "On", 2 => "Ultra", _ => ull.ToString() }, Docteur.T("RA.Nv.Absent"),
                RepairStatus.NonDefault, Docteur.T("RA.Nv.PoseeExplication"),
                // Meme motif que POWER : sans geste, ce constat n'atteindrait jamais l'ecran.
                RepairAction.Recommandation);
    }

    private IEnumerable<RepairFinding> ThermiqueGpu()
    {
        // HWiNFO publie le capteur du paquet quand il tourne avec sa mémoire partagée. Lu là,
        // c'est une vraie température, nommée avec sa source ; sans lui, la phrase d'avant reste vraie.
        if (_temperatureHwinfo() is { } hw)
        {
            var c = hw.Celsius.ToString("0", CultureInfo.InvariantCulture) + " °C";
            // Le seuil vient d'Intel (freinage à 100 °C). Un Ryzen 7000 (« CPU (Tctl/Tdie) »)
            // tourne à 95 °C par conception : pour lui, aucun jugement, la valeur seule.
            // La lecture par les
            // registres est Intel seulement, elle se juge comme le paquet ; et elle ne vient pas de HWiNFO.
            var parRegistres = hw.Libelle == DrDxt.Detection.Cpu.TemperaturePawnIo.Libelle;
            var chaudIntel = (parRegistres || hw.Libelle is "CPU Package" or "Core Max") && hw.Celsius >= SeuilChaleurProcesseurC;
            var aLaLimite = chaudIntel && hw.Celsius >= 100;
            yield return new RepairFinding("THERMIQUE", "temperature du processeur", c, null,
                chaudIntel ? RepairStatus.Suspicious : RepairStatus.Observed,
                (parRegistres ? Docteur.T("RA.Temp.Registres") : Docteur.T("RA.Temp.Hwinfo", hw.Libelle))
                + Docteur.T("RA.Temp.AuScan", c)
                + Docteur.T(aLaLimite ? "RA.Temp.Limite" : chaudIntel ? "RA.Temp.PresLimite" : "RA.Temp.EnPartie"));
            foreach (var r in ThermiqueCarte()) yield return r;
            yield break;
        }

        var zone = WmiDans(@"\\.\root\WMI", "SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature",
            "CurrentTemperature");
        yield return new RepairFinding("THERMIQUE", "temperature du processeur", null, null,
            RepairStatus.Unknown,
            // Sans droits, ni la zone ACPI ni HWiNFO n'ont répondu ; la suite (pourquoi une zone ACPI ne suffirait pas)
            // reste vraie dans les deux cas.
            (!_eleve() ? Docteur.T("RA.Temp.SansDroits")
            : zone is null
                ? Docteur.T("RA.Temp.AucuneZone")
                : Docteur.T("RA.Temp.Zone",
                  // Lu et ecrit en invariant. En culture courante, une machine francaise ecrirait
                  // « 29,9 °C » dans le rapport que le client colle dans son ticket, qu'un test promet
                  // identique partout ; et une valeur WMI a point decimal ne se lirait pas en francais,
                  // la chaine brute s'afficherait a la place de la temperature.
                  double.TryParse(zone, NumberStyles.Float, CultureInfo.InvariantCulture, out var dk)
                      ? (dk / 10 - 273.15).ToString("0.0", CultureInfo.InvariantCulture) + " °C"
                      : zone))
            + Docteur.T("RA.Temp.ZoneFabriquee")
            + PourquoiHwinfoNePubliePas());

        foreach (var r in ThermiqueCarte()) yield return r;
    }

    /// <summary>HWiNFO tourne mais ne publie rien. Chez moi, c'était une version de 2012 ; la
    /// case « Shared Memory Support » non cochée, ou décochée au bout de 12 heures dans la version gratuite (forum
    /// HWiNFO, depuis la v7.00 ; aucune source officielle), en sont les autres causes. Dites comme causes possibles,
    /// jamais détectées. HWiNFO absent, ou ce service sans lecteur (tests) : rien.</summary>
    private string PourquoiHwinfoNePubliePas()
    {
        string? version;
        bool publie;
        try { version = _hwinfoQuiTourne?.Invoke(); publie = _hwinfoPublie?.Invoke() ?? false; }
        catch (Exception) { return ""; }
        if (version is null) return "";
        // Une version illisible ne s'écrit pas « ? ».
        var nom = version == "?" ? "HWiNFO" : $"HWiNFO {version}";
        return publie
            ? Docteur.T("RA.Hwinfo.PublieSansTemp", nom)
            : Docteur.T("RA.Hwinfo.NePubliePas", nom);
    }

    /// <summary>Au-dessus, la carte dit « près de la limite ». Intel freine ses processeurs récents
    /// à 100 °C (TjMax) ; 95 °C laisse la marge d'un relevé ponctuel. **Non chiffré** au-delà de cette source.</summary>
    internal const double SeuilChaleurProcesseurC = 95;

    private IEnumerable<RepairFinding> ThermiqueCarte()
    {

        var gpu = new NvmlHardwareProbe().Lire();
        if (gpu.GpuTemperatureC is null && TemperatureAdlx(CartesLues(), _amd.Value) is { } amd)
        {
            yield return amd;
            yield break;
        }
        if (gpu.GpuTemperatureC is null && TemperatureIntel(CartesLues(), _intel.Value) is { } intel)
        {
            yield return intel;
            yield break;
        }
        yield return gpu.GpuTemperatureC is { } tGpu
            ? new RepairFinding("THERMIQUE", "temperature du GPU", tGpu.ToString("0") + " °C", null,
                RepairStatus.Observed,
                // « publie sa température (55,2 W) » collait des watts a une phrase sur la
                // temperature (relu dans le rapport reel). La puissance est dite pour ce qu'elle est.
                Docteur.T("RA.Gpu.Nvml")
                + (gpu.GpuPowerW is { } w ? Docteur.T("RA.Gpu.Consommait", w) : "")
                + ".")
            : new RepairFinding("THERMIQUE", "temperature du GPU", null, null, RepairStatus.Unknown,
                TemperatureCarteNonLue(CartesLues(), _amd.Value, _intel.Value.Raison));
    }

    // La carte Intel vue par Windows (la plus grosse mémoire : une Arc passe devant la puce intégrée), quand ni NVIDIA ni
    // AMD n'est là : celles-là ont leur propre bibliothèque, lue d'abord.
    private static GpuInfo? IntelSeule(IReadOnlyList<GpuInfo> cartes)
        => cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                           || c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) ? null
            : cartes.Where(c => c.Model.Contains("Intel", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);

    /// <summary>La température d'une carte Intel lue par IGCL (ctlTemperatureGetState, sonde GPU) ; null si
    /// pas de carte Intel seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureIntel(IReadOnlyList<GpuInfo> cartes, (double? Celsius, string? Raison) intel)
        => IntelSeule(cartes) is not null && intel.Celsius is > 0 and < 150 and { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, Docteur.T("RA.Gpu.Igcl"))
            : null;

    // La carte AMD dédiée vue par Windows (la plus grosse mémoire : une APU liste sa puce intégrée d'abord), quand
    // aucune carte NVIDIA n'est là.
    private static GpuInfo? RadeonSansNvidia(IReadOnlyList<GpuInfo> cartes)
        => cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) ? null
            : cartes.Where(c => c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);

    /// <summary>La température d'une carte AMD lue par ADLX (IADLXGPUMetrics.GPUTemperature) ; null si pas de
    /// carte AMD seule ou température non lue.</summary>
    internal static RepairFinding? TemperatureAdlx(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
        => RadeonSansNvidia(cartes) is not null && amd.TemperatureC is { } t
            ? new RepairFinding("THERMIQUE", "temperature du GPU", t.ToString("0", CultureInfo.InvariantCulture) + " °C", null,
                RepairStatus.Observed, Docteur.T("RA.Gpu.Adlx"))
            : null;

    /// <summary>L'état d'Anti-Lag lu par ADLX sur une carte AMD. Rien quand il n'est pas lu : la ligne de la
    /// limite d'images dit déjà ce que DrDXT n'a pas pu lire.</summary>
    internal static IEnumerable<RepairFinding> AntiLagAmd(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (RadeonSansNvidia(cartes) is not { } carte || amd.AntiLag is not { } actif) yield break;
        yield return new RepairFinding("LATENCE", "Anti-Lag de la carte", Docteur.T(actif ? "RA.AntiLag.Active" : "RA.AntiLag.Desactive"), null,
            RepairStatus.Observed, Docteur.T("RA.AntiLag.Explication", carte.Model));
    }

    // La limite d'images (VRR, cap) se règle par le pilote NVIDIA, et se lit par ADLX sur une Radeon. Sur une carte AMD
    // ou Intel sans carte NVIDIA, le silence passerait pour « rien à régler » : sans lecture, c'est un « non lu », dit.
    internal static IEnumerable<RepairFinding> LimiteImagesHorsNvidia(IReadOnlyList<GpuInfo> cartes,
        DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd)
    {
        if (cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))) yield break;
        // Une Intel Arc reçoit le même « non lu ».
        if (cartes.Where(c => (c.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                               || c.Model.Contains("Arc", StringComparison.Ordinal)) && c.VramBytes >= 2L << 30)
                .MaxBy(c => c.VramBytes) is not { } carte)
            yield break;
        var radeon = carte.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
        var vrr = Docteur.T("RA.Vrr", Docteur.T(radeon ? "RA.Logiciel.Amd" : "RA.Logiciel.Intel"));
        // Sur une Radeon, la limite FRTC / Chill lue par ADLX remplace le « non lu ».
        if (radeon && amd.LimiteImages is { } fps)
        {
            yield return new RepairFinding("LATENCE", "limite d'images de la carte", fps == 0 ? Docteur.T("RA.Fps.Aucune") : Docteur.T("RA.Fps.Valeur", fps), null,
                RepairStatus.Observed,
                Docteur.T("RA.Fps.LueAdlx", carte.Model)
                + Docteur.T(fps == 0 ? "RA.Fps.AucuneLimite" : amd.ChillActif == true && amd.ChillMaxFps == fps ? "RA.Fps.Chill" : "RA.Fps.Frtc")
                + vrr);
            yield break;
        }
        yield return new RepairFinding("LATENCE", "limite d'images de la carte", null, null, RepairStatus.Unknown,
            (radeon
                ? Docteur.T("RA.Fps.NonLueAmd", carte.Model)
                  + (amd.Raison is { } r ? Docteur.T("RA.Raison", r.TrimEnd('.')) : "") + ". "
                : Docteur.T("RA.Fps.PasNvidia", carte.Model))
            + vrr);
    }

    internal static string TemperatureCarteNonLue(IReadOnlyList<GpuInfo> cartes, DrDxt.Detection.Gpu.Adlx.AdlxReglagesLus amd,
        string? intel = null)
    {
        // APU + Radeon dédiée, Windows rend la puce intégrée d'abord : la plus grosse mémoire.
        var autre = cartes.Where(c => !c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)).MaxBy(c => c.VramBytes);
        return cartes.Any(c => c.Model.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) || autre is null
            ? Docteur.T("RA.TempGpu.Nvml")
            // Une Radeon passe par ADLX ; la raison de l'échec est dite.
            : autre.Model.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                ? Docteur.T("RA.TempGpu.NonLueAmd", autre.Model)
                  + (amd.Raison is { } r ? Docteur.T("RA.Raison", r.TrimEnd('.')) : "") + ". "
                  + Docteur.T("RA.TempGpu.LogicielAmd")
            // Une carte Intel passe par IGCL ; la raison de l'échec est dite.
            : autre.Model.Contains("Intel", StringComparison.OrdinalIgnoreCase)
                ? Docteur.T("RA.TempGpu.NonLueIntel", autre.Model)
                  + (intel is { } i ? Docteur.T("RA.Raison", i.TrimEnd('.')) : "") + ". "
                  + Docteur.T("RA.TempGpu.LogicielIntel")
            : Docteur.T("RA.TempGpu.Autre", autre.Model);
    }
}
