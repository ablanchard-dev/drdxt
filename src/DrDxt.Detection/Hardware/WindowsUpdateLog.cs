using static DrDxt.Core.TexteFrancais;
using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

/// <summary>Une installation tentee par Windows Update (ou le Store) : reussie (evenement 19) ou
/// echouee (evenement 20), avec le titre et le code d'erreur que le journal donne.</summary>
public sealed record EssaiDeMiseAJour(DateTimeOffset Quand, bool Reussi, string Titre, string? Code);

/// <summary>Les essais de mise a jour depuis une date. Un echec de lecture est un `Fail`, jamais une
/// liste vide.</summary>
public interface IWindowsUpdateLog
{
    Result<IReadOnlyList<EssaiDeMiseAJour>> Depuis(DateTimeOffset earliest);
}

/// <summary>
/// 25/09/2026 — le « docteur » : les mises a jour qui echouent. Mesure du jour sur la machine de
/// reference, 90 jours : **27 echecs pour 575 reussites**. Le compte seul ferait peur pour rien : le
/// correctif de securite KB5124008 a echoue deux fois puis s'est installe. Ce qui merite d'etre dit
/// est ce qui reste en echec : trois applications du Store, toujours en 0x80073D02.
/// </summary>
public static class MisesAJourEnEchec
{
    public const string Sources =
        "learn.microsoft.com/windows/deployment/update/windows-update-error-reference ; "
        + "learn.microsoft.com/windows/win32/appxpkg/troubleshooting";

    /// <summary>Les codes que DrDXT sait nommer, avec la phrase de la documentation Microsoft.</summary>
    private static readonly Dictionary<string, string> Codes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["0x8024001e"] = "le service ou le système s'arrêtait pendant l'opération",
        ["0x80073d02"] = "ce que le paquet modifie était en cours d'utilisation",
    };

    /// <summary>29/09/2026, troisième critique : « 9NTXGKQ8P7N0-MicrosoftWindows.CrossDevice » en titre. Un paquet du
    /// Store (identifiant de douze caractères, tiret) se nomme par son application ; un autre titre reste tel quel.</summary>
    internal static string Lisible(string titre)
    {
        var m = System.Text.RegularExpressions.Regex.Match(titre, @"^[0-9A-Z]{12}-(?:.*\.)?([^.]+)$");
        return m.Success ? $"Mise à jour de l'application {m.Groups[1].Value}" : titre;
    }

    /// <summary>Echouee au moins deux fois et jamais reussie depuis : c'est ce qui reste a soigner.
    /// Un seul echec peut encore etre repris au prochain essai ; il est observe, pas accuse.</summary>
    public const int EchecsPourPersister = 2;

    /// <summary>04/10/2026 (plan 3.3, relecture sur la machine) : « AppliX échoue à chaque essai et ne s'est pas installée
    /// depuis » était faux. En 0x80073D02 (appli ouverte), Windows dépose la nouvelle version puis note l'échec : relevé,
    /// dossier de AppliX 26.930 créé le 02/10 à 18:48:08, échec noté à 18:48:42, et c'est cette version qui est installée.
    /// Une mise à jour du Store dont la version installée a été déposée au plus tard 10 minutes avant le dernier échec, ou
    /// après, est rattrapée.</summary>
    public static readonly TimeSpan MargeDeDepot = TimeSpan.FromMinutes(10);

    internal static string SansVersion(string titre)
        => System.Text.RegularExpressions.Regex.Replace(titre, @"\b(?:version\s*|v)?\d+(?:\.\d+){1,3}\b", "#");

    /// <summary>Le nom de paquet d'un titre du Store (« 9PLM9XGG6VKS-Editeur.AppliX » ⇒ « Editeur.AppliX »), sinon <c>null</c>.</summary>
    internal static string? NomDePaquet(string titre)
    {
        var m = System.Text.RegularExpressions.Regex.Match(titre, @"^[0-9A-Z]{12}-(.+)$");
        return m.Success ? m.Groups[1].Value : null;
    }

    private static readonly string[] PaquetsDesJeux = ["Microsoft.GamingServices", "Microsoft.XboxIdentityProvider"];

    public static IReadOnlyList<RepairFinding> Juger(IReadOnlyList<EssaiDeMiseAJour> essais, int jours,
        Func<string, DateTimeOffset?>? versionInstalleeLe = null)
    {
        var echecs = essais.Count(e => !e.Reussi);
        if (echecs == 0)
            return [new RepairFinding("WINDOWS UPDATE", "echecs", "aucun", "aucun", RepairStatus.ExpectedDefault,
                $"aucune installation de mise à jour n'a échoué sur {jours} jours.")];

        var enEchec = essais
            // Revue adverse du 09/10/2026 (R10) : Defender et l'outil de suppression changent de titre à chaque version ;
            // sans le numéro de version, la version suivante réussie rattrape l'échec de la précédente.
            .GroupBy(e => SansVersion(e.Titre))
            .Select(g => g.OrderBy(e => e.Quand).ToList())
            .Where(g => !g[^1].Reussi)
            .Where(g => !(NomDePaquet(g[^1].Titre) is { } paquet && versionInstalleeLe?.Invoke(paquet) is { } depose
                          && depose >= g[^1].Quand - MargeDeDepot))
            .Select(g => (Titre: g[^1].Titre, Rates: g.Where(e => !e.Reussi).ToList()))
            .ToList();
        var rattrapes = echecs - enEchec.Sum(g => g.Rates.Count);

        var restants = enEchec
            .Select(g =>
            {
                var rates = g.Rates;
                var code = rates[^1].Code;
                // 29/09/2026 : le code passe de la valeur affichée à la seconde phrase de l'explication (sous
                // « Pourquoi ? ») ; la valeur dit ce que le joueur comprend, combien d'échecs et depuis quand.
                var sens = code is not null && Codes.TryGetValue(code, out var p) ? $" : {p}" : "";
                var persiste = rates.Count >= EchecsPourPersister;
                // UI1 (08/10/2026) : une appli du Store qui ne se met pas à jour marche toujours ; elle se regarde (avec son
                // geste), elle ne se range pas en rouge avec un service exploitable. Une mise à jour de Windows reste suspecte.
                // Revue n° 15 : sauf les paquets dont les jeux dépendent (Gaming Services, identité Xbox : un échec peut
                // empêcher un jeu Game Pass de se lancer) ; ceux-là restent à corriger.
                var appliDuStore = NomDePaquet(g.Titre) is { } paquet && !PaquetsDesJeux.Any(j => paquet.EndsWith(j, StringComparison.OrdinalIgnoreCase));
                return new RepairFinding("WINDOWS UPDATE", Lisible(g.Titre),
                    F($"{rates.Count} {(rates.Count > 1 ? "échecs" : "échec")}, le dernier le {rates[^1].Quand.ToLocalTime():dd/MM}"),
                    "installée",
                    persiste && !appliDuStore ? RepairStatus.Suspicious : RepairStatus.Observed,
                    (persiste
                        ? "échoue à chaque essai et ne s'est pas installée depuis"
                        : "a échoué une fois ; Windows la retentera")
                    + ". " + (code is null ? "Code d'erreur inconnu." : $"Code {code}{sens}.")
                    // DOC-QUALITE (09/10/2026) : une recommandation porte son geste.
                    + (!persiste ? ""
                        : NomDePaquet(g.Titre) is not null
                            ? " Pour la relancer : ferme l'application, puis Microsoft Store > Bibliothèque > Obtenir les mises à jour."
                            : " Pour la relancer : Paramètres > Windows Update > Rechercher des mises à jour."),
                    persiste ? RepairAction.Recommandation : RepairAction.Aucune);
            })
            .OrderByDescending(c => c.Statut == RepairStatus.Suspicious)
            .ToList();

        // 27/09/2026 : le rattrapage n'etait dit qu'en info-bulle ; « 27 sur 90 jours » au-dessus de quatre
        // lignes a un echec chacune se lisait comme 23 echecs caches. La valeur visible porte sa part rattrapee.
        var bilan = new RepairFinding("WINDOWS UPDATE", "echecs",
            rattrapes > 0 ? $"{echecs} échecs en {jours} jours, dont {rattrapes} rattrapés ensuite" : $"{echecs} échecs en {jours} jours",
            null, RepairStatus.Observed,
            $"{rattrapes} de ces échecs ont été rattrapés par une installation réussie ensuite : ils ne demandent rien. "
            + (restants.Count == 0
                ? "Aucune mise à jour ne reste en échec."
                : restants.Count == 1 ? "1 mise à jour reste en échec, détaillée ci-dessous."
                : $"{restants.Count} mises à jour restent en échec, détaillées ci-dessous."));
        return [bilan, .. restants];
    }
}

/// <summary>04/10/2026 (plan 3.3) : la date de dépôt de la version installée d'un paquet du Store pour l'utilisateur
/// courant, lue dans le dépôt des paquets (HKCU, AppModel\Repository\Packages : nom complet et dossier). <c>null</c> si
/// le paquet n'est pas installé ou si rien n'est lisible.</summary>
public static class PaquetsInstalles
{
    private const string Depot = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    public static DateTimeOffset? DeposeLe(string nomDePaquet)
    {
        try
        {
            using var cle = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(Depot);
            if (cle is null) return null;
            DateTimeOffset? plusRecent = null;
            foreach (var nom in cle.GetSubKeyNames().Where(n => n.StartsWith(nomDePaquet + "_", StringComparison.OrdinalIgnoreCase)))
            {
                using var p = cle.OpenSubKey(nom);
                if (p?.GetValue("PackageRootFolder") is string dossier && Directory.Exists(dossier))
                {
                    var d = new DateTimeOffset(Directory.GetCreationTimeUtc(dossier), TimeSpan.Zero);
                    if (plusRecent is null || d > plusRecent) plusRecent = d;
                }
            }
            return plusRecent;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>Le lecteur reel : journal Systeme, fournisseur WindowsUpdateClient, evenements 19 et 20.</summary>
public sealed class WindowsUpdateEventLog : IWindowsUpdateLog
{
    private const int MaxEvents = 2000;

    public Result<IReadOnlyList<EssaiDeMiseAJour>> Depuis(DateTimeOffset earliest)
    {
        try
        {
            var query = new EventLogQuery("System", PathType.LogName,
                "*[System[Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20)]]")
            { ReverseDirection = true };
            var essais = new List<EssaiDeMiseAJour>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null && essais.Count < MaxEvents; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.TimeCreated is not { } t) continue;
                    var quand = new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero);
                    if (quand < earliest) break;
                    var donnees = XDocument.Parse(e.ToXml()).Descendants().Where(d => d.Name.LocalName == "Data")
                        .ToDictionary(d => (string?)d.Attribute("Name") ?? "", d => d.Value);
                    essais.Add(new EssaiDeMiseAJour(quand, e.Id == 19,
                        donnees.GetValueOrDefault("updateTitle") ?? "mise à jour sans titre",
                        donnees.GetValueOrDefault("errorCode")));
                }
            }
            return Result<IReadOnlyList<EssaiDeMiseAJour>>.Ok(essais);
        }
        catch (Exception ex)
        {
            return Result<IReadOnlyList<EssaiDeMiseAJour>>.Fail(ErrorCodes.DetectionFailed, ex.Message);
        }
    }
}

/// <summary>06/10/2026 (chantier L-WU) : les redémarrages demandés par Windows Update, lus dans le journal Système
/// (événement 1074 de User32 dont le processus est `MoUsoCoreWorker.exe`), sans droits. <c>null</c> = journal non lu, jamais
/// « aucun ». Mesure du 06/10 : un seul en 30 jours, le 06/10 à 10:15 (0x80020010, « Planifié »).</summary>
public static class RedemarragesWindowsUpdate
{
    public static IReadOnlyList<DateTimeOffset>? Depuis(DateTimeOffset earliest)
    {
        try
        {
            var ms = (long)Math.Max(0, (DateTimeOffset.UtcNow - earliest).TotalMilliseconds);
            var query = new EventLogQuery("System", PathType.LogName,
                $"*[System[Provider[@Name='User32'] and EventID=1074 and TimeCreated[timediff(@SystemTime) <= {ms}]]]");
            var liste = new List<DateTimeOffset>();
            using var reader = new EventLogReader(query);
            for (EventRecord? e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } t && DuWindowsUpdate(e.Properties.Count > 0 ? e.Properties[0].Value as string : null))
                        liste.Add(new DateTimeOffset(t.ToUniversalTime(), TimeSpan.Zero));
            return liste;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Le premier champ de l'événement : « C:\WINDOWS\uus\AMD64\MoUsoCoreWorker.exe (MACHINE) ».</summary>
    internal static bool DuWindowsUpdate(string? processus)
        => processus is not null && Path.GetFileNameWithoutExtension(processus.Split(" (")[0])
            .Equals(HeuresActives.ProcessusWindowsUpdate, StringComparison.OrdinalIgnoreCase);
}
