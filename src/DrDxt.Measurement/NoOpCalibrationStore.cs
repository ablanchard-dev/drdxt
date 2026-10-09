using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Le journal des controles — il ACCUMULE, il ne corrige rien.
///
/// La tentation est de regarder trois no-op et d'« ajuster » les intervalles. Ce serait
/// exactement la faute qu'on vient de corriger ailleurs : trancher sur un echantillon trop petit.
/// Sous vingt observations, <see cref="NoOpCalibration"/> refuse de conclure, et ce magasin ne
/// fait rien d'autre que remplir le compteur.
///
/// Ce qui rend ce journal precieux est qu'il n'a pas besoin d'etre lu tout de suite. Chaque
/// session de controle depose un fait qui ne perd rien a attendre : l'effet valait zero, voici ce
/// que la chaine a trouve, voici ce qu'elle avait annonce. Dans quelques semaines, le produit
/// pourra dire s'il est surconfiant, calibre ou aveugle — par grandeur et par protocole.
///
/// Le protocole fait partie de la cle. Un intervalle calibre a 8 alternances de 10 s ne dit
/// rien de ce qu'il vaut a 24 : melanger les deux ferait une calibration moyenne de rien.
///
/// Meme frontiere disque que <see cref="PerformanceJournalStore"/>, volontairement : une seule
/// chaine traverse, donc le magasin se teste sans fichier.
/// </summary>
public sealed class NoOpCalibrationStore
{
    /// <summary>Assez pour des mois de controles, assez peu pour rester lisible a la main. On
    /// elague les plus ANCIENNES : contrairement au journal de derive, ou la premiere mesure est
    /// la reference, ici toutes les observations ont le meme statut et les recentes decrivent
    /// mieux la machine d'aujourd'hui.</summary>
    public const int MaximumEntrees = 500;

    // 26/09/2026 : une resolution peut valoir +infini (PairedAb, zero degre de liberte) ; le JSON par
    // defaut refuse de l'ecrire, et tout le lot de controles du jour etait perdu.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IPerformanceJournalFile _fichier;

    public NoOpCalibrationStore(IPerformanceJournalFile fichier) => _fichier = fichier;

    /// <summary>Un journal illisible ne doit pas empecher de mesurer aujourd'hui : on juge alors
    /// sur rien, et <see cref="NoOpCalibration"/> refuse de conclure sous vingt observations.</summary>
    public IReadOnlyList<NoOpObservation> Lire()
        => LireOuEchec() is { IsOk: true } lu ? lu.Value! : Array.Empty<NoOpObservation>();

    /// <summary>La lecture qui DIT son echec. Un fichier absent ou vide est un journal vide ; une
    /// lecture ratee ou un JSON illisible ne le sont pas.</summary>
    private Result<List<NoOpObservation>> LireOuEchec()
    {
        var brut = _fichier.Read();
        if (!brut.IsOk)
            return Result<List<NoOpObservation>>.Fail(brut.Error!.Value.Code, brut.Error.Value.Message);
        if (string.IsNullOrWhiteSpace(brut.Value))
            return Result<List<NoOpObservation>>.Ok(new List<NoOpObservation>());
        try
        {
            return Result<List<NoOpObservation>>.Ok(
                JsonSerializer.Deserialize<List<NoOpObservation>>(brut.Value!, Options) ?? new List<NoOpObservation>());
        }
        catch (Exception ex)
        {
            return Result<List<NoOpObservation>>.Fail(ErrorCodes.CaptureFail,
                "Journal des controles illisible : " + ex.Message);
        }
    }

    /// <summary>Ajoute les observations d'une session de controle. Une ecriture qui echoue SE DIT :
    /// sinon le produit croirait accumuler un historique qui n'existe pas.
    ///
    /// Corrige LE 10/09/2026 (L2, plan du 10/09). Une lecture ratee (fichier verrouille) ou un
    /// JSON illisible rendait une liste vide, puis cette methode ecrasait le fichier avec les seules
    /// observations du jour : des semaines de controles effacees en silence, par le commentaire meme
    /// qui promettait l'inverse. Sans lecture sure, on n'ecrit pas.</summary>
    public Result Ajouter(IReadOnlyList<NoOpObservation> nouvelles)
    {
        if (nouvelles is null || nouvelles.Count == 0) return Result.Ok();

        var existant = LireOuEchec();
        if (!existant.IsOk)
            return Result.Fail(existant.Error!.Value.Code,
                "Contrôles non ajoutés, pour ne pas écraser un journal que DrDXT n'a pas su lire : "
                + existant.Error.Value.Message);

        var tout = existant.Value!.Concat(nouvelles)
            .OrderBy(o => o.QuandUtc)
            .ToList();

        if (tout.Count > MaximumEntrees)
            tout = tout.Skip(tout.Count - MaximumEntrees).ToList();

        return _fichier.WriteAtomic(JsonSerializer.Serialize(tout, Options));
    }

    /// <summary>Ce que les controles accumules disent d'une grandeur, pour un protocole donne.
    ///
    /// Le filtre par protocole n'est pas un raffinement : un intervalle calibre a 8 alternances
    /// de 10 s ne dit rien de ce qu'il vaut a 24.</summary>
    public CalibrationRapport Juger(GameMetricKind metrique, double secondesParPasse, int passes) =>
        NoOpCalibration.Juger(
            Lire().Where(o => Math.Abs(o.SecondesParPasse - secondesParPasse) < 0.01
                              && o.Passes == passes).ToList(),
            metrique);
}
