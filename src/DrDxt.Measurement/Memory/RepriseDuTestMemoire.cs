using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// Le point d'étape du test mémoire en session : écrit dans les données de DrDXT avant le test et après chaque passe,
/// effacé à sa fin normale. S'il est encore là au lancement suivant, le test ne s'est pas fini ; un arrêt brutal (hors
/// bouton) ou une erreur matérielle grave juste après dit que c'est la machine qui s'est arrêtée pendant le test. Un
/// plantage pendant un test mémoire est un résultat, pas une absence de résultat. Rien n'est écrit sur le système.
/// </summary>
public static class RepriseDuTestMemoire
{
    public const string Fichier = "test-memoire-en-cours.json";

    /// <summary>Après la dernière passe notée, combien de temps un arrêt se rattache encore au test. **Non chiffré** :
    /// une passe dure de quelques secondes à quelques minutes ; la marge couvre la passe en cours sans aller chercher
    /// un arrêt du lendemain.</summary>
    public static readonly TimeSpan Marge = TimeSpan.FromMinutes(15);

    /// <summary>Kernel-Power 41 et une erreur fatale sont notés au démarrage qui suit la panne, pas à son heure : ce qui est
    /// noté dans ce délai après le premier démarrage suivant la dernière passe se rattache au test. **Non chiffré** : un
    /// démarrage écrit ces notes en quelques secondes, la marge couvre un démarrage lent.</summary>
    public static readonly TimeSpan NoteAuDemarrage = TimeSpan.FromMinutes(10);

    public sealed record Point(DateTimeOffset Debut, DateTimeOffset DernierePasse, int Passes);

    public static void Poser(string chemin, Point p)
        => new FichierJsonAtomique(chemin, ErrorCodes.StorageFail).WriteAtomic(JsonSerializer.Serialize(p));

    public static void Effacer(string chemin)
    {
        try { if (FichierJsonAtomique.RefusEleve(chemin, ErrorCodes.StorageFail) is null) File.Delete(chemin); }
        catch (IOException) { /* un point resté se dira « interrompu », jamais « réussi » */ }
        catch (UnauthorizedAccessException) { }
    }

    public static Point? Lire(string chemin)
    {
        try { return File.Exists(chemin) ? JsonSerializer.Deserialize<Point>(File.ReadAllText(chemin)) : null; }
        catch (Exception) { return null; }
    }

    /// <summary>Le verdict, puis le point effacé : il se dit une fois, au lancement qui suit le test, au lieu de revenir à
    /// chaque lancement. Des journaux non lus laissent le point en place, pour un verdict au lancement suivant.</summary>
    public static RepairFinding? JugerUneFois(string chemin, Point point, IReadOnlyList<DateTimeOffset>? arrets,
        IReadOnlyList<DateTimeOffset>? erreursGraves, IReadOnlyList<DateTimeOffset>? demarrages)
    {
        var verdict = Juger(point, arrets, erreursGraves, demarrages);
        if (verdict is { Statut: not RepairStatus.Unknown }) Effacer(chemin);
        return verdict;
    }

    /// <summary>Pur. <paramref name="arrets"/> : les arrêts brutaux sans l'appui long sur le bouton (un geste du joueur
    /// n'est pas un échec de la mémoire) ; <paramref name="erreursGraves"/> : les erreurs WHEA fatales ou récupérables ;
    /// <paramref name="demarrages"/> : les démarrages de Windows depuis le début du test. null = journal non lu.</summary>
    public static RepairFinding? Juger(Point? point, IReadOnlyList<DateTimeOffset>? arrets, IReadOnlyList<DateTimeOffset>? erreursGraves,
        IReadOnlyList<DateTimeOffset>? demarrages)
    {
        if (point is null) return null;
        var quand = point.Debut.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue);
        if (arrets is null || erreursGraves is null || demarrages is null)
            return new RepairFinding(TestMemoireEnSession.Domaine, TestMemoireEnSession.NomDuConstat, Docteur.T("TR.Interrompu"), null,
                RepairStatus.Unknown, Docteur.T("TR.JournauxNonLus", quand, point.Passes));
        var fin = point.DernierePasse + Marge;
        // Le premier démarrage après la dernière passe : un arrêt noté à ce démarrage-là est la panne qui a coupé le test,
        // quelle que soit l'heure où le PC a été rallumé ; un démarrage plus tardif suit un arrêt qui n'est plus le test.
        var redemarrage = demarrages.Where(d => d > point.DernierePasse).Order().Select(d => (DateTimeOffset?)d).FirstOrDefault();
        bool Pendant(DateTimeOffset d) => (d >= point.Debut && d <= fin)
            || (redemarrage is { } r && d >= r - TimeSpan.FromMinutes(1) && d <= r + NoteAuDemarrage);
        var arret = arrets.Where(Pendant).Order().FirstOrDefault();
        var erreur = erreursGraves.Where(Pendant).Order().FirstOrDefault();
        if (arret == default && erreur == default)
            return new RepairFinding(TestMemoireEnSession.Domaine, TestMemoireEnSession.NomDuConstat, Docteur.T("TR.Interrompu"), null,
                RepairStatus.Observed, Docteur.T("TR.SansPlantage", quand, point.Passes));
        var premier = new[] { arret, erreur }.Where(d => d != default).Min();
        return new RepairFinding(TestMemoireEnSession.Domaine, TestMemoireEnSession.NomDuConstat, Docteur.T("TR.Plantage"), Docteur.T("TM.AucuneErreur"),
            RepairStatus.Suspicious,
            Docteur.T(arret != default ? "TR.ArretPendant" : "TR.ErreurPendant", quand, point.Passes,
                premier.ToLocalTime().ToString(Docteur.T("ST.FormatDateHeure"), Docteur.Langue)),
            RepairAction.Recommandation);
    }
}
