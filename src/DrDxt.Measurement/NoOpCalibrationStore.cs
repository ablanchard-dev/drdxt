using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement;

/// <summary>
/// Le journal des controles : il accumule, il ne corrige rien (sous vingt observations, <see cref="NoOpCalibration"/>
/// refuse de conclure). Chaque session de controle depose un fait : l'effet valait zero, voici ce que la chaine a trouve
/// et annonce ; a terme, le produit dira s'il est surconfiant, calibre ou aveugle, par grandeur et par protocole. Le
/// protocole fait partie de la cle : 8 alternances de 10 s ne disent rien de 24. Meme frontiere disque que
/// <see cref="PerformanceJournalStore"/> : une seule chaine traverse, le magasin se teste sans fichier.
/// </summary>
public sealed class NoOpCalibrationStore
{
    /// <summary>Des mois de controles, lisible a la main. Les plus anciennes s'elaguent : ici toutes les observations se
    /// valent (pas de premiere mesure de reference comme pour la derive), et les recentes decrivent mieux la machine.</summary>
    public const int MaximumEntrees = 500;

    // Une resolution peut valoir +infini (PairedAb, zero degre de liberte), que le JSON par defaut refuse d'ecrire.
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly IPerformanceJournalFile _fichier;

    public NoOpCalibrationStore(IPerformanceJournalFile fichier) => _fichier = fichier;

    /// <summary>Un journal illisible n'empeche pas de mesurer : on juge sur rien, et <see cref="NoOpCalibration"/>
    /// refuse de conclure.</summary>
    public IReadOnlyList<NoOpObservation> Lire()
        => LireOuEchec() is { IsOk: true } lu ? lu.Value! : Array.Empty<NoOpObservation>();

    /// <summary>La lecture qui dit son echec : absent ou vide = journal vide ; lecture ratee ou JSON illisible = echec.</summary>
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
                "Control log unreadable: " + ex.Message);
        }
    }

    /// <summary>Ajoute les observations d'une session de controle ; un echec d'ecriture se dit. Sans lecture sure, on
    /// n'ecrit pas : une liste lue vide par erreur ecraserait des semaines de controles.</summary>
    public Result Ajouter(IReadOnlyList<NoOpObservation> nouvelles)
    {
        if (nouvelles is null || nouvelles.Count == 0) return Result.Ok();

        var existant = LireOuEchec();
        if (!existant.IsOk)
            return Result.Fail(existant.Error!.Value.Code,
                "Controls not added, so as not to overwrite a log DrDXT could not read: "
                + existant.Error.Value.Message);

        var tout = existant.Value!.Concat(nouvelles)
            .OrderBy(o => o.QuandUtc)
            .ToList();

        if (tout.Count > MaximumEntrees)
            tout = tout.Skip(tout.Count - MaximumEntrees).ToList();

        return _fichier.WriteAtomic(JsonSerializer.Serialize(tout, Options));
    }

    /// <summary>Ce que les controles accumules disent d'une grandeur, pour un protocole donne (8 alternances de 10 s ne
    /// disent rien de 24).</summary>
    public CalibrationRapport Juger(GameMetricKind metrique, double secondesParPasse, int passes) =>
        NoOpCalibration.Juger(
            Lire().Where(o => Math.Abs(o.SecondesParPasse - secondesParPasse) < 0.01
                              && o.Passes == passes).ToList(),
            metrique);
}
