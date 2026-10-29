namespace DrDxt.Core;

// --- Application ---

public enum TweakCategory { Latency, GpuTuning, PowerPlan, FixIt, Bios /*guided*/ }

/// <param name="TweakId">Le levier.</param>
/// <param name="OriginalValueJson">L'op inverse serialisee, que le revert rejoue pour remettre l'etat d'avant.</param>
/// <param name="AppliedValueJson">L'op avant serialisee, ce que le levier a demande d'ecrire : sans elle, la relecture
/// dit « la valeur a change », jamais « la cible est en place » (une troisieme valeur posee par un tiers donnerait le meme
/// verdict). <c>null</c> (snapshot plus ancien) : la verification rend <c>Unknown</c>, jamais un verdict
/// favorable.</param>
public record TweakSnapshot(string TweakId, string OriginalValueJson, string? AppliedValueJson = null);

public interface ITweak
{
    string Id { get; }
    TweakCategory Category { get; }
    bool RequiresElevation { get; }
    bool RequiresRestorePoint { get; }

    /// <summary>Vrai quand le levier ne prend effet qu'après un redémarrage ou au prochain lancement du jeu (HAGS, Game
    /// Mode, Game DVR, modèle flip, VRR…) : le moteur saute son A/B, le bruit de session n'annule jamais un changement pas
    /// encore actif ; le gain se prouve au scan en jeu suivant. Faux par défaut.</summary>
    bool TakesEffectAfterReboot => false;

    /// <summary>Clé de ressource de la phrase montrée pour ce levier. Dans le contrat, le garde-fou i18n la dérive et
    /// prouve qu'elle existe en EN et en FR, au lieu d'une liste tenue à la main.</summary>
    string SummaryKey { get; }

    bool AppliesTo(HardwareInventory hw, SettingsState state); // condition matérielle (portable/iGPU/console portable)

    /// <summary>
    /// Vrai quand <see cref="AppliesTo"/> rend <c>false</c> parce que l'etat n'a pas pu etre lu, pas parce que la machine
    /// est deja reglee. Mesuré le 17/09/2026 sur la machine de reference (`scanprobe --plan`) : `AllowAutoGameMode` et `HwSchMode`
    /// absents du registre rendaient <c>Unknown</c>, et <c>Unknown != Off</c> donnait « Not applicable » la ou la verite
    /// etait « je n'ai pas su regarder » (Auto vaut Unknown, pas OK). Faux par defaut : un levier qui ne depend que du
    /// materiel sait toujours repondre.
    /// </summary>
    bool EtatNonLu(SettingsState state) => false;

    /// <summary>Le banc profond (--valider-profond), pour un levier déjà dans son état cible : l'origine exacte lue
    /// maintenant et l'état non cible à poser le temps de l'exercice, sur la même feuille (rejouées par son RevertAsync,
    /// donc par la liste blanche). <c>null</c> = pas d'exercice sûr (par défaut) : « pas touché », et c'est dit.</summary>
    ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state) => null;
    Task<Result<TweakSnapshot>> ApplyAsync();
    Task<Result> RevertAsync(TweakSnapshot snapshot);
}

/// <summary>Les deux ops d'un exercice profond, sérialisées comme dans un snapshot : l'origine exacte et l'état non cible.</summary>
public sealed record ExerciceProfond(string OrigineJson, string HorsCibleJson);

public interface ISafetyManager
{
    Task<Result> EnsureRestorePointAsync(); // bloque si impossible
    Task<Result> RevertAsync(TweakSnapshot snapshot);

    /// <summary>Quand Windows a garde un point existant (moins de 24 h) au lieu d'en creer
    /// un : sa date (UTC), pour que l'ecran le dise. <c>null</c> = un point neuf, ou aucun encore.</summary>
    DateTime? PointReutiliseUtc => null;
}

public record TweakPlan(IReadOnlyList<string> TweakIds);

public record ApplyOutcome(
    string TweakId,
    bool Applied,
    bool AutoReverted,
    TweakSnapshot? Snapshot,
    Error? Error);

public record ApplyReport(IReadOnlyList<ApplyOutcome> Outcomes, AbVerdict? Verdict);

public interface ITweakEngine
{
    Task<Result<ApplyReport>> ApplyAsync(TweakPlan plan, BenchmarkResult? baseline, int targetPid, CancellationToken ct);
}

public interface ITweakCatalog
{
    Result<ITweak> Get(string tweakId);
    IReadOnlyList<ITweak> All();
}
