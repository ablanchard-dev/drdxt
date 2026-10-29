using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>Une grandeur jugee : son ecart, le bruit contre lequel il a ete juge, et l'issue.</summary>
public sealed record EcartJuge(GameMetricKind Grandeur, double EcartPct, double BruitPct, EvidenceOutcome Issue);

/// <summary>Une ligne du journal : un candidat juge par une session.</summary>
/// <param name="Decision">`garde`, `rejete` ou `non_decidable` : voir <see cref="JournalDesCandidats.Decision"/>.</param>
/// <param name="Queue">Recopiee de <see cref="CandidateResult.Queue"/>, donc de la decision elle-meme.</param>
/// <param name="Scene">La scene declaree (<see cref="SceneContract.Representativite"/>) du contrat qui a atteint la
/// session : un keep ne en partie reelle ou dans un lobby. <c>null</c> = non consignee, jamais
/// <see cref="SceneRepresentativity.Unknown"/> (« je ne sais pas », declare).</param>
/// <param name="Role">Decouverte ou validation, meme source, meme regle du <c>null</c>.</param>
/// <param name="DescriptionScene">Ce que le joueur en a dit, mot pour mot.</param>
public sealed record CandidatJuge(
    DateTimeOffset QuandUtc,
    string? MachineId,
    string? Jeu,
    string Levier,
    string Decision,
    CandidateOutcome Issue,
    PreuveDeQueue Queue,
    int PassesAb,
    IReadOnlyList<EcartJuge> Ecarts,
    SceneRepresentativity? Scene = null,
    SceneRole? Role = null,
    string? DescriptionScene = null);

/// <summary>
/// Le reservoir des candidats juges, un JSONL, une ligne par candidat : le corpus pour trancher un jour si le p95 seul
/// (<see cref="KeepDecision.Queue"/>) merite de garder un reglage, <see cref="CandidateResult"/> ne vivant qu'une
/// session. Ajout seul, pas <see cref="FichierJsonAtomique"/> : une ligne a moitie ecrite ne coute qu'elle-meme, une
/// reecriture ratee couterait tout. Il ne decide rien ; <see cref="Consigner"/> ne leve jamais.
/// </summary>
public sealed class JournalDesCandidats
{
    public const string NomFichier = "candidate-journal.jsonl";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        // Une preuve invalide porte NaN, que le JSON par defaut refuse : sinon toute la session serait perdue.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _chemin;
    private readonly Func<string?> _machineId;
    private readonly Func<DateTimeOffset> _horloge;

    public JournalDesCandidats(string chemin, Func<string?>? machineId = null, Func<DateTimeOffset>? horloge = null)
    {
        _chemin = string.IsNullOrWhiteSpace(chemin)
            ? throw new ArgumentException("A path is required.", nameof(chemin))
            : chemin;
        _machineId = machineId ?? (() => null);
        _horloge = horloge ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>A cote de `performance-journal.json` et `repair-history.json`.</summary>
    public static string CheminParDefaut() => FichierJsonAtomique.CheminLocal(NomFichier);

    /// <summary>Le verdict de la ligne. Jamais mesure (non tente, ecriture qui n'a pas pris, instabilite) : ni garde ni
    /// rejete.</summary>
    public static string Decision(CandidateOutcome issue, string? raisonCle = null) =>
        // Une experience invalide, ou sans aucune passe exploitable, est annulee sans avoir rien decide.
        raisonCle is KeepPolicy.CleInvalide or KeepPolicy.CleAucuneMesure ? "non_decidable" : issue switch
    {
        CandidateOutcome.Kept => "garde",
        CandidateOutcome.RejectedRegression or CandidateOutcome.RejectedUnresolved => "rejete",
        _ => "non_decidable",
    };

    /// <summary>Ajoute une ligne par candidat de la session. Ne leve jamais.</summary>
    public Result Consigner(GameOptimizationProfile profil)
    {
        try
        {
            var quand = _horloge();
            var machine = _machineId();
            var lignes = new StringBuilder();
            foreach (var r in profil.Results)
            {
                var ligne = new CandidatJuge(quand, machine, profil.Identity.ProcessName, r.Id,
                    Decision(r.Outcome, r.RaisonCle), r.Outcome, r.Queue, r.PassesAb,
                    (r.Metriques ?? Array.Empty<MetricEvidence>())
                        .Select(m => new EcartJuge(m.Metrique, m.Preuve.DeltaPct, m.Preuve.ResolutionPct, m.Preuve.Outcome))
                        .ToList(),
                    profil.Scene2?.Representativite, profil.Scene2?.Role, profil.Scene2?.Description);
                lignes.Append(JsonSerializer.Serialize(ligne, Options)).Append('\n');
            }

            // Dossier du joueur : jamais à travers un nom posé d'avance en lien.
            var ajout = FichierJsonAtomique.AjouterSansLien(_chemin, lignes.ToString(), ErrorCodes.IoFailure);
            return ajout.IsOk ? Result.Ok() : Result.Fail(ErrorCodes.IoFailure, Docteur.T("TC.JournalCandidats", ajout.Error!.Value.Message));
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, Docteur.T("TC.JournalCandidats", ex.Message));
        }
    }
}
