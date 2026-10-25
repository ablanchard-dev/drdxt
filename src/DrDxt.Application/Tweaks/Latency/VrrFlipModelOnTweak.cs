using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// VrrFlipModelOnTweak (système, persistant).
//
// Active d'un coup les deux bascules graphiques de Windows qui partagent une seule REG_SZ :
//   * VRROptimizeEnable : « Taux de rafraîchissement variable » (VRR en fenêtré et sans bordure) ;
//   * SwapEffectUpgradeEnable : « Optimisations pour les jeux en fenêtré » (présentation flip-model).
// Ce sont les noms qu'écrivent les Paramètres de Windows eux-mêmes (relevés tels quels dans le
// registre de la machine de référence, `SwapEffectUpgradeEnable=1;`).
// Les deux vivent dans HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings,
// une chaîne « clé=valeur;... » qui porte aussi AutoHDREnable et HighPerfAdapter. D'où une fusion :
// lire la chaîne, poser nos deux clés seulement, réécrire en gardant toutes les autres (jamais écraser
// AutoHDR). Le snapshot garde la chaîne d'origine brute (ou une suppression si la valeur était absente) ; le
// rendu la remet telle quelle si rien d'autre n'a bougé, sinon seules nos deux clés reprennent leur origine dans
// la chaîne courante (un Auto HDR réglé entre-temps reste), et seules elles se relisent.
//
// Effet au redémarrage ou au prochain lancement du jeu : le moteur saute l'A/B immédiat.
//
// Proposé, jamais posé en silence ; le SummaryKey décrit le changement, pas de « +FPS ». Le VRR ne fait
// rien sur un écran sans VRR (sans danger) ; la moitié flip-model aide les jeux fenêtrés dans tous les cas.
//
// Gating : AppliesTo lit lui-même cette chaîne (la lecture VRR du scan vient du pilote, une autre
// source). Flip-model absent sous Windows 11 : proposé sur tout écran. Sinon (flip-model déjà actif, ou Windows 10, où
// « Optimisations pour les jeux en fenêtré » n'existe pas d'après le support Microsoft) : proposé seulement pour la moitié
// VRR, et seulement sur un écran lu compatible VRR (`VrrCapableDisplay`, voir AppliesTo).
// -----------------------------------------------------------------------
/// <summary>Active le VRR fenêtré et le flip-model de Windows dans DirectXUserGlobalSettings, en gardant les autres
/// clés. Proposé quand le flip-model manque, ou quand seul le VRR manque sur un écran compatible.</summary>
public sealed class VrrFlipModelOnTweak : LatencyTweakBase
{
    private const string PrefsPath = DirectXUserGlobalSettings.Chemin;
    private const string ValueName = DirectXUserGlobalSettings.Nom;
    private static readonly (string Key, string Value)[] TurnOn =
    {
        ("VRROptimizeEnable", "1"),
        ("SwapEffectUpgradeEnable", "1"),
    };

    private readonly IRegistryReader _registry;
    private readonly Func<int> _buildWindows;

    /// <param name="buildWindows">Le build de Windows ; 22000 et plus : Windows 11.</param>
    public VrrFlipModelOnTweak(IRegistryReader registry, IElevatedClient elevated, Func<int>? buildWindows = null) : base(elevated)
        => (_registry, _buildWindows) = (registry, buildWindows ?? (() => Environment.OSVersion.Version.Build));

    /// <summary>Le flip-model reste à poser et compte ici : absent, sous Windows 11.</summary>
    private bool FlipModelAPoser(string? lu) => _buildWindows() >= 22000 && !DirectXUserGlobalSettings.AlreadyHas(lu, [TurnOn[1]]);

    public override string Id => "latency.vrrFlipModelOn";
    public override string SummaryKey => "tweak.latency.vrr_flipmodel.enable";

    public override bool TakesEffectAfterReboot => true;

    // Les deux interrupteurs deja a 1 ⇒ rien a faire, hors du plan (sinon
    // « rien a tenter » n'est jamais vrai). Illisible ⇒ non lu (EtatNonLu) : ni « deja regle », ni « a poser », puisque la
    // pose relit la chaine d'origine et echouerait. La capacite VRR de l'ecran reste a lire.
    //
    // La moitie VRR ne vaut rien sur un ecran qui ne fait pas de VRR. Le
    // poste de reference a deja le flip-model (`SwapEffectUpgradeEnable=1`) et pas `VRROptimizeEnable` :
    // le levier restait au plan pour la seule moitie VRR, que l'ecran soit capable ou non. Elle ne
    // compte plus que sur un ecran LU comme VRR (`VrrCapableDisplay`, deja lu par le scan).
    public override bool AppliesTo(HardwareInventory hw, SettingsState state)
    {
        try
        {
            var read = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
            if (!read.IsOk)
                return false;
            if (FlipModelAPoser(read.Value))
                return true; // flip-model absent : utile a tout jeu fenetre, quel que soit l'ecran
            return !DirectXUserGlobalSettings.AlreadyHas(read.Value, [TurnOn[0]])
                && state.VrrCapableDisplay == TriState.On;
        }
        catch (Exception) { return false; } // frontiere externe : non lu, dit par EtatNonLu
    }

    // Registre illisible, ou flip-model pose (ou sans objet sous Windows 10), moitie VRR absente et capacite VRR de l'ecran
    // jamais lue : ce n'est pas « deja regle ».
    public override bool EtatNonLu(SettingsState state)
    {
        try
        {
            var read = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
            return !read.IsOk
                || (!FlipModelAPoser(read.Value)
                    && !DirectXUserGlobalSettings.AlreadyHas(read.Value, [TurnOn[0]])
                    && state.VrrCapableDisplay == TriState.Unknown);
        }
        catch (Exception) { return true; }
    }

    // Banc profond : les deux interrupteurs déjà à 1, l'exercice les met à 0 (la forme que Windows écrit quand on les
    // éteint), voisins préservés par la fusion, puis rend la chaîne d'origine à l'identique.
    public override ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state)
    {
        try
        {
            if (_registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName) is not { IsOk: true, Value: { } origine }
                || !DirectXUserGlobalSettings.AlreadyHas(origine, TurnOn))
                return null;
            string Op(string v) => PrivilegedOpCodec.Serialize(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName,
                RegistryValueType.String, v)).Value!;
            return new ExerciceProfond(Op(origine), Op(DirectXUserGlobalSettings.Merge(origine, TurnOn.Select(t => (t.Key, "0")).ToList())));
        }
        catch (Exception) { return null; }
    }

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            // Relever la vraie chaîne d'origine avant d'écrire (retour fidèle de toute la valeur).
            var read = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
            if (!read.IsOk)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original DirectXUserGlobalSettings: {read.Error?.Code}.");

            var original = read.Value; // null = la valeur était absente

            // Inverse : remettre exactement la chaîne d'origine, ou supprimer la valeur si elle était absente.
            PrivilegedOp inverse = original is null
                ? new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
                : new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName,
                    RegistryValueType.String, original);
            var inverseWire = PrivilegedOpCodec.Serialize(inverse);
            if (!inverseWire.IsOk || inverseWire.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                    "Could not serialize the revert op.");

            // Fusionner nos deux clés dans la chaîne existante (AutoHDR et les autres gardés), puis la réécrire.
            var merged = DirectXUserGlobalSettings.Merge(original, TurnOn);
            var op = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName,
                RegistryValueType.String, merged);

            var applied = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
            if (!applied.IsOk)
                return Result<TweakSnapshot>.Fail(applied.Error!.Value.Code, applied.Error!.Value.Message);

            // La chaîne posée voyage avec l'inverse. Sans elle, la désinstallation ne
            // sait pas si la valeur est encore celle de DrDXT, et n'y touche pas.
            return Result<TweakSnapshot>.Ok(SnapshotAvec(Id, inverseWire.Value, op));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"VRR/flip-model apply boundary error: {ex.Message}");
        }
    }

    /// <summary>La chaîne porte aussi Auto HDR, que le joueur règle lui-même : remettre l'origine entière l'effacerait. La
    /// chaîne courante est relue, et seules les clés de DrDXT reprennent leur origine
    /// (<see cref="DirectXUserGlobalSettings.Rendre"/>). Courante illisible : rien n'est écrit par-dessus.</summary>
    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        PrivilegedOp? inverse = null;
        try { if (snapshot?.OriginalValueJson is { Length: > 0 } json && PrivilegedOpCodec.Deserialize(json) is { IsOk: true } d) inverse = d.Value; }
        catch (Exception) { /* la base le dit invalide */ }
        if (inverse is not (SetRegistryValueOp { Path: PrefsPath, Name: ValueName } or DeleteRegistryValueOp { Path: PrefsPath, Name: ValueName }))
            return await base.RevertAsync(snapshot!).ConfigureAwait(false);

        var courante = _registry.ReadString(RegistryHiveKind.CurrentUser, PrefsPath, ValueName);
        if (!courante.IsOk)
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, $"Could not read the current DirectXUserGlobalSettings: {courante.Error?.Code}.");
        var cible = DirectXUserGlobalSettings.Rendre(courante.Value, (inverse as SetRegistryValueOp)?.Value);
        PrivilegedOp rendu = cible is null
            ? new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName)
            : new SetRegistryValueOp(RegistryHiveKind.CurrentUser, PrefsPath, ValueName, RegistryValueType.String, cible);
        return await base.RevertAsync(snapshot! with { OriginalValueJson = PrivilegedOpCodec.Serialize(rendu).Value! }).ConfigureAwait(false);
    }
}
