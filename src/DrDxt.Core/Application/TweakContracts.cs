namespace DrDxt.Core;

// --- P2 Application ---

public enum TweakCategory { Latency, GpuTuning, PowerPlan, FixIt, Bios /*guided*/ }

/// <param name="TweakId">Le levier.</param>
/// <param name="OriginalValueJson">L'op INVERSE serialisee : de quoi remettre exactement l'etat
/// d'avant. C'est ce que le revert rejoue.</param>
/// <param name="AppliedValueJson">
/// L'op avant serialisee : ce que le levier a demande d'ecrire.
///
/// Sans elle, le produit ne peut pas prouver qu'un levier est applique. Le snapshot ne portait
/// que l'etat original : la relecture savait donc dire « la valeur a change », jamais « la valeur
/// Cible est en place ». Un tiers qui aurait pose une troisieme valeur donnait exactement le meme
/// verdict — et le moteur mesurait ensuite le gain d'un levier dont l'application n'etait pas
/// etablie.
///
/// Optionnelle et en queue : toutes les constructions existantes restent valides, et un
/// snapshot ecrit avant ce champ rend <c>null</c>. Inconnu n'est pas « applique » : la verification
/// rendra alors <c>Unknown</c>, jamais un verdict favorable.
/// </param>
public record TweakSnapshot(string TweakId, string OriginalValueJson, string? AppliedValueJson = null);

public interface ITweak
{
    string Id { get; }
    TweakCategory Category { get; }
    bool RequiresElevation { get; }
    bool RequiresRestorePoint { get; }

    /// <summary>True when the tweak only takes effect after a reboot / next game launch
    /// (HAGS, Game Mode, Game DVR, flip-model, VRR…). Such a change is not present during an
    /// immediate post-apply re-bench, so the engine must skip the A/B for it (session noise
    /// must never auto-revert a change whose effect isn't even live yet); the gain is proven
    /// by the NEXT in-game scan instead. Defaults to false — a normal tweak takes effect now
    /// and is A/B-verified immediately.</summary>
    bool TakesEffectAfterReboot => false;

    /// <summary>Clé de ressource de la phrase montrée à l'utilisateur pour ce levier. Chaque
    /// tweak en avait déjà une, mais elle n'était pas dans le contrat : rien ne pouvait donc
    /// vérifier qu'elle existe en EN et en FR, et le garde-fou i18n devait lister les clés à la
    /// main — il en avait raté cinq. Dans l'interface, elle devient dérivable, donc prouvable.</summary>
    string SummaryKey { get; }

    bool AppliesTo(HardwareInventory hw, SettingsState state); // hardware gating (laptop/iGPU/handheld)

    /// <summary>
    /// Vrai quand <see cref="AppliesTo"/> rend <c>false</c> parce que l'etat de ce levier n'a
    /// Pas pu etre lu — pas parce que la machine est deja reglee.
    ///
    /// Mesure du 17/09/2026 sur la machine de reference (`scanprobe --plan`) : les dix leviers
    /// du plan sortaient « Not applicable to this machine », plan vide, ecran muet. Trois d'entre
    /// eux etaient en realite INCONNUS : `AllowAutoGameMode` et `HwSchMode` n'existent pas dans ce
    /// registre, le lecteur rend donc <c>Unknown</c>, et <c>Unknown != Off</c> ferme la porte en
    /// silence. Le client lisait « ca ne te concerne pas » la ou la verite etait « je n'ai pas su
    /// regarder » — le defaut exact que ce produit a deja corrige ailleurs : <b>Auto vaut UNKNOWN,
    /// pas OK</b>.
    ///
    /// Par defaut <c>false</c> : un levier dont l'applicabilite ne depend que du materiel (chassis,
    /// marque de GPU) sait toujours repondre, il n'a rien a declarer ici.
    /// </summary>
    bool EtatNonLu(SettingsState state) => false;
    Task<Result<TweakSnapshot>> ApplyAsync();
    Task<Result> RevertAsync(TweakSnapshot snapshot);
}

public interface ISafetyManager
{
    Task<Result> EnsureRestorePointAsync(); // blocks if impossible
    Task<Result> RevertAsync(TweakSnapshot snapshot);

    /// <summary>14/09/2026 — quand Windows a garde un point existant (moins de 24 h) au lieu d'en creer
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
