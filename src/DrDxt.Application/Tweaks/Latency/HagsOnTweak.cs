using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// HagsOnTweak (système et latence, persistant).
//
// Active la planification GPU accélérée par le matériel (HAGS) par la feuille autorisée
// ...\GraphicsDrivers\HwSchMode (DWORD 2 = activé, 1 = désactivé, comme RegistryProvider.GetHags ;
// sources : learn.microsoft.com/answers question 4137979 et howtogeek.com/756935 ; sans support du
// pilote, Windows l'ignore), écrite par le helper élevé. L'original est relu d'abord, l'op inverse
// rangée dans le snapshot, puis 2 est écrit. Persiste au redémarrage (registre), réconciliable.
//
// Effet au redémarrage seulement : le moteur saute l'A/B immédiat (TakesEffectAfterReboot = true) ;
// le gain se prouve au prochain scan en jeu, jamais dans la session en cours.
//
// Proposé, jamais posé en silence : l'effet varie selon la carte, le pilote et le jeu, donc le
// SummaryKey décrit le changement (jamais « +FPS ») et la preuve est l'A/B mesuré de la session suivante.
//
// Gating : seulement quand l'état effectif (lu par D3DKMT) est désactivé ; inconnu, rien n'est proposé.
// Tous fabricants (AMD, Intel, NVIDIA, WDDM 2.7+).
// -----------------------------------------------------------------------
/// <summary>Active HAGS (HwSchMode = 2) quand Windows le lit désactivé ; effet au redémarrage.</summary>
public sealed class HagsOnTweak : LatencyTweakBase
{
    private const uint HagsOn = 2; // 2 = On, 1 = Off (HwSchMode).
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string HwSchModeName = "HwSchMode";

    private readonly IRegistryReader _registry;

    public HagsOnTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.hagsOn";
    public override string SummaryKey => "tweak.latency.hags.enable";

    // Une cle `HwSchMode` absente ne veut pas dire « HAGS eteint » : personne n'a ecrit ce reglage,
    // et Windows choisit alors son propre defaut, selon la version, le GPU et le pilote.
    //
    // Un candidat fonde sur une absence de cle est un candidat fonde sur rien : il ecrirait un
    // reglage pour « corriger » un etat que personne n'a lu, et l'A/B qui suit mesurerait un
    // changement dont on ne connait meme pas le point de depart.
    //
    // Le gate lit donc l'etat effectif par D3DKMT (`SettingsState.HagsEffective`) ; la cle ne sert
    // qu'a l'ecriture et au revert. Effectif = On : « deja bien regle », pas un trou.
    // Unknown (non lu, non supporte) ne se corrige pas.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.HagsEffective == TriState.Off;

    // `HwSchMode` absent du registre => Unknown. Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) => state.HagsEffective == TriState.Unknown;

    // HwSchMode n'agit qu'après un redémarrage → le moteur saute l'A/B immédiat (preuve = scan suivant).
    public override bool TakesEffectAfterReboot => true;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Relever la vraie origine avant d'écrire (retour arrière fidèle).
            var read = _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original HwSchMode: {read.Error?.Code}.");

            var inverse = BuildRegistryInverse(
                RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName, read.Value);
            if (!inverse.IsOk || inverse.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Activer HAGS (=2) par l'op typée sur liste blanche.
            var op = new SetRegistryValueOp(
                RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName,
                RegistryValueType.DWord,
                HagsOn.ToString(System.Globalization.CultureInfo.InvariantCulture));

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverse.Value, op));
        }
        catch (Exception ex)
        {
            // Chaque frontière externe protégée → échec propre, jamais d'exception.
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"HAGS enable apply boundary error: {ex.Message}");
        }
    }
}
