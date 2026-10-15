using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Réparer un fichier d'échange désactivé.
//
// « Couper le pagefile pour gagner des FPS » est un mythe répandu, classé comme tel par la
// recherche du projet (non publiée). Écarté du catalogue, il laisse quand même la machine cassée
// quand l'utilisateur l'a appliqué avant d'installer DrDXT : à la limite de commit, Windows tombe
// en manque de mémoire (gel, plantage), et l'utilisateur accuse le jeu.
//
// Le scan le signale (`Finding.Memory.PagefileDisabled`). La configuration vit dans une simple
// valeur de registre, `PagingFiles` (REG_MULTI_SZ), que le lecteur du produit lit déjà ; pas besoin
// d'op WMI. La feuille reste unique (allowlist à la feuille exacte).
//
// C'est un levier de fiabilité, jamais un gain d'images. Sa phrase ne parle pas de FPS, et il ne
// prend effet qu'au redémarrage.
// -----------------------------------------------------------------------
/// <summary>Rend à Windows la gestion du fichier d'échange (`PagingFiles`) quand il est lu désactivé ; effet au redémarrage.</summary>
public sealed class SanePagefileTweak : ITweak
{
    private const string MemoryManagementPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesName = "PagingFiles";

    // La valeur exacte que Windows lui-même écrit quand « Gestion automatique du fichier
    // d'échange pour tous les lecteurs » est cochée. Relevée sur une machine réelle
    // (Windows 11 26200, gestion automatique active) : `?:\pagefile.sys`, sans taille.
    //
    // La forme `?:\pagefile.sys 0 0` n'est pas la bonne : « 0 0 » existe, mais décrit
    // une taille gérée par le système sur un lecteur nommé (`C:\pagefile.sys 0 0`), pas la
    // gestion automatique. Écrire une forme voisine mais non canonique laisserait la machine
    // dans un état que l'interface de Windows ne produit jamais. Le `?:` est le marqueur de
    // l'automatique : c'est Windows qui choisit le lecteur et la taille.
    private const string SystemManagedEntry = @"?:\pagefile.sys";

    private readonly IRegistryReader _registry;
    private readonly IElevatedClient _elevated;

    public SanePagefileTweak(IRegistryReader registry, IElevatedClient elevated)
        => (_registry, _elevated) = (registry, elevated);

    public string Id => "fixit.sane_pagefile";
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    // La configuration du fichier d'échange ne s'applique qu'au redémarrage : un A/B immédiat
    // mesurerait le bruit de session sur un changement qui n'est même pas actif, et pourrait
    // l'annuler à tort.
    public bool TakesEffectAfterReboot => true;

    public string SummaryKey => "tweak.fixit.sane_pagefile.reliability";

    /// <summary>Uniquement quand le fichier d'échange est lu comme désactivé. `Unknown`
    /// (illisible) ne déclenche rien : on ne répare pas ce qu'on n'a pas su lire.</summary>
    public bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.PagefileConfigured == TriState.Off;

    /// <summary>Illisible n'est pas « déjà bon ».</summary>
    public bool EtatNonLu(SettingsState state) => state.PagefileConfigured == TriState.Unknown;

    public async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Capture de l'original avant d'écrire. Absent (null) → l'inverse est une
            // suppression de valeur, pas une écriture de chaîne vide : les deux états ne sont
            // pas les mêmes pour Windows.
            var capture = _registry.ReadMultiString(RegistryHiveKind.LocalMachine,
                                                    MemoryManagementPath, PagingFilesName);
            if (!capture.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read the current page file configuration: {capture.Error?.Code}.");

            PrivilegedOp inverse = capture.Value is null
                ? new DeleteRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath, PagingFilesName)
                : new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath,
                                         PagingFilesName, RegistryValueType.MultiString, capture.Value);

            var snapshotJson = PrivilegedOpCodec.Serialize(inverse);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await _elevated.ExecuteAsync(
                new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath,
                                       PagingFilesName, RegistryValueType.MultiString, SystemManagedEntry),
                CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Page file apply boundary error: {ex.Message}");
        }
    }

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        var op = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        if (!op.IsOk || op.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Page file snapshot is not a known typed op.");
        return await _elevated.ExecuteAsync(op.Value, CancellationToken.None).ConfigureAwait(false);
    }
}
