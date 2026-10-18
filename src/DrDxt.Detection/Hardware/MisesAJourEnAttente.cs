using System.Globalization;
using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Detection.Hardware;

public enum GenreDeMiseAJour { Securite, NouvelleVersion, Pilote, Definition, Autre }

public sealed record MiseAJourEnAttente(string Titre, GenreDeMiseAJour Genre);

/// <param name="DerniereRecherche">La dernière recherche réussie de Windows Update ; <c>null</c> = jamais ou non lue.</param>
/// <param name="Proposees">Ce qui ne s'installe qu'au clic (« Télécharger et installer »).</param>
public sealed record EtatWindowsUpdate(IReadOnlyList<MiseAJourEnAttente> EnAttente, DateTimeOffset? DerniereRecherche,
    IReadOnlyList<MiseAJourEnAttente>? Proposees = null);

public interface ILecteurWindowsUpdate
{
    Result<EtatWindowsUpdate> Lire();
}

/// <summary>
/// Ce qui attend dans Windows Update, en lecture seule. Le docteur lisait les échecs passés,
/// Jamais ce qui attend. DrDXT n'installe
/// rien : il dit, et renvoie à Paramètres &gt; Windows Update.
/// </summary>
public static class MisesAJourEnAttente
{
    /// <summary>**Non chiffré** : Windows vérifie d'ordinaire chaque jour ; au-delà d'une semaine sans vérification
    /// réussie, quelque chose l'empêche (service arrêté, réseau, stratégie).</summary>
    public static readonly TimeSpan VerificationAncienne = TimeSpan.FromDays(7);

    /// <summary>La cible du geste « chercher maintenant », portée par un constat dont la vérification est ancienne.</summary>
    public const string CibleRecherche = "recherche";

    /// <param name="fuseau">Le fuseau de l'heure affichée ; absent = celui de la machine (tests : un fuseau donné).</param>
    public static RepairFinding Juger(EtatWindowsUpdate? etat, DateTimeOffset maintenant, string? pourquoiNonLu = null,
        TimeZoneInfo? fuseau = null)
    {
        if (etat is null)
            return new RepairFinding("WINDOWS UPDATE", "en attente", null, null, RepairStatus.Unknown,
                Docteur.T("WU.NonLu", pourquoiNonLu));

        var verifie = etat.DerniereRecherche is { } d
            ? Docteur.T("WU.DerniereVerification",
                TimeZoneInfo.ConvertTime(d, fuseau ?? TimeZoneInfo.Local).ToString(Docteur.T("WU.FormatDate"), Docteur.Langue))
            : Docteur.T("WU.JamaisVerifiePhrase");
        var ancienne = etat.DerniereRecherche is not { } dr || maintenant - dr > VerificationAncienne;
        // « jamais vérifié » se dit tel quel, pas « depuis 0 jours ».
        var depuis = etat.DerniereRecherche is { } j
            ? Docteur.T("WU.PasVerifieDepuis", (int)(maintenant - j).TotalDays)
            : Docteur.T("WU.JamaisVerifie");

        if (etat.EnAttente.Count == 0)
            return ancienne
                ? new RepairFinding("WINDOWS UPDATE", "en attente", Docteur.T("WU.Aucune"), null, RepairStatus.Suspicious,
                    Docteur.T("WU.RienMaisAncien", depuis), RepairAction.Recommandation, Remede: Docteur.T("WU.PourInstaller"),
                    Cible: CibleRecherche)
                : new RepairFinding("WINDOWS UPDATE", "en attente", Docteur.T("WU.Aucune"), Docteur.T("WU.Aucune"), RepairStatus.ExpectedDefault,
                    Docteur.T("WU.RienNAttend", verifie));

        string Compte(GenreDeMiseAJour g)
        {
            var n = etat.EnAttente.Count(u => u.Genre == g);
            return n == 0 ? "" : $"{n} {Docteur.T($"WU.{g}.{(n == 1 ? "Un" : "Plusieurs")}")}";
        }
        var parGenre = string.Join(", ", new[]
        {
            Compte(GenreDeMiseAJour.Securite), Compte(GenreDeMiseAJour.NouvelleVersion), Compte(GenreDeMiseAJour.Pilote),
            Compte(GenreDeMiseAJour.Definition), Compte(GenreDeMiseAJour.Autre),
        }.Where(s => s.Length > 0));
        // 3 titres au plus ; une machine avec 15 pilotes en attente ne remplit pas la carte.
        const int TitresMax = 3;
        var reste = etat.EnAttente.Count - TitresMax;
        var titres = string.Join(Docteur.T("WU.SeparateurTitres"), etat.EnAttente.Take(TitresMax).Select(u => u.Titre))
            + (reste > 0 ? Docteur.T(reste == 1 ? "WU.EtUnAutre" : "WU.EtAutres", reste) : "");
        var securite = etat.EnAttente.Any(u => u.Genre == GenreDeMiseAJour.Securite);

        return new RepairFinding("WINDOWS UPDATE", "en attente", etat.EnAttente.Count.ToString(CultureInfo.InvariantCulture), "0",
            securite || ancienne ? RepairStatus.Suspicious : RepairStatus.Observed,
            (etat.EnAttente.Count == 1 ? Docteur.T("WU.UneAttend") : Docteur.T("WU.PlusieursAttendent", etat.EnAttente.Count))
            + $"{parGenre} ({titres}). {verifie} "
            + (securite ? Docteur.T("WU.SecuriteAInstaller") : "")
            + Docteur.T("WU.RienInstalle"),
            RepairAction.Recommandation, Remede: Docteur.T("WU.PourInstaller"), Cible: ancienne ? CibleRecherche : null);
    }

    /// <summary>Une nouvelle version de Windows proposée au clic. Mesure : la
    /// version proposée lue sur cette machine. Les préversions facultatives ne sont pas un constat.</summary>
    /// <param name="versionInstallee">DisplayVersion (« 25H2 ») ; null = non lue.</param>
    /// <param name="proposees">null = la recherche des propositions n'a pas pu être lue : dit « non lu ».</param>
    public static IEnumerable<RepairFinding> JugerProposees(IReadOnlyList<MiseAJourEnAttente>? proposees, string? versionInstallee)
    {
        if (proposees is null)
        {
            yield return new RepairFinding("WINDOWS UPDATE", "nouvelle version de Windows proposée", null, null,
                RepairStatus.Unknown, Docteur.T("WU.ProposeesNonLu"));
            yield break;
        }
        // Windows Update peut proposer la même version sous deux identifiants (deux paquets, un titre) : une ligne par titre.
        foreach (var v in proposees.Where(p => p.Genre == GenreDeMiseAJour.NouvelleVersion).DistinctBy(p => p.Titre, StringComparer.OrdinalIgnoreCase))
            yield return new RepairFinding("WINDOWS UPDATE", "nouvelle version de Windows proposée", v.Titre,
                versionInstallee is null ? null : Docteur.T("WU.VersionInstallee", versionInstallee), RepairStatus.Observed,
                Docteur.T("WU.Proposee", v.Titre, versionInstallee is null ? "" : Docteur.T("WU.TuEsEn", versionInstallee)),
                RepairAction.Recommandation);
    }
}

/// <summary>Le vrai : l'API Windows Update (Microsoft.Update.Session), recherche hors ligne (dernier résultat de
/// Windows, sans réseau ; 2,4 s mesurées le 06/10/2026), et la date de la dernière recherche réussie.</summary>
[SupportedOSPlatform("windows")]
public sealed class LecteurWindowsUpdate : ILecteurWindowsUpdate
{
    private readonly string _marque;

    /// <param name="marque">La date de la dernière recherche lancée par DrDXT ; absent = celle du profil.</param>
    public LecteurWindowsUpdate(string? marque = null) => _marque = marque ?? RechercheWindowsUpdate.MarqueParDefaut;

    /// <summary>La plus récente des deux recherches réussies : celle de Windows et celle que DrDXT a lancée. Une marque
    /// illisible ne compte pas.</summary>
    internal static DateTimeOffset? LaPlusRecente(DateTimeOffset? windows, string? marque)
    {
        if (!DateTimeOffset.TryParse(marque?.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var drdxt))
            return windows;
        return windows is { } w && w >= drdxt ? w : drdxt;
    }

    public Result<EtatWindowsUpdate> Lire()
    {
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
            dynamic chercheur = session.CreateUpdateSearcher();
            chercheur.Online = false;
            var enAttente = Chercher(chercheur, "IsInstalled=0 and IsHidden=0");
            // Ce qui ne s'installe qu'au clic n'apparaît qu'avec ce critère (Microsoft Learn, IUpdateSearcher::Search).
            // Une panne de cette seconde recherche ne retire pas la première : la liste reste vide.
            // Une panne de cette seconde recherche est « non lu » (null), pas « rien de proposé ».
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
            // LastSearchSuccessDate ne compte que les recherches de Windows lui-même (Microsoft Learn : « Only the
            // AutomaticUpdates object will update this property ») ; celle que DrDXT a lancée compte aussi.
            try { derniere = LaPlusRecente(derniere, File.Exists(_marque) ? File.ReadAllText(_marque) : null); }
            catch (IOException) { /* marque illisible : la date de Windows seule */ }

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

    /// <summary>Type 2 = pilote (IUpdate.Type) ; les autres, par identifiant de catégorie.</summary>
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

/// <summary>
/// La recherche de Windows Update relancée par DrDXT : l'API Windows Update Agent (Microsoft Learn,
/// IUpdateSearcher::Search), en ligne, sans droits d'administrateur. UsoClient n'est pas documenté. Windows ne compte pas
/// cette recherche dans sa propre date ; la réussite est donc notée à part, et <see cref="LecteurWindowsUpdate"/> la relit.
/// </summary>
[SupportedOSPlatform("windows")]
public static class RechercheWindowsUpdate
{
    public static string MarqueParDefaut => DossierDonnees.Chemin("recherche-windows-update.txt");

    /// <summary>Cherche en ligne ce qui attend ; la réussite est notée dans <paramref name="marque"/>. Bloquant : peut
    /// durer plusieurs minutes, à lancer hors du fil de l'interface.</summary>
    public static Result Chercher(string? marque = null)
    {
        try
        {
            dynamic session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!)!;
            dynamic chercheur = session.CreateUpdateSearcher();
            chercheur.Online = true;
            chercheur.Search("IsInstalled=0 and IsHidden=0");
            // Fichier du compte : jamais à travers un nom posé d'avance en lien (l'App peut tourner élevée).
            return new FichierJsonAtomique(marque ?? MarqueParDefaut, ErrorCodes.IoFailure)
                .WriteAtomic(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, ex.Message);
        }
    }
}
