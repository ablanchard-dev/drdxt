using System.Globalization;

namespace DrDxt.Core;

/// <summary>
/// Les traces qu'un autre outil de réglage a pu laisser dans le registre : une liste fermée de clés que Microsoft
/// documente, lues avec la date de leur dernière écriture, et cette date rangée par rapport à ce que DrDXT sait
/// (un réglage qu'il tient dans la clé, le démarrage de Windows, l'installation de Windows). Lecture seule : un constat
/// neutre, jamais une correction. Aucun produit n'est nommé : la trace est décrite, pas attribuée.
/// </summary>
/// <remarks>
/// N'entrent ici que les clés dont la page Microsoft existe et dit ce que fait la valeur. NetworkThrottlingIndex, la
/// minuterie globale ou les priorités de Win32PrioritySeparation, souvent posées par des scripts, n'ont pas de page
/// Microsoft qui les décrive : elles restent dehors tant qu'aucune source primaire ne les documente.
/// </remarks>
public static class TracesDOptimiseurs
{
    public const string Domaine = "REGISTRE";

    /// <summary>Où la date de la clé tombe, de la plus parlante à la moins parlante.</summary>
    public enum Origine { TenueParDrDxt, AuDemarrage, AvantInstallation, ApresInstallation, SansRepere, DateNonLue }

    /// <param name="Id">« mmcss », « tdr », « affinite ».</param>
    /// <param name="Chemin">Sous HKEY_LOCAL_MACHINE.</param>
    /// <param name="Valeurs">Les seules valeurs lues dans la clé.</param>
    public sealed record Cle(string Id, string Chemin, IReadOnlyList<string> Valeurs, string Source);

    public const string SourceMmcss = "https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service";
    public const string SourceTdr = "https://learn.microsoft.com/en-us/windows-hardware/drivers/display/tdr-registry-keys";
    public const string SourceAffinite = "https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority";

    /// <summary>Les clés fixes. Les politiques d'affinité, une par périphérique, se cherchent sous
    /// <see cref="RacineDesPeripheriques"/> (sous-clé <see cref="SousCleAffinite"/>).</summary>
    public static readonly IReadOnlyList<Cle> Liste =
    [
        // Microsoft Learn, MMCSS : « SystemResponsiveness … percentage of CPU resources that should be guaranteed to
        // low-priority tasks », sous cette clé.
        new("mmcss", @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", ["SystemResponsiveness"], SourceMmcss),
        // Microsoft Learn, TDR : « End users shouldn't manipulate these registry keys » ; des valeurs de développement de pilote.
        new("tdr", @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            ["TdrLevel", "TdrDelay", "TdrDdiDelay", "TdrDebugMode", "TdrLimitTime", "TdrLimitCount"], SourceTdr),
    ];

    public const string RacineDesPeripheriques = @"SYSTEM\CurrentControlSet\Enum\PCI";
    public const string SousCleAffinite = @"Device Parameters\Interrupt Management\Affinity Policy";
    public static readonly IReadOnlyList<string> ValeursAffinite = ["DevicePolicy", "AssignmentSetOverride"];

    /// <summary>IrqPolicySpecifiedProcessors (IRQ_DEVICE_POLICY, wdm.h) : les interruptions vont aux processeurs du masque
    /// AssignmentSetOverride.</summary>
    public const string PolitiqueProcesseursDesignes = "4";
    public const string SourcePolitique = "https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/wdm/ne-wdm-_irq_device_policy";

    /// <summary>Ce qui a été lu d'une clé : ses valeurs de la liste (absentes = non posées) et la date de sa dernière
    /// écriture (null = non lue).</summary>
    /// <param name="Libelle">Pour une politique d'affinité, le nom du périphérique ; sinon null.</param>
    public sealed record Lecture(string Id, string Chemin, IReadOnlyDictionary<string, string> Valeurs, DateTimeOffset? Ecrite,
        string? Libelle = null);

    /// <summary>Au démarrage : Windows réécrit des clés en démarrant, une date dans ces minutes ne désigne personne.</summary>
    public static readonly TimeSpan FenetreDemarrage = TimeSpan.FromMinutes(3);

    /// <summary>Après l'installation (ou la mise à niveau) : le temps de la première configuration de la machine.</summary>
    public static readonly TimeSpan FenetreInstallation = TimeSpan.FromHours(24);

    /// <summary>Pur : où tombe la date. Une clé où DrDXT tient un réglage passe en premier : la date peut être la sienne.</summary>
    public static Origine Classer(DateTimeOffset? ecrite, DateTimeOffset? installation, DateTimeOffset? demarrage, bool tenueParDrDxt)
    {
        if (tenueParDrDxt) return Origine.TenueParDrDxt;
        if (ecrite is not { } e) return Origine.DateNonLue;
        if (demarrage is { } d && e >= d && e - d <= FenetreDemarrage) return Origine.AuDemarrage;
        // Une mise à niveau en place réécrit la date d'installation et garde le registre : avant elle, rien ne départage.
        if (installation is { } avant && e < avant) return Origine.AvantInstallation;
        if (installation is { } i && e >= i && e - i <= FenetreInstallation) return Origine.ApresInstallation;
        return Origine.SansRepere;
    }

    /// <summary>Pur. <paramref name="lectures"/> null = registre non lu. <paramref name="tenues"/> : les chemins de clés où
    /// DrDXT tient un réglage gardé.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Lecture>? lectures, DateTimeOffset? installation,
        DateTimeOffset? demarrage, IReadOnlySet<string> tenues)
    {
        if (lectures is null)
        {
            yield return new RepairFinding(Domaine, "traces de reglages", null, null, RepairStatus.Unknown, Docteur.T("TO.NonLu"));
            yield break;
        }
        foreach (var l in lectures)
        {
            var origine = Classer(l.Ecrite, installation, demarrage, tenues.Contains(l.Chemin));
            var date = Docteur.T("TO.Date", l.Ecrite is { } e ? e.ToLocalTime().ToString(Docteur.T("Format.DateHeure"), CultureInfo.InvariantCulture) : "?",
                Docteur.T("TO.Origine." + origine));
            var valeurs = string.Join(", ", l.Valeurs.Select(v => $"{v.Key} = {v.Value}"));
            switch (l.Id)
            {
                case "mmcss":
                    yield return new RepairFinding(Domaine, "traces : MMCSS (SystemResponsiveness)",
                        l.Valeurs.GetValueOrDefault("SystemResponsiveness"), null, RepairStatus.Observed,
                        Docteur.T("TO.Mmcss", valeurs.Length > 0 ? valeurs : Docteur.T("TO.Absente")) + date + Docteur.T("TO.Source", SourceMmcss));
                    break;
                // Une clé de la carte toujours présente : seules des valeurs de débogage posées sont une trace.
                case "tdr" when l.Valeurs.Count > 0:
                    yield return new RepairFinding(Domaine, "traces : delais TDR de la carte", valeurs, null, RepairStatus.Suspicious,
                        Docteur.T("TO.Tdr", valeurs) + date + Docteur.T("TO.Source", SourceTdr));
                    break;
                // Les INF des pilotes posent eux-mêmes une politique (Microsoft le prévoit) : seuls des processeurs désignés
                // un à un (IrqPolicySpecifiedProcessors) sont une trace.
                case "affinite" when l.Valeurs.GetValueOrDefault("DevicePolicy") == PolitiqueProcesseursDesignes:
                    yield return new RepairFinding(Domaine, Docteur.Nom("TO.NomAffinite", l.Libelle ?? l.Chemin), valeurs, null,
                        RepairStatus.NonDefault, Docteur.T("TO.Affinite", valeurs) + date + Docteur.T("TO.Source", SourcePolitique));
                    break;
            }
        }
    }
}
