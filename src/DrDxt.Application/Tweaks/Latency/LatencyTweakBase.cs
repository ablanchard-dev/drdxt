using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// Shared base for the M2 Phase D latency tweaks.
//
// Every latency tweak follows the same revert contract: the snapshot's
// OriginalValueJson is the serialized INVERSE typed PrivilegedOp carrying the
// Real captured original; reverting = deserialize it and send it back through
// IElevatedClient. A malformed / unknown-typed snapshot must never throw and
// Never run an arbitrary op → SAFETY.SNAPSHOT_INVALID (fail clean).
//
// This mirrors ISafetyManager.RevertAsync (Phase B) so the engine's generic
// revert path and the tweak's own RevertAsync behave identically.
// -----------------------------------------------------------------------
public abstract class LatencyTweakBase : ITweak
{
    /// <summary>Le profil DRS global du pilote : tous les jeux, et le bureau.</summary>
    protected const string ProfilDeBase = "Base Profile";

    protected LatencyTweakBase(IElevatedClient elevated) => Elevated = elevated;

    protected IElevatedClient Elevated { get; }

    public abstract string Id { get; }
    public TweakCategory Category => TweakCategory.Latency;
    public bool RequiresElevation => true;
    public bool RequiresRestorePoint => true;

    /// <summary>Most latency tweaks take effect immediately; a reboot-required one (HAGS)
    /// overrides this to true so the engine skips its immediate A/B (see ITweak).</summary>
    public virtual bool TakesEffectAfterReboot => false;

    /// <summary>The honest summary key surfaced to the UI (no "+FPS" claim).</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    /// <inheritdoc />
    /// <remarks>Virtuel ici, et pas seulement sur l'interface : une classe ne peut pas substituer
    /// un membre par defaut d'interface. Les leviers dont l'applicabilite se lit sur un
    /// <c>TriState</c> le redefinissent ; les autres repondent toujours, donc n'ont rien a dire.</remarks>
    public virtual bool EtatNonLu(SettingsState state) => false;

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

    /// <summary>Reverts by replaying the serialized inverse op from the snapshot.
    /// Defensive: malformed snapshot → SAFETY.SNAPSHOT_INVALID, never throws.</summary>
    public virtual async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.OriginalValueJson))
            return Result.Fail(ErrorCodes.SnapshotInvalid, "Snapshot has no original-value payload.");

        Result<PrivilegedOp> decoded;
        try
        {
            decoded = PrivilegedOpCodec.Deserialize(snapshot.OriginalValueJson);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.SnapshotInvalid, $"Snapshot decode error: {ex.Message}");
        }

        if (!decoded.IsOk || decoded.Value is null)
            return Result.Fail(ErrorCodes.SnapshotInvalid,
                "Snapshot original value is not a known typed privileged op.");

        try
        {
            return await Elevated.ExecuteAsync(decoded.Value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.HelperOpFailed, $"Revert boundary error: {ex.Message}");
        }
    }

    /// <summary>Builds the serialized inverse op for a single DWORD registry leaf:
    /// if the value existed, restore it; if it was absent, the faithful inverse is a
    /// delete. Returns the codec wire form to store in the snapshot.</summary>
    protected static Result<string> BuildRegistryInverse(
        RegistryHiveKind hive, string path, string name, uint? capturedOriginal)
    {
        PrivilegedOp inverse = capturedOriginal is uint original
            ? new SetRegistryValueOp(hive, path, name, RegistryValueType.DWord,
                original.ToString(System.Globalization.CultureInfo.InvariantCulture))
            : new DeleteRegistryValueOp(hive, path, name);
        return PrivilegedOpCodec.Serialize(inverse);
    }

    /// <summary>
    /// Le snapshot d'un levier de registre : l'op INVERSE (pour revenir) **et** l'op avant (pour
    /// prouver que la cible est en place).
    ///
    /// Sans la seconde, la relecture ne sait dire que « la valeur a change », jamais « la valeur
    /// demandee est la ». Un tiers qui pose une troisieme valeur donne le meme verdict, et le
    /// moteur mesure alors le gain d'un levier dont l'application n'est pas etablie.
    ///
    /// Une serialisation de la cible qui echoue ne fait pas echouer l'apply : le levier est
    /// deja ecrit a ce stade, et perdre la preuve est un cout, pas une raison de tout defaire. Le
    /// verificateur rendra <c>Unknown</c> — donc aucune mesure, ce qui est le comportement voulu.
    /// </summary>
    protected static TweakSnapshot SnapshotAvec(string id, string inverseJson, PrivilegedOp demande)
    {
        var cible = PrivilegedOpCodec.Serialize(demande);
        return new TweakSnapshot(id, inverseJson, cible.IsOk ? cible.Value : null);
    }

    /// <summary>
    /// PHASE 18 F1 lot 3 (PreferMaxPerf), etendu le 14/09/2026 aux deux autres leviers DRS (plan
    /// complet 1.3 : ils ecrivaient dans « Base Profile », donc dans tous les jeux et le bureau,
    /// pour une mesure faite sur un seul) — une ecriture DRS et son inverse, au scope que le
    /// pilote applique au jeu.
    ///
    /// Jeu connu avec un profil lisible : on ecrit dans ce profil (rayon d'effet = un executable),
    /// et l'inverse depend de la PROVENANCE lue avant l'ecriture :
    ///   * pose sur ce profil (`SetByUser`) ⇒ reecrire la valeur d'avant ;
    ///   * herite, defaut pilote, ou livre par NVIDIA ⇒ SUPPRIMER le reglage du profil (reecrire
    ///     laisserait au jeu un reglage « pose par toi » qu'il n'avait pas) ;
    ///   * provenance inconnue ⇒ refus : ni ecriture au jeu, ni repli sur le global.
    /// Jeu inconnu ou sans profil lisible : le global, lu par <paramref name="lireGlobal"/>.
    /// Simplification voulue : repli global hors jeu (outils, bancs, gate de revert) ; le fermer le jour ou plus
    /// rien n'applique un levier DRS sans jeu en cours.
    /// </summary>
    protected static Result<(PrivilegedOp Inverse, ApplyNvApiDrsSettingOp Op)> PlanifierDrs(
        INvApi nvApi, bool jeuCherche, string? jeu, uint settingId, uint cible, Func<Result<uint>> lireGlobal, string quoi)
    {
        // 24/09/2026 : le produit cherche toujours le jeu. Pas trouvé (PC au repos, anti-triche qui
        // refuse l'ouverture du processus) ⇒ rien d'écrit, jamais le global à sa place.
        if (jeuCherche && jeu is null)
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"No running game was found: the {quoi} is not written, "
                + "and nothing falls back to the global profile.");

        // Une portee pour l'ecriture et son inverse : le gate `DrsRevertMatchesItsScopeGateTests`
        // l'exige, et c'est la propriete qui compte — restaurer le profil qu'on a modifie.
        var portee = ProfilDeBase;
        uint avant;
        DrsValueOrigin? origineAuJeu = null;

        var auJeu = jeu is null ? null : nvApi.GetDrsForApplication(settingId, jeu);
        if (auJeu is { IsOk: true })
        {
            portee = jeu!;
            avant = auJeu.Value.Value;
            origineAuJeu = auJeu.Value.Origin.Origin;
        }
        // 14/09/2026 (relecture 12/20, lot B) : un profil de jeu qui existe mais ne se lit pas
        // n'est pas « pas de profil ». Se rabattre sur le global ecrivait la ou le pilote n'applique
        // rien au jeu, et la relecture globale prouvait ensuite `Applied`. Seul « pas de profil » replie.
        else if (auJeu is { } echec && echec.Error?.Code != ErrorCodes.DrsNoGameProfile)
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"The game's driver profile exists but its {quoi} could not be read: nothing is written, "
                + "and nothing falls back to the global profile.");
        // 24/09/2026 : un jeu CONNU sans profil pilote ne replie plus sur le global. Sur la machine
        // de référence, le global portait « performances maximales » : carte au maximum hors jeu
        // toute la journée, pour un gain mesuré sur un seul jeu. Seul « aucun jeu » (outils, bancs)
        // garde le repli.
        else if (jeu is not null)
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"The game has no driver profile: the {quoi} is not written, "
                + "and nothing falls back to the global profile.");
        else
        {
            var original = lireGlobal();
            if (!original.IsOk)
                return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                    $"Could not read original {quoi}: {original.Error?.Code}.");
            avant = original.Value;
        }

        PrivilegedOp? inverse = origineAuJeu switch
        {
            // Global, ou reglage pose sur le profil du jeu : reecrire la valeur d'avant.
            null or DrsValueOrigin.SetByUser =>
                new ApplyNvApiDrsSettingOp(settingId, avant, portee),
            // Un reglage que le profil du jeu ne possedait pas : le lui retirer.
            DrsValueOrigin.InheritedFromGlobalProfile or DrsValueOrigin.InheritedFromBaseProfile
                or DrsValueOrigin.DriverDefault or DrsValueOrigin.ShippedWithTheDriver =>
                new DeleteNvApiDrsSettingOp(settingId, portee, origineAuJeu.Value, avant),
            _ => null,
        };

        return inverse is null
            ? Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Where the game's current {quoi} comes from is not established: "
                + "nothing is written at game scope, and nothing falls back to the global profile.")
            : Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Ok(
                (inverse, new ApplyNvApiDrsSettingOp(settingId, cible, portee)));
    }
}
