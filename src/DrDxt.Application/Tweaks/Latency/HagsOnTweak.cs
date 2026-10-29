using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// Système et latence, persistant, réconciliable. Active HAGS par la feuille autorisée ...\GraphicsDrivers\HwSchMode
// (DWORD 2 = activé, 1 = désactivé, comme RegistryProvider.GetHags ; sources : learn.microsoft.com/answers question
// 4137979 et howtogeek.com/756935 ; sans support du pilote, Windows l'ignore), écrite par le helper élevé après relecture
// de l'original (op inverse dans le snapshot). Effet au redémarrage seulement (TakesEffectAfterReboot) : le gain se
// prouve au prochain scan en jeu. Proposé, jamais posé en silence, sans « +FPS » : l'effet varie selon carte, pilote, jeu.
// Gating : seulement quand l'état effectif (lu par D3DKMT) est désactivé ; inconnu, rien. Origine : absente ou 1/2
// seulement, le helper ne réécrivant HwSchMode qu'à 1 ou 2 (OpAllowlist) ; une autre valeur (0, un autre outil) ne se
// remettrait jamais : refus OrigineNonRendable. Tous fabricants (AMD, Intel, NVIDIA, WDDM 2.7+).
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

    // Une cle `HwSchMode` absente ne veut pas dire « HAGS eteint » : Windows choisit son defaut (version, GPU, pilote).
    // Le gate lit l'etat effectif par D3DKMT (`SettingsState.HagsEffective`) ; la cle ne sert qu'a l'ecriture et au
    // revert. On = « deja bien regle » ; Unknown (non lu, non supporte) ne se corrige pas.
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.HagsEffective == TriState.Off;

    // `HwSchMode` absent du registre => Unknown. Ne rien ecrire, mais le dire.
    public override bool EtatNonLu(SettingsState state) => state.HagsEffective == TriState.Unknown;

    // HwSchMode n'agit qu'après un redémarrage → le moteur saute l'A/B immédiat (preuve = scan suivant).
    public override bool TakesEffectAfterReboot => true;

    // Banc profond : HAGS effectif, la clé absente (défaut du pilote) ou à 2 ; l'exercice écrit 1, puis rend l'origine
    // exacte. Une clé à 1 sous un HAGS effectif est un redémarrage en attente : pas d'exercice.
    public override ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state)
    {
        if (state.HagsEffective != TriState.On) return null;
        try
        {
            if (_registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName) is not { IsOk: true, Value: null or HagsOn } lu)
                return null;
            var origine = BuildRegistryInverse(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName, lu.Value);
            var horsCible = PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, GraphicsDriversPath,
                HwSchModeName, RegistryValueType.DWord, "1"));
            return origine is { IsOk: true, Value: { } o } && horsCible is { IsOk: true, Value: { } h } ? new ExerciceProfond(o, h) : null;
        }
        catch (Exception) { return null; }
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Relever la vraie origine avant d'écrire (retour arrière fidèle).
            var read = _registry.ReadDword(RegistryHiveKind.LocalMachine, GraphicsDriversPath, HwSchModeName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original HwSchMode: {read.Error?.Code}.");
            if (read.Value is uint lu && lu is not (1 or 2))
                return Result<TweakSnapshot>.Fail(ErrorCodes.OrigineNonRendable,
                    $"HwSchMode is {lu}: DrDXT only writes 1 or 2 back, so it is not changed.");

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
