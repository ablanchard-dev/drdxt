using System.Runtime.Versioning;
using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Display;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Settings;
using Microsoft.Win32;

namespace DrDxt.Detection.Repair;

/// <summary>Périphériques et disques : périphériques en erreur, suspension USB, usure des disques et fichier d'échange.</summary>
public sealed partial class RepairAuditService
{
    /// <summary>Au-dela de cette part d'endurance consommee, l'usure merite le regard.
    ///
    /// **seuil emprunte, pas mesure, et il manque une source.** Quatre-vingts pour cent est la
    /// convention que les constructeurs emploient pour dire « prevois le remplacement » ; aucune
    /// mesure de ce depot ne l'etaye. Il est ecrit ici plutot que noye dans une condition, et il
    /// devra etre remplace par une source ou par une mesure.</summary>
    private const int SeuilUsurePercent = 80;

    /// <summary>Les peripheriques que le Gestionnaire marque en erreur. Le tri par code
    /// vit dans <see cref="PeripheriquesEnErreur"/>.</summary>
    private IEnumerable<RepairFinding> Peripheriques()
    {
        var lu = _wmi.Query("SELECT Name, PNPDeviceID, ConfigManagerErrorCode, Present FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
        if (!lu.IsOk)
        {
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Periph.NonLu", lu.Error!.Value.Message));
            yield break;
        }

        var constats = lu.Value!
            // Un appareil absent (Present = false) n'est pas malade : Windows en garde seulement la trace.
            .Where(r => !string.Equals(r.GetString("Present"), "False", StringComparison.OrdinalIgnoreCase))
            .Select(r => PeripheriquesEnErreur.Juger(r.GetString("Name") ?? Docteur.T("RA.Periph.SansNom"),
                r.GetInt("ConfigManagerErrorCode") ?? 0, r.GetString("PNPDeviceID")))
            .OfType<RepairFinding>()
            .ToList();

        if (constats.Count == 0)
            yield return new RepairFinding("PERIPHERIQUES", "erreurs", Docteur.T("RA.Periph.Aucune"), Docteur.T("RA.Periph.Aucune"),
                RepairStatus.ExpectedDefault, Docteur.T("RA.Periph.AucunEnErreur"));
        foreach (var c in constats) yield return c;
    }

    // Constat seul, voir SuspensionUsb. Le plan actif sur secteur, puis les périphériques d'entrée
    // USB branchés (pilote HidUsb) dont le constructeur a activé la suspension. Rien n'est écrit.
    private IEnumerable<RepairFinding> SuspensionSelectiveUsb()
    {
        var plan = new PowerProvider().GetActiveSchemeGuid();
        if (plan.IsOk && Guid.TryParse(plan.Value, out var g)) yield return SuspensionUsb.JugerPlan(PowerSettingsReader.LireSuspensionUsb(g));

        var lu = _wmi.Query("SELECT PNPDeviceID, Present FROM Win32_PnPEntity WHERE Service = 'HidUsb'");
        if (!lu.IsOk) { yield return SuspensionUsb.JugerEntrees(null, 0); yield break; }
        var branches = lu.Value!
            .Where(r => !string.Equals(r.GetString("Present"), "False", StringComparison.OrdinalIgnoreCase))
            .Select(r => r.GetString("PNPDeviceID"))
            .OfType<string>()
            .ToList();
        yield return SuspensionUsb.JugerEntrees(branches.Count, branches.Count(id => SuspensionUsb.Activee(ValeurDeParametre(id))));
    }

    private static object? ValeurDeParametre(string pnpDeviceId)
    {
        try
        {
            using var cle = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{pnpDeviceId}\Device Parameters");
            return cle?.GetValue("SelectiveSuspendEnabled");
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// L'usure des disques. Un SSD qui a consomme son endurance ralentit puis lache : c'est une
    /// piece qui vieillit de facon mesurable, la seule du PC dans ce cas.
    ///
    /// La forme de la requete a ete verifiee sur la machine avant d'etre ecrite, et la forme
    /// evidente est fausse : interroger `MSFT_StorageReliabilityCounter` directement rend la classe
    /// presente et aucune instance. Voir <see cref="StorageWearReader"/>.
    /// </summary>
    private IEnumerable<RepairFinding> UsureDisques()
    {
        var lu = new StorageWearReader(_wmi).Lire();
        if (!lu.IsOk)
        {
            yield return new RepairFinding("STOCKAGE", "usure", null, null, RepairStatus.Unknown,
                Docteur.T("RA.Usure.NonLu", lu.Error!.Value.Message));
            yield break;
        }

        foreach (var d in lu.Value!)
        {
            // Les erreurs non corrigées, avant l'usure.
            if (StorageWearReader.JugerErreurs(d) is { } erreurs) yield return erreurs;
            if (StorageWearReader.JugerSante(d) is { } sante) yield return sante;
            if (d.UsurePercent is not { } usure)
            {
                yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}", null, null,
                    RepairStatus.Unknown,
                    _eleve()
                        ? Docteur.T("RA.Usure.PasDeCompteur")
                        : SansDroits);
                continue;
            }

            var merite = usure >= SeuilUsurePercent;
            yield return new RepairFinding("STOCKAGE", $"usure · {d.Disque}",
                ValeurDUsure(usure, d.TemperatureC), Docteur.T("RA.Usure.ZeroPourcent"),
                StatutDUsure(usure),
                merite
                    ? Docteur.T("RA.Usure.Merite", usure, SeuilUsurePercent)
                    : Docteur.T("RA.Usure.Lecture", usure),
                merite ? RepairAction.Recommandation : RepairAction.Aucune);
        }
    }

    /// <summary>Le seuil d'usure est une convention du constructeur : il dit quand regarder, il ne prouve aucun tort.</summary>
    public static RepairStatus StatutDUsure(int usure) => usure >= SeuilUsurePercent ? RepairStatus.NonDefault : RepairStatus.Observed;

    // La température est nommée à côté de l'usure : « 0 %, 40 °C » sous le mot « usure » laisserait le second chiffre sans nom.
    internal static string ValeurDUsure(int usure, int? temperatureC) =>
        // Une clé USB publiait « 0 °C ». Zéro n'est pas une température de
        // disque en marche : c'est une valeur non publiée, elle n'est pas dite.
        temperatureC is > 0 and { } t ? Docteur.T("RA.Usure.AvecTemperature", usure, t) : Docteur.T("RA.Usure.Seule", usure);

    // ── PAGEFILE ───────────────────────────────────────────────────────────────────────────
    // Un pagefile manuel n'est pas automatiquement mauvais : c'est un choix courant et defendable.
    // Ce qui compte est la coherence entre ce qui est configure et ce dont la machine a besoin.
    private IEnumerable<RepairFinding> Pagefile()
    {
        var auto = Wmi("SELECT AutomaticManagedPagefile FROM Win32_ComputerSystem", "AutomaticManagedPagefile");
        var alloue = Wmi("SELECT AllocatedBaseSize FROM Win32_PageFileUsage", "AllocatedBaseSize");
        var pic = Wmi("SELECT PeakUsage FROM Win32_PageFileUsage", "PeakUsage");
        yield return new RepairFinding("PAGEFILE", "gestion",
            auto is null ? null : (auto == "True" ? Docteur.T("RA.Pf.Auto") : Docteur.T("RA.Pf.Manuelle")),
            Docteur.T("RA.Pf.Auto"),
            auto == "True" ? RepairStatus.ExpectedDefault
                : auto is null ? RepairStatus.Unknown
                : RepairStatus.UserChoiceProbable,
            auto == "True"
                ? Docteur.T("RA.Pf.AutoExplication")
                : Docteur.T("RA.Pf.ManuelleExplication"));
        if (alloue is not null)
            yield return new RepairFinding("PAGEFILE", "taille / pic",
                Docteur.T("RA.Pf.Taille", alloue, pic ?? "?"), null, RepairStatus.Observed,
                Docteur.T("RA.Pf.TailleExplication"));
        yield return FichierDEchangeSurDisqueDur.Juger(EmplacementsDuFichierDEchange(out var ssdPresent), ssdPresent);
        // La mémoire pleine est le défaut de fluidité d'un PC de bureau ; lue à l'instant, dite avec ses gestes.
        yield return MemoireSaturee.Juger(Hardware.PressionMemoire.Lire(), _lecturesDures?.Invoke());
    }

    // Chaque fichier d'échange en service et le type du disque qui le porte (même relevé que les jeux).
    private static IReadOnlyList<(string Chemin, int TypeDeDisque)>? EmplacementsDuFichierDEchange(out bool? ssdPresent)
    {
        ssdPresent = null;
        try
        {
            var types = DossiersDeJeux.TypesParLettre();
            ssdPresent = SsdInterne();
            return LectureWmiBornee.Lire(() =>
            {
                using var s = new System.Management.ManagementObjectSearcher("SELECT Name FROM Win32_PageFileUsage");
                var fichiers = new List<(string, int)>();
                using var rangees = s.Get();
                foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                    using (o)
                        if (o["Name"]?.ToString() is { Length: > 1 } chemin)
                            fichiers.Add((chemin, chemin[1] == ':' && types.TryGetValue(char.ToUpperInvariant(chemin[0]), out var t) ? t : 0));
                return fichiers;
            }, () => throw new TimeoutException("WMI did not answer in time."));
        }
        catch (Exception) { return null; }
    }

    /// <summary>Un SSD interne parmi tous les disques (avec ou sans lettre) ; un SSD en USB (BusType 7)
    /// ne compte pas. Aucun SSD mais un disque au type non dit : <c>null</c>, rien n'est affirmé.</summary>
    private static bool? SsdInterne() => LectureWmiBornee.Lire<bool?>(() =>
    {
        var disques = new List<(int Type, int Bus)>();
        using (var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT MediaType, BusType FROM MSFT_PhysicalDisk"))
        using (var rangees = s.Get())
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                    disques.Add((Convert.ToInt32(o["MediaType"] ?? 0), Convert.ToInt32(o["BusType"] ?? 0)));
        return SsdInterneParmi(disques);
    }, () => throw new TimeoutException("WMI did not answer in time."));

    internal static bool? SsdInterneParmi(IReadOnlyList<(int Type, int Bus)> disques)
    {
        const int Usb = 7;
        if (disques.Any(d => d.Type == JeuxSurDisqueDur.Ssd && d.Bus != Usb)) return true;
        return disques.Any(d => d.Type is not (JeuxSurDisqueDur.Ssd or JeuxSurDisqueDur.DisqueDur)) ? null : false;
    }
}
