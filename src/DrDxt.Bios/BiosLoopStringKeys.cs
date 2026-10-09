using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Resx keys for the post-reboot closed-loop result: the honest "it took" /
/// "it did not take" / "could not verify" labels and the recovery card shown when a
/// firmware change did not take or could not be verified. FR+en live in the shared
/// Infrastructure resx; the verifier emits only these keys, never hard-coded UI text
/// (mirrors <c>BiosStringKeys</c> / <c>BiosGuideStringKeys</c> / <c>ErrorCodes</c>).
/// </summary>
public static class BiosLoopStringKeys
{
    // ── Per-setting verdict labels (honest constat). ──
    public const string VerdictTook = "Bios.Loop.Verdict.Took";
    public const string VerdictDidNotTake = "Bios.Loop.Verdict.DidNotTake";
    public const string VerdictUnreadable = "Bios.Loop.Verdict.Unreadable";

    // ── Clean-success header (every setting took). ──
    public const string AllTook = "Bios.Loop.AllTook";

    // ── Nom lisible d'un reglage, pour la ligne « <reglage> : <verdict> ». Le verifieur rend
    //    un FirmwareSettingKind ; l'afficher brut mettrait du texte non traduit a l'ecran, et
    //    le mapper en dur dans le VM contournerait la discipline « aucun texte code en dur ». ──
    public const string SettingNamePrefix = "Bios.Loop.Setting.";

    /// <summary>La cle du nom lisible d'un reglage. Ferme par construction : le nom du membre
    /// de l'enumeration est le suffixe, donc un nouveau reglage sans sa cle se voit tout de
    /// suite a l'ecran au lieu de tomber silencieusement sur un libelle generique.</summary>
    public static string SettingName(FirmwareSettingKind kind) => SettingNamePrefix + kind;

    // ── Recovery card (shown only when something did not take or is unreadable). The
    //    order below is the order the card is rendered in — D2. ──
    public const string RecoveryHeader = "Bios.Loop.Recovery.Header";
    public const string RecoveryReenter = "Bios.Loop.Recovery.Reenter";
    public const string RecoveryOneChangeAtATime = "Bios.Loop.Recovery.OneChangeAtATime";
    public const string RecoveryLoadDefaults = "Bios.Loop.Recovery.LoadDefaults";
    public const string RecoveryClearCmos = "Bios.Loop.Recovery.ClearCmos";

    /// <summary>The recovery-card keys, in render order (header first). Single source of truth.</summary>
    public static readonly IReadOnlyList<string> RecoveryCard = new[]
    {
        RecoveryHeader,
        RecoveryReenter,
        RecoveryOneChangeAtATime,
        RecoveryLoadDefaults,
        RecoveryClearCmos,
    };
}
