using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Xml.Linq;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Un demarrage mesure par Windows (evenement 100) ou une application qui l'a ralenti (101).</summary>
public sealed record MesureDemarrage(DateTimeOffset Quand, bool EstLeDemarrage, string Nom, long DureeMs, long DegradationMs);

/// <summary>Un allumage vu par le BIOS (evenement Kernel-Boot 30). <paramref name="BiosMs"/> :
/// StartOSImageStart, le moment ou le BIOS lance le chargeur de Windows ; <paramref name="ChargeurMs"/> :
/// de la jusqu'a ExitBootServicesEntry.</summary>
public sealed record TempsBios(DateTimeOffset Quand, long BiosMs, long ChargeurMs);

public interface IBootPerformanceLog
{
    /// <summary>Les evenements 100 et 101, du plus recent au plus ancien. Un echec de lecture est
    /// un `Fail`, jamais une liste vide.</summary>
    Result<IReadOnlyList<MesureDemarrage>> Lire();

    /// <summary>Les evenements Kernel-Boot 30 du journal Systeme, du plus recent au plus ancien.</summary>
    Result<IReadOnlyList<TempsBios>> LireTempsBios();
}

/// <summary>
/// Le « docteur » : ce qui ralentit le demarrage, tel que Windows l'a mesure, et non
/// une liste de programmes au demarrage suspectes d'avance. Le journal
/// Microsoft-Windows-Diagnostics-Performance/Operational porte, evenement 101, la phrase de Windows :
/// « Le démarrage de cette application a été plus long que l'habitude ce qui a entraîné une
/// dégradation des performances du processus de démarrage du système », avec la duree de la
/// degradation.
///
/// Le piege, trouve le jour meme sur la machine de reference : ses mesures les plus recentes datent
/// du **31/07/2025** (Defender +39,9 s, un Norton desinstalle depuis). L'annee ne s'affichait pas ; les
/// presenter comme actuelles aurait decrit un autre PC. D'ou la fenetre : au-dela, DrDXT le dit et
/// ne cite aucun chiffre.
/// </summary>
public static class DemarrageMesure
{
    // Phrases francaises : nombres et dates en fr-FR, quelle que soit la langue de Windows (« 10,7 s »,
    // jamais « 10.7 s » sur un Windows anglais).
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static string F(FormattableString s) => s.ToString(Fr);

    public static string Source => Docteur.T("BP.Source");

    public static IReadOnlyList<RepairFinding> Juger(IReadOnlyList<MesureDemarrage> mesures, DateTimeOffset maintenant, TimeSpan fenetre)
    {
        var dernier = mesures.Where(m => m.EstLeDemarrage).MaxBy(m => m.Quand);
        if (dernier is null)
            return [new RepairFinding("DEMARRAGE", "mesure de Windows", null, null, RepairStatus.Unknown,
                Docteur.T("BP.Aucune"))];

        if (maintenant - dernier.Quand > fenetre)
            // Observed et non Unknown : la mesure a ete lue, elle est seulement perimee. Unknown la rangerait
            // dans « Non lus » du rapport, ce qui serait faux.
            return [new RepairFinding("DEMARRAGE", "mesure de Windows", Docteur.T("BP.Perimee", dernier.Quand.ToLocalTime()),
                null, RepairStatus.Observed, Docteur.T("BP.PerimeeExplication", dernier.Quand.ToLocalTime()))];

        // Les applications du dernier demarrage mesure : celles qui l'ont ralenti ce jour-la.
        var jour = dernier.Quand.ToLocalTime().Date;
        var lentes = mesures
            .Where(m => !m.EstLeDemarrage && m.Quand.ToLocalTime().Date == jour && m.DegradationMs > 0)
            .GroupBy(m => m.Nom)
            .Select(g => g.MaxBy(m => m.DegradationMs)!)
            .OrderByDescending(m => m.DegradationMs)
            .ToList();

        var constats = new List<RepairFinding>
        {
            new("DEMARRAGE", "mesure de Windows", Docteur.T("BP.Duree", dernier.DureeMs / 1000.0, dernier.Quand.ToLocalTime()),
                null, RepairStatus.Observed, Docteur.T("BP.DureeExplication", lentes.Count)),
        };
        constats.AddRange(lentes.Select(m => new RepairFinding("DEMARRAGE", m.Nom,
            Docteur.T("BP.Plus", m.DegradationMs / 1000.0), null, RepairStatus.Observed,
            Docteur.T("BP.Lente", m.DegradationMs / 1000.0))));
        return constats;
    }

    /// <summary>
    /// L'autre mesure du demarrage, celle que Windows ecrit encore quand l'evenement 100
    /// ne vient plus (releve sur la machine de reference : plus aucun depuis le 31/07/2025, cause inconnue).
    /// Kernel-Boot 30 porte les temps que le BIOS publie dans sa table ACPI FPDT, a chaque allumage,
    /// demarrage rapide compris. Unite prouvee sur la machine de reference : StartOSImageStart du
    /// 23/09/2026 = 11 386 = FwPOSTTime, le « Dernier temps BIOS » du Gestionnaire des taches, en ms.
    /// Aucun seuil : DrDXT n'a pas de source pour dire a partir de quand ce temps est « trop long ».
    /// </summary>
    public static IReadOnlyList<RepairFinding> JugerBios(IReadOnlyList<TempsBios> allumages)
    {
        if (allumages.Count == 0)
            return [new RepairFinding("BIOS", "temps du BIOS", null, null, RepairStatus.Unknown,
                Docteur.T("BP.BiosNePubliePas"))];

        var dernier = allumages.MaxBy(a => a.Quand)!;
        var min = allumages.Min(a => a.BiosMs) / 1000.0;
        var max = allumages.Max(a => a.BiosMs) / 1000.0;
        var plage = allumages.Count > 1 ? Docteur.T("BP.Plage", min, max, allumages.Count) : "";
        return [new RepairFinding("BIOS", "temps du BIOS", Docteur.T("BP.Secondes", dernier.BiosMs / 1000.0), null, RepairStatus.Observed,
            Docteur.T("BP.Bios", dernier.Quand.ToLocalTime(), plage, dernier.ChargeurMs / 1000.0))];
    }
}

public sealed class WindowsBootPerformanceLog : IBootPerformanceLog
{
    private const int MaxEvents = 400;

    public Result<IReadOnlyList<MesureDemarrage>> Lire()
    {
        try
        {
            var query = new EventLogQuery("Microsoft-Windows-Diagnostics-Performance/Operational", PathType.LogName,
                "*[System[(EventID=100 or EventID=101)]]") { ReverseDirection = true };
            var mesures = new List<MesureDemarrage>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null && mesures.Count < MaxEvents; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    var d = XDocument.Parse(e.ToXml()).Descendants().Where(x => x.Name.LocalName == "Data")
                        .ToDictionary(x => (string?)x.Attribute("Name") ?? "", x => x.Value);
                    long Ms(string cle) => long.TryParse(d.GetValueOrDefault(cle), out var v) ? v : 0;
                    var nom = d.GetValueOrDefault("FriendlyName") is { Length: > 0 } f ? f : d.GetValueOrDefault("Name") ?? "application";
                    mesures.Add(e.Id == 100
                        ? new MesureDemarrage(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero), true, "demarrage", Ms("BootTime"), 0)
                        : new MesureDemarrage(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero), false, nom, Ms("TotalTime"), Ms("DegradationTime")));
                }
            }
            return Result<IReadOnlyList<MesureDemarrage>>.Ok(mesures);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<MesureDemarrage>>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }

    public Result<IReadOnlyList<TempsBios>> LireTempsBios()
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-Kernel-Boot'] and EventID=30]]") { ReverseDirection = true };
            var allumages = new List<TempsBios>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null && allumages.Count < 10; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    var d = XDocument.Parse(e.ToXml()).Descendants().Where(x => x.Name.LocalName == "Data")
                        .ToDictionary(x => (string?)x.Attribute("Name") ?? "", x => x.Value);
                    if (!long.TryParse(d.GetValueOrDefault("StartOSImageStart"), out var bios) || bios <= 0) continue;
                    var chargeur = long.TryParse(d.GetValueOrDefault("ExitBootServicesEntry"), out var sortie) && sortie > bios
                        ? sortie - bios : 0;
                    allumages.Add(new TempsBios(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero), bios, chargeur));
                }
            }
            return Result<IReadOnlyList<TempsBios>>.Ok(allumages);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<TempsBios>>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }
}
