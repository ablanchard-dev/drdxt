using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

/// <summary>
/// Coupe l'accélération du pointeur de Windows, proposé quand elle est lue active.
///
/// « Améliorer la précision du pointeur » de Windows est une accélération : le curseur parcourt plus de pixels quand la souris va vite. En jeu, la
/// visée n'a plus la même échelle d'un geste à l'autre. Coupée, le déplacement redevient proportionnel au geste. Ce n'est
/// pas un gain de latence ni d'images : de la justesse, et le texte le dit.
///
/// Trois valeurs REG_SZ de la session (HKCU\Control Panel\Mouse) : MouseSpeed (1 = accélération, 0 = aucune),
/// MouseThreshold1 et MouseThreshold2 (seuils de vitesse ; 6 et 10 par défaut, 0 sans accélération). Les trois sont
/// lues avant d'écrire et rendues à l'identique (absentes ⇒ supprimées). Windows relit ces valeurs à l'ouverture de
/// session : l'effet attend le prochain démarrage de session, et le moteur ne mesure rien dans celle-ci.
/// Source : Microsoft, paramètres de la souris (« Améliorer la précision du pointeur ») ; valeurs relevées sur la
/// machine de référence et confirmées par la documentation communautaire (elevenforum.com, 7327).
/// </summary>
public sealed class AccelerationSourisOffTweak : LatencyTweakBase
{
    public const string Chemin = @"Control Panel\Mouse";
    public static readonly string[] Noms = ["MouseSpeed", "MouseThreshold1", "MouseThreshold2"];
    private const string SansAcceleration = "0";

    private readonly IRegistryReader _registry;

    public AccelerationSourisOffTweak(IRegistryReader registry, IElevatedClient elevated) : base(elevated)
        => _registry = registry;

    public override string Id => "latency.mouse_acceleration_off";
    public override string SummaryKey => "tweak.latency.mouse_acceleration.off";

    // Windows relit ces valeurs à l'ouverture de session.
    public override bool TakesEffectAfterReboot => true;

    /// <summary>Seulement quand l'accélération est lue comme active. Non lue (null, Unknown) : rien n'est proposé.</summary>
    public override bool AppliesTo(HardwareInventory hw, SettingsState state) =>
        state.AccelerationSouris == TriState.On;

    public override bool EtatNonLu(SettingsState state) =>
        state.AccelerationSouris is null or TriState.Unknown;

    public override async Task<Result<TweakSnapshot>> ApplyAsync()
    {
        try
        {
            var inverses = new List<PrivilegedOp>(Noms.Length);
            var avants = new List<PrivilegedOp>(Noms.Length);
            foreach (var nom in Noms)
            {
                var lu = _registry.ReadString(RegistryHiveKind.CurrentUser, Chemin, nom);
                if (!lu.IsOk)
                    return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                        $"Could not read original {nom}: {lu.Error?.Code}.");
                inverses.Add(lu.Value is { } origine
                    ? new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom, RegistryValueType.String, origine)
                    : new DeleteRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom));
                avants.Add(new SetRegistryValueOp(RegistryHiveKind.CurrentUser, Chemin, nom, RegistryValueType.String, SansAcceleration));
            }

            var snapshotJson = CompositeSnapshotCodec.Encode(inverses);
            if (!snapshotJson.IsOk || snapshotJson.Value is null)
                return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable, "Could not serialize the revert ops.");

            var appliedInverses = new List<PrivilegedOp>(Noms.Length);
            for (var i = 0; i < avants.Count; i++)
            {
                var res = await Elevated.ExecuteAsync(avants[i], CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk)
                    return await RetourArriere.EchecAsync(Elevated, appliedInverses, res.Error!.Value.Code, res.Error!.Value.Message)
                        .ConfigureAwait(false);
                appliedInverses.Add(inverses[i]);
            }

            var posees = CompositeSnapshotCodec.Encode(avants);
            return Result<TweakSnapshot>.Ok(new TweakSnapshot(Id, snapshotJson.Value, posees.IsOk ? posees.Value : null));
        }
        catch (Exception ex)
        {
            return Result<TweakSnapshot>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Mouse acceleration off apply boundary error: {ex.Message}");
        }
    }

    public override async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<IReadOnlyList<PrivilegedOp>> decoded;
        try { decoded = CompositeSnapshotCodec.Decode(snapshot.OriginalValueJson); }
        catch (Exception ex) { return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}"); }
        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot original value is not a known composite of typed ops.");

        try
        {
            foreach (var op in decoded.Value)
            {
                var res = await Elevated.ExecuteAsync(op, CancellationToken.None).ConfigureAwait(false);
                if (!res.IsOk) return res;
            }
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.TweakSdkUnavailable, $"Mouse acceleration revert boundary error: {ex.Message}");
        }
    }
}
