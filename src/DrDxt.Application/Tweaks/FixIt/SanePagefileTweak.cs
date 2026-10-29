using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Réparer un fichier d'échange désactivé. « Couper le pagefile pour gagner des FPS » est un mythe (recherche du projet,
// non publiée) qui laisse la machine cassée : à la limite de commit, Windows manque de mémoire (gel, plantage). Signalé
// au scan (`Finding.Memory.PagefileDisabled`) ; la configuration est la seule valeur `PagingFiles` (REG_MULTI_SZ), sans
// op WMI, une feuille exacte de l'allowlist. Un levier de fiabilité, jamais un gain d'images ; effet au redémarrage.
// -----------------------------------------------------------------------
/// <summary>Rend à Windows la gestion du fichier d'échange (`PagingFiles`) quand il est lu désactivé ; effet au redémarrage.</summary>
public sealed class SanePagefileTweak : ITweak
{
    private const string MemoryManagementPath =
        @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesName = "PagingFiles";

    // La valeur exacte qu'écrit Windows pour « Gestion automatique du fichier d'échange pour tous les lecteurs », relevée
    // sur une machine réelle (Windows 11 26200) : `?:\pagefile.sys`, sans taille. `?:` = Windows choisit lecteur et
    // taille ; « 0 0 » décrit une taille gérée sur un lecteur nommé (`C:\pagefile.sys 0 0`), un état que l'interface de
    // Windows ne produit jamais ici.
    private const string SystemManagedEntry = @"?:\pagefile.sys";

    private readonly IRegistryReader _registry;
    private readonly IElevatedClient _elevated;

    public SanePagefileTweak(IRegistryReader registry, IElevatedClient elevated)
        => (_registry, _elevated) = (registry, elevated);

    public string Id => "fixit.sane_pagefile";
    public TweakCategory Category => TweakCategory.FixIt;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    // Effet au redémarrage : un A/B immédiat mesurerait le bruit sur un changement inactif, et l'annulerait à tort.
    public bool TakesEffectAfterReboot => true;

    public string SummaryKey => "tweak.fixit.sane_pagefile.reliability";

    /// <summary>Seulement quand le fichier d'échange est lu désactivé ; `Unknown` ne déclenche rien.</summary>
    public bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.PagefileConfigured == TriState.Off;

    /// <summary>Illisible n'est pas « déjà bon ».</summary>
    public bool EtatNonLu(SettingsState state) => state.PagefileConfigured == TriState.Unknown;

    public async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // L'original avant d'écrire ; absent → l'inverse est une suppression, pas une chaîne vide (deux états différents
            // pour Windows).
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

            var pose = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath,
                                              PagingFilesName, RegistryValueType.MultiString, SystemManagedEntry);
            var applied = await _elevated.ExecuteAsync(pose, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            // La valeur posée voyage avec le snapshot : sans elle, la relecture après pose ne peut rien prouver.
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, PrivilegedOpCodec.Serialize(pose).Value));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Page file apply boundary error: {ex.Message}");
        }
    }

    public async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
        var op = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        if (!op.IsOk || op.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Page file snapshot is not a known typed op.");
        return await _elevated.ExecuteAsync(op.Value, CancellationToken.None).ConfigureAwait(false);
    }
}
