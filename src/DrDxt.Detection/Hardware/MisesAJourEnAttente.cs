using System.Globalization;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

public enum GenreDeMiseAJour { Securite, NouvelleVersion, Pilote, Definition, Autre }

public sealed record MiseAJourEnAttente(string Titre, GenreDeMiseAJour Genre);

/// <param name="DerniereRecherche">La dernière recherche réussie de Windows Update ; <c>null</c> = jamais ou non lue.</param>
/// <param name="Proposees">chantier B12 : ce qui ne s'installe qu'au clic (« Télécharger et installer »).</param>
public sealed record EtatWindowsUpdate(IReadOnlyList<MiseAJourEnAttente> EnAttente, DateTimeOffset? DerniereRecherche,
    IReadOnlyList<MiseAJourEnAttente>? Proposees = null);

public interface ILecteurWindowsUpdate
{
    Result<EtatWindowsUpdate> Lire();
}

/// <summary>
/// 06/10/2026 (plan 3b.7) : ce qui attend dans Windows Update, en lecture seule. Le docteur lisait les échecs passés,
/// jamais ce qui attend (le mainteneur : « Windows me propose une mise à jour, DrDXT ne le dit même pas »). DrDXT n'installe
/// rien : il dit, et renvoie à Paramètres &gt; Windows Update.
/// </summary>
public static class MisesAJourEnAttente
{
    /// <summary>**NON CHIFFRÉ** : Windows vérifie d'ordinaire chaque jour ; au-delà d'une semaine sans vérification
    /// réussie, quelque chose l'empêche (service arrêté, réseau, stratégie).</summary>
    public static readonly TimeSpan VerificationAncienne = TimeSpan.FromDays(7);

    /// <param name="fuseau">Le fuseau de l'heure affichée ; absent = celui de la machine (tests : un fuseau donné).</param>
    public static RepairFinding Juger(EtatWindowsUpdate? etat, DateTimeOffset maintenant, string? pourquoiNonLu = null,
        TimeZoneInfo? fuseau = null)
    {
        if (etat is null)
            return new RepairFinding("WINDOWS UPDATE", "en attente", null, null, RepairStatus.Unknown,
                $"ce que Windows Update attend n'a pas pu être lu ({pourquoiNonLu}). « Non lu » n'est pas « rien en attente ».");

        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var verifie = etat.DerniereRecherche is { } d
            ? $"Dernière vérification par Windows : {TimeZoneInfo.ConvertTime(d, fuseau ?? TimeZoneInfo.Local).ToString("dd/MM 'à' HH:mm", fr)}."
            : "Windows n'a jamais réussi à vérifier.";
        var ancienne = etat.DerniereRecherche is not { } dr || maintenant - dr > VerificationAncienne;
        // Revue du 06/10 (P2) : « jamais vérifié » se dit tel quel, pas « depuis 0 jours ».
        var depuis = etat.DerniereRecherche is { } j
            ? $"Windows n'a pas vérifié depuis {(int)(maintenant - j).TotalDays} jours"
            : "Windows n'a jamais réussi à vérifier";

        if (etat.EnAttente.Count == 0)
            return ancienne
                ? new RepairFinding("WINDOWS UPDATE", "en attente", "aucune", null, RepairStatus.Suspicious,
                    $"rien en attente, mais {depuis} : ce qu'il a manqué n'est pas connu. "
                    + "Paramètres > Windows Update > Rechercher des mises à jour.", RepairAction.Recommandation)
                : new RepairFinding("WINDOWS UPDATE", "en attente", "aucune", "aucune", RepairStatus.ExpectedDefault,
                    $"rien n'attend dans Windows Update. {verifie}");

        string Compte(GenreDeMiseAJour g, string un, string plusieurs)
        {
            var n = etat.EnAttente.Count(u => u.Genre == g);
            return n == 0 ? "" : $"{n} {(n == 1 ? un : plusieurs)}";
        }
        var parGenre = string.Join(", ", new[]
        {
            Compte(GenreDeMiseAJour.Securite, "correctif de sécurité", "correctifs de sécurité"),
            Compte(GenreDeMiseAJour.NouvelleVersion, "nouvelle version de Windows", "nouvelles versions de Windows"),
            Compte(GenreDeMiseAJour.Pilote, "pilote", "pilotes"),
            Compte(GenreDeMiseAJour.Definition, "définition de l'antivirus", "définitions de l'antivirus"),
            Compte(GenreDeMiseAJour.Autre, "autre", "autres"),
        }.Where(s => s.Length > 0));
        // Revue du 06/10 (P2) : 3 titres au plus ; une machine avec 15 pilotes en attente ne remplit pas la carte.
        const int TitresMax = 3;
        var reste = etat.EnAttente.Count - TitresMax;
        var titres = string.Join(" ; ", etat.EnAttente.Take(TitresMax).Select(u => u.Titre))
            + (reste > 0 ? $" ; et {reste} {(reste == 1 ? "autre" : "autres")}" : "");
        var securite = etat.EnAttente.Any(u => u.Genre == GenreDeMiseAJour.Securite);

        return new RepairFinding("WINDOWS UPDATE", "en attente", etat.EnAttente.Count.ToString(CultureInfo.InvariantCulture), "0",
            securite || ancienne ? RepairStatus.Suspicious : RepairStatus.Observed,
            (etat.EnAttente.Count == 1 ? "1 mise à jour attend : " : $"{etat.EnAttente.Count} mises à jour attendent : ") + $"{parGenre} ({titres}). {verifie} "
            + (securite ? "Un correctif de sécurité ferme une faille connue : à installer hors partie. " : "")
            + "Pour les installer : Paramètres > Windows Update. DrDXT n'installe rien lui-même.",
            RepairAction.Recommandation);
    }

    /// <summary>06/10/2026 (chantier B12) : une nouvelle version de Windows proposée au clic. Mesure : la
    /// version proposée lue sur cette machine. Les préversions facultatives ne sont pas un constat.</summary>
    /// <param name="versionInstallee">DisplayVersion (« 25H2 ») ; null = non lue.</param>
    /// <param name="proposees">null = la recherche des propositions n'a pas pu être lue (F-34) : dit « non lu ».</param>
    public static IEnumerable<RepairFinding> JugerProposees(IReadOnlyList<MiseAJourEnAttente>? proposees, string? versionInstallee)
    {
        if (proposees is null)
        {
            yield return new RepairFinding("WINDOWS UPDATE", "nouvelle version de Windows proposée", null, null,
                RepairStatus.Unknown, "ce que Windows propose au clic (« Télécharger et installer ») n'a pas pu être lu. "
                + "« Non lu » n'est pas « rien de proposé » : regarde Paramètres > Windows Update.");
            yield break;
        }
        foreach (var v in proposees.Where(p => p.Genre == GenreDeMiseAJour.NouvelleVersion))
            yield return new RepairFinding("WINDOWS UPDATE", "nouvelle version de Windows proposée", v.Titre,
                versionInstallee is null ? null : $"{versionInstallee} installée", RepairStatus.Observed,
                $"Windows te propose « {v.Titre} »{(versionInstallee is null ? "" : $" (tu es en {versionInstallee})")}. Elle ne "
                + "s'installe que si tu cliques « Télécharger et installer » dans Paramètres > Windows Update. Ce n'est pas un "
                + "correctif de sécurité : rien ne presse. Si tu l'installes, fais-le hors partie : elle redémarre le PC et peut "
                + "remettre des pilotes ou des réglages à zéro. Aucun gain en jeu n'est publié.",
                RepairAction.Recommandation);
    }
}

/// <summary>Le vrai : l'API Windows Update (Microsoft.Update.Session), recherche HORS LIGNE (dernier résultat de
/// Windows, sans réseau ; 2,4 s mesurées le 06/10/2026), et la date de la dernière recherche réussie.</summary>
[SupportedOSPlatform("windows")]
public sealed class LecteurWindowsUpdate : ILecteurWindowsUpdate
{
    public Result<EtatWindowsUpdate> Lire()
    {
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
            dynamic chercheur = session.CreateUpdateSearcher();
            chercheur.Online = false;
            var enAttente = Chercher(chercheur, "IsInstalled=0 and IsHidden=0");
            // B12 : ce qui ne s'installe qu'au clic n'apparaît qu'avec ce critère (Microsoft Learn, IUpdateSearcher::Search).
            // Une panne de cette seconde recherche ne retire pas la première : la liste reste vide.
            // Revue (F-34) : une panne de cette seconde recherche est « non lu » (null), pas « rien de proposé ».
            List<MiseAJourEnAttente>? proposees;
            try { proposees = Chercher(chercheur, "IsInstalled=0 and IsHidden=0 and DeploymentAction='OptionalInstallation'"); }
            catch (Exception) { proposees = null; }

            DateTimeOffset? derniere = null;
            try
            {
                dynamic auto = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate", throwOnError: true)!)!;
                if (auto.Results.LastSearchSuccessDate is DateTime dt && dt.Year > 2000)
                    derniere = new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            }
            catch (Exception) { /* date non lue : dite « jamais » plutôt qu'inventée */ }

            return Result<EtatWindowsUpdate>.Ok(new EtatWindowsUpdate(enAttente, derniere, proposees));
        }
        catch (Exception ex)
        {
            return Result<EtatWindowsUpdate>.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }

    private static List<MiseAJourEnAttente> Chercher(dynamic chercheur, string critere)
    {
        dynamic resultat = chercheur.Search(critere);
        var liste = new List<MiseAJourEnAttente>();
        foreach (dynamic u in resultat.Updates)
        {
            var categories = new List<string>();
            foreach (dynamic c in u.Categories) categories.Add((string)c.CategoryID);
            liste.Add(new MiseAJourEnAttente((string)u.Title, Genre((int)u.Type, categories)));
        }
        return liste;
    }

    // Les identifiants de classification de Windows Update (stables ; le nom, lui, est traduit selon la langue).
    private const string Pilotes = "ebfc1fc5-71a4-4f7b-9aca-3b9a503104a0";
    private const string MisesANiveau = "3689bdc8-b205-4af4-8d4a-a63924c5e9d5";
    private const string Securite = "0fa1201d-4330-4fa8-8ae9-b877473b6441";
    private const string Critiques = "e6cf1350-c01b-414d-a61f-263d14d133b4";
    private const string Definitions = "e0789628-ce08-4437-be74-2495b842f43b";

    /// <summary>Type 2 = pilote (IUpdate.Type) ; les autres, par identifiant de catégorie (revue du 06/10).</summary>
    internal static GenreDeMiseAJour Genre(int type, IReadOnlyList<string> categories)
    {
        bool A(string id) => categories.Any(c => string.Equals(c, id, StringComparison.OrdinalIgnoreCase));
        if (type == 2 || A(Pilotes)) return GenreDeMiseAJour.Pilote;
        if (A(MisesANiveau)) return GenreDeMiseAJour.NouvelleVersion;
        if (A(Securite) || A(Critiques)) return GenreDeMiseAJour.Securite;
        if (A(Definitions)) return GenreDeMiseAJour.Definition;
        return GenreDeMiseAJour.Autre;
    }
}
