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
/// <param name="Scene">La scene declaree pour la session (<see cref="SceneContract.Representativite"/>),
/// recopiee du contrat qui a reellement atteint la session. Sans elle, le corpus ne
/// saurait pas si un keep est ne en partie reelle ou dans un lobby. <c>null</c> = ligne ecrite avant
/// ce champ (non consignee), a ne jamais confondre avec <see cref="SceneRepresentativity.Unknown"/>
/// (« je ne sais pas », declare).</param>
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
/// Le reservoir des candidats juges : un fichier JSONL en ajout seul, une ligne par candidat.
///
/// <see cref="KeepDecision.Queue"/> nomme la grandeur de queue qui a valide un keep, pour trancher
/// un jour si le p95 seul merite de garder un reglage. <see cref="CandidateResult"/> ne vit que le
/// temps d'une session : ce fichier garde le corpus qui permettra de trancher.
///
/// Ajout seul, jamais reecrit : une ligne a moitie ecrite par une panne ne coute qu'elle-meme,
/// alors qu'une reecriture complete ratee couterait tout l'historique. C'est la raison de ne pas
/// passer par <see cref="FichierJsonAtomique"/>, qui remplace le fichier entier.
///
/// Il ne decide rien et ne change aucun critere. Et un echec d'ecriture ne fait jamais echouer
/// la session : <see cref="Consigner"/> ne leve pas, il rend un <see cref="Result"/>.
/// </summary>
public sealed class JournalDesCandidats
{
    public const string NomFichier = "candidate-journal.jsonl";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
        // Une preuve invalide porte NaN, et le JSON par defaut refuse de l'ecrire. Un seul
        // candidat non decidable faisait perdre toutes les lignes de la session.
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

    /// <summary>Ce que la ligne dit du verdict. Un candidat jamais mesure (non tente, ecriture qui
    /// n'a pas pris, instabilite) n'est ni garde ni rejete : la session n'a pas pu en decider.</summary>
    public static string Decision(CandidateOutcome issue) => issue switch
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
                    Decision(r.Outcome), r.Outcome, r.Queue, r.PassesAb,
                    (r.Metriques ?? Array.Empty<MetricEvidence>())
                        .Select(m => new EcartJuge(m.Metrique, m.Preuve.DeltaPct, m.Preuve.ResolutionPct, m.Preuve.Outcome))
                        .ToList(),
                    profil.Scene2?.Representativite, profil.Scene2?.Role, profil.Scene2?.Description);
                lignes.Append(JsonSerializer.Serialize(ligne, Options)).Append('\n');
            }

            var dossier = Path.GetDirectoryName(_chemin);
            if (!string.IsNullOrEmpty(dossier)) Directory.CreateDirectory(dossier);
            File.AppendAllText(_chemin, lignes.ToString());
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, Docteur.T("TC.JournalCandidats", ex.Message));
        }
    }
}
