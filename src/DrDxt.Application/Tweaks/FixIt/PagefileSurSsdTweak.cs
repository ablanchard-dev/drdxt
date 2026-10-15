using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.FixIt;

// -----------------------------------------------------------------------
// Le fichier d'échange rendu au disque système quand il est sur un disque dur et que Windows est sur un SSD.
//
// Ce qui déborde de la mémoire (chargements, retour au bureau) passe par le fichier d'échange : sur un disque dur, ça se
// sent. Le geste à la main (sysdm.cpl, Mémoire virtuelle) revient à rendre la gestion à Windows, qui le pose sur le disque
// système. DrDXT écrit la même valeur que « Gestion automatique » (`?:\pagefile.sys`, celle de fixit.sane_pagefile).
//
// Proposé seulement quand chaque entrée de `PagingFiles` est une taille gérée par le système sur un lecteur nommé (une
// taille choisie à la main reste au joueur), qu'une est sur un disque dur et que Windows est sur un SSD
// (FichierDEchangeADeplacer). La valeur d'avant est relue juste avant et rendue telle quelle par l'Annuler. Effet au
// redémarrage ; fiabilité et fluidité hors jeu, jamais un gain d'images annoncé.
// -----------------------------------------------------------------------
/// <summary>Rend à Windows la gestion du fichier d'échange posé sur un disque dur, quand Windows est sur un SSD ; effet au redémarrage.</summary>
public sealed class PagefileSurSsdTweak : FixItTweakBase
{
    private const string MemoryManagementPath = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";
    private const string PagingFilesName = "PagingFiles";
    private const string GereParWindows = @"?:\pagefile.sys";

    private readonly IRegistryReader _registry;

    public PagefileSurSsdTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated) => _registry = registry;

    public override string Id => "fixit.pagefile_ssd";
    public override string SummaryKey => "tweak.fixit.pagefile_ssd.summary";
    public override bool TakesEffectAfterReboot => true;

    public override bool AppliesTo(HardwareInventory hw, SettingsState state) => state.FichierDEchangeADeplacer == TriState.On;

    public override bool EtatNonLu(SettingsState state) => state.FichierDEchangeADeplacer == TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var lu = _registry.ReadMultiString(RegistryHiveKind.LocalMachine, MemoryManagementPath, PagingFilesName);
            var entrees = lu.IsOk ? (lu.Value ?? "").Split('\n').Select(e => e.Trim()).Where(e => e.Length > 0).ToList() : null;
            if (entrees is not { Count: > 0 } || !entrees.All(FichierDEchangeADeplacer.EntreeGereeParLeSysteme))
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Page file configuration unread, or no longer system-managed on a named drive: nothing to write.");

            var inverse = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath,
                PagingFilesName, RegistryValueType.MultiString, lu.Value!));
            var op = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, MemoryManagementPath, PagingFilesName,
                RegistryValueType.MultiString, GereParWindows);
            var pose = PrivilegedOpCodec.Serialize(op);
            if (!inverse.IsOk || inverse.Value is null || !pose.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert op.");

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            return applied.IsOk
                ? Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, inverse.Value, pose.Value))
                : Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, $"Page file to SSD apply boundary error: {ex.Message}");
        }
    }
}
