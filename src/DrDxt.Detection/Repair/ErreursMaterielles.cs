using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Les erreurs matérielles que Windows enregistre (WHEA : processeur, mémoire, PCIe). Après une
/// hausse de la limite de puissance, une erreur corrigée qui apparaît est le premier signe d'instabilité, avant l'arrêt.
///
/// Lues dans le journal Système (fournisseur Microsoft-Windows-WHEA-Logger) ; la gravité vient de l'en-tête de
/// l'enregistrement CPER (UEFI, annexe N.2.1 : signature « CPER » sur 4 octets, révision sur 2, fin de signature sur 4,
/// nombre de sections sur 2, puis la gravité sur 4 octets à l'octet 12 : 0 récupérable, 1 fatale, 2 corrigée, 3 information). La gravité CPER est la même
/// chez tous les fabricants, les numéros d'événement non. Relevé le 04/10 sur la machine de référence : un seul
/// enregistrement en 90 jours, gravité 3 (information) : aucune erreur.
/// </summary>
public static class ErreursMaterielles
{
    /// <summary><paramref name="DuProcesseur"/> : une section CPER du processeur (générique ou IA32/X64), pas une erreur PCIe,
    /// mémoire ou de plateforme.</summary>
    public sealed record Enregistrement(DateTimeOffset Quand, int Gravite, bool DuProcesseur = false, Origine? Source = null);

    /// <summary>D'où vient l'erreur, lue dans les champs nommés de l'événement (manifeste du fournisseur WHEA-Logger,
    /// relevé le 16/10/2026 : 18, 19 et 20 portent ApicId, MCABank, MciStat ; 17 porte Bus, Device, Function, VendorID,
    /// DeviceID, PortType, CorrectableErrorStatus). Jamais le message, qui change avec la langue de Windows.</summary>
    /// <param name="ApicId">Processeur logique qui a relevé l'erreur (identifiant APIC) ; null pour une erreur PCIe.</param>
    public sealed record Origine(int? ApicId, int? Banque, ulong? Statut, int? Bus, int? Peripherique, int? Fonction,
        int? Fabricant, int? Produit, int? TypeDePort, uint? StatutCorrigeable);

    /// <summary><c>null</c> quand le journal n'a pas pu être lu ; une liste vide veut dire « lu, rien trouvé » (jamais
    /// « aucune erreur en 90 jours » sur un journal refusé).</summary>
    public static IReadOnlyList<Enregistrement>? Lire(int jours = 90)
    {
        var lus = new List<Enregistrement>();
        try
        {
            var depuis = DateTime.UtcNow.AddDays(-jours).ToString("o", CultureInfo.InvariantCulture);
            var requete = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger'] and TimeCreated[@SystemTime>='{depuis}']]]");
            using var lecteur = new EventLogReader(requete);
            for (var e = lecteur.ReadEvent(); e is not null; e = lecteur.ReadEvent())
                using (e)
                {
                    var brut = e.Properties.Select(p => p.Value).OfType<byte[]>().FirstOrDefault()
                               ?? (e.Properties.Select(p => p.Value as string).FirstOrDefault(s => s?.StartsWith("43504552", StringComparison.Ordinal) == true) is { } hex
                                   ? Convert.FromHexString(hex) : null);
                    if (e.TimeCreated is { } t && Gravite(brut) is { } g)
                        lus.Add(new(new DateTimeOffset(t), g, DuProcesseur(brut), e.Id is >= 17 and <= 20 ? LireOrigine(e.ToXml()) : null));
                }
        }
        catch (Exception) { return null; }
        return lus;
    }

    /// <summary>Pur : les champs nommés de l'événement (XML rendu par Windows) ; null si aucun n'est lisible.</summary>
    public static Origine? LireOrigine(string? xml)
    {
        if (string.IsNullOrEmpty(xml)) return null;
        Dictionary<string, string> champs;
        try
        {
            champs = System.Xml.Linq.XDocument.Parse(xml).Descendants()
                .Where(d => d.Name.LocalName == "Data" && d.Attribute("Name") is not null)
                .GroupBy(d => d.Attribute("Name")!.Value).ToDictionary(g => g.Key, g => g.First().Value.Trim());
        }
        catch (System.Xml.XmlException) { return null; }
        ulong? N(string nom) => champs.TryGetValue(nom, out var v) && v.Length > 0
            && (v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? ulong.TryParse(v.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : (ulong?)null
                : ulong.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : (ulong?)null) is { } x ? x : null;
        int? I(string nom) => N(nom) is { } x && x <= int.MaxValue ? (int)x : null;
        var o = new Origine(I("ApicId"), I("MCABank"), N("MciStat"), I("Bus"), I("Device"), I("Function"), I("VendorID"),
            I("DeviceID"), I("PortType"), N("CorrectableErrorStatus") is { } s && s <= uint.MaxValue ? (uint)s : null);
        return o.ApicId is null && o.Bus is null ? null : o;
    }

    // PCI Express Base Specification, registre « Correctable Error Status » de la capacité AER : un bit par cause.
    private static readonly (int Bit, string Nom)[] BitsCorrigeables =
    [
        (0, "Receiver Error"), (6, "Bad TLP"), (7, "Bad DLLP"), (8, "REPLAY_NUM Rollover"), (12, "Replay Timer Timeout"),
        (13, "Advisory Non-Fatal Error"), (14, "Corrected Internal Error"), (15, "Header Log Overflow"),
    ];
    private const uint AvisNonFatal = 1u << 13;

    /// <summary>
    /// Pur : d'où viennent les erreurs. Une erreur du processeur est dite par le processeur logique qui l'a relevée et sa
    /// banque MCA ; le bloc en cause n'est jamais déduit du numéro de banque. Sur les processeurs AMD récents (SMCA), le
    /// rôle d'une banque se lit dans son registre MCA_IPID (AMD PPR, chapitre Machine Check Architecture), que l'événement
    /// ne porte pas : le bloc reste « non identifié ». Une erreur PCIe est dite par son périphérique et ses causes AER ; un
    /// avis non fatal seul est un avertissement, pas une panne du lien.
    /// </summary>
    public static IEnumerable<RepairFinding> Attribuer(IReadOnlyList<Enregistrement>? lus)
    {
        var sources = (lus ?? []).Where(e => e.Gravite is 0 or 1 or 2 && e.Source is not null)
            .GroupBy(e => e.Source! with { Statut = null, StatutCorrigeable = null })
            .OrderByDescending(g => g.Count()).Take(5).ToList();
        if (sources.Count == 0) yield break;
        var morceaux = sources.Select(g =>
        {
            var o = g.Key;
            var n = g.Count();
            if (o.ApicId is { } apic)
                return Docteur.T("EM.Processeur", n, apic, o.Banque?.ToString(CultureInfo.InvariantCulture) ?? "?",
                    g.Select(e => e.Source!.Statut).OfType<ulong>().Select(s => $"0x{s:X16}").FirstOrDefault() ?? "?");
            var bits = g.Aggregate(0u, (acc, e) => acc | (e.Source!.StatutCorrigeable ?? 0));
            var causes = BitsCorrigeables.Where(b => (bits & (1u << b.Bit)) != 0).Select(b => b.Nom).ToList();
            return Docteur.T("EM.Pcie", n, $"{o.Bus:X2}:{o.Peripherique:X2}.{o.Fonction:X}",
                o.Fabricant is { } v ? $"{v:X4}" : "?", o.Produit is { } p ? $"{p:X4}" : "?",
                causes.Count > 0 ? string.Join(", ", causes) : Docteur.T("EM.CauseNonLue"))
                + (bits == AvisNonFatal ? Docteur.T("EM.AvisSeul") : "");
        });
        yield return new RepairFinding("STABILITE", "origine des erreurs materielles", null, null, RepairStatus.Observed,
            string.Join(" ", morceaux) + Docteur.T("EM.OrigineSuite"));
    }

    /// <summary>La gravité CPER, ou <c>null</c> si l'en-tête n'en est pas un.</summary>
    public static int? Gravite(byte[]? cper)
        => cper is { Length: >= 16 } && cper[0] == (byte)'C' && cper[1] == (byte)'P' && cper[2] == (byte)'E' && cper[3] == (byte)'R'
            ? (int)BitConverter.ToUInt32(cper, 12) : null;

    /// <summary>Pur : l'enregistrement porte-t-il une section du processeur ? UEFI, annexe N (Common Platform Error Record) : le
    /// nombre de sections en octet 10, les descripteurs de section à partir de l'octet 128, 72 octets chacun, le type de section
    /// (GUID) à l'octet 16 du descripteur ; processeur = « Processor Generic » ou « IA32/X64 ».</summary>
    public static bool DuProcesseur(byte[]? cper)
    {
        if (Gravite(cper) is null || cper!.Length < 130) return false;
        var sections = BitConverter.ToUInt16(cper, 10);
        for (var i = 0; i < sections; i++)
        {
            var o = 128 + 72 * i;
            if (o + 32 > cper.Length) return false;
            var type = new Guid(cper.AsSpan(o + 16, 16));
            if (type == ProcesseurGenerique || type == ProcesseurX64) return true;
        }
        return false;
    }

    private static readonly Guid ProcesseurGenerique = new("9876CCAD-47B4-4BDB-B65E-16F193C4F3DB");
    private static readonly Guid ProcesseurX64 = new("DC3EA0B0-A144-4797-B95B-53FA242B6E1D");

    /// <summary>Au-delà, des erreurs corrigées toutes PCIe sont un flot du lien, à corriger. Aucun seuil n'est publié ; un lien
    /// qui gère son énergie en relève quelques-unes, pas des dizaines par jour, qui disent plutôt un signal qui passe mal
    /// (carte mal enfoncée, rallonge, vitesse PCIe). Compté sur les 24 heures avant la plus récente.</summary>
    public const int PcieParJourARegarder = 5;

    /// <summary>Pur. Les informations ne comptent pas ; une erreur fatale ou récupérable pèse plus qu'une corrigée.</summary>
    public static IEnumerable<RepairFinding> Juger(IReadOnlyList<Enregistrement>? lus, int jours = 90)
    {
        if (lus is null)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", null, null, RepairStatus.Unknown,
                Docteur.T("EM.NonLu"));
            yield break;
        }
        var erreurs = lus.Where(e => e.Gravite is 0 or 1 or 2).OrderByDescending(e => e.Quand).ToList();
        if (erreurs.Count == 0)
        {
            yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)", Docteur.T("EM.AucuneEn", jours), Docteur.T("EM.Aucune"),
                RepairStatus.ExpectedDefault,
                Docteur.T("EM.AucuneExplication", jours)
                + (lus.Count > 0 ? Docteur.T("EM.Information", lus.Count) : Docteur.T("EM.Point"))
                + Docteur.T("EM.ApresBios"));
            yield break;
        }
        var graves = erreurs.Count(e => e.Gravite is 0 or 1);
        // Une seule erreur corrigée, ou des corrigées toutes PCIe à un rythme faible (un « Advisory Non-Fatal » que le lien
        // relève en gérant son énergie) : rien ne prouve un tort, à regarder. Une grave, plusieurs corrigées du processeur ou de
        // la mémoire, ou un flot du lien PCIe : à corriger.
        var toutesPcie = erreurs.All(e => e.Source?.Bus is not null);
        var surUnJour = erreurs.Count(e => e.Quand > erreurs[0].Quand.AddDays(-1));
        var flotPcie = graves == 0 && erreurs.Count > 1 && toutesPcie && surUnJour > PcieParJourARegarder;
        var aRegarder = graves == 0 && !flotPcie && (erreurs.Count == 1 || toutesPcie);
        var dates = string.Join(", ", erreurs.Take(5).Select(e => e.Quand.LocalDateTime.ToString(Docteur.T("EM.FormatDate"), CultureInfo.InvariantCulture)));
        yield return new RepairFinding("STABILITE", "erreurs matérielles (WHEA)",
            graves > 0 ? Docteur.T("EM.GravesValeur", graves, erreurs.Count - graves) : Docteur.T("EM.CorrigeesValeur", erreurs.Count),
            Docteur.T("EM.Aucune"),
            aRegarder ? RepairStatus.NonDefault : RepairStatus.Suspicious,
            (graves > 0 ? Docteur.T("EM.Graves", graves) : Docteur.T("EM.Corrigees", erreurs.Count))
            + Docteur.T("EM.Suite", jours, dates) + (aRegarder ? Docteur.T("EM.ARegarder") : "")
            + (flotPcie ? Docteur.T("EM.FlotPcie", surUnJour) : ""),
            RepairAction.Recommandation);
    }
}
