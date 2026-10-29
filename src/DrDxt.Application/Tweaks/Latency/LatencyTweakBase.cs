using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// Base commune des leviers de latence. Annuler = désérialiser l'OriginalValueJson du snapshot (la PrivilegedOp inverse,
// avec l'original relu) et le renvoyer par IElevatedClient, comme ISafetyManager.RevertAsync. Snapshot mal formé ou de
// type inconnu : SAFETY.SNAPSHOT_INVALID, jamais d'exception ni d'op arbitraire.
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

    /// <summary>La plupart des leviers de latence agissent tout de suite ; celui qui exige un
    /// redémarrage (HAGS) passe ceci à vrai pour que le moteur saute son A/B immédiat (voir ITweak).</summary>
    public virtual bool TakesEffectAfterReboot => false;

    /// <summary>La clé de résumé honnête affichée par l'UI (aucune promesse de « +FPS »).</summary>
    public abstract string SummaryKey { get; }

    public abstract bool AppliesTo(HardwareInventory hw, SettingsState state);

    /// <inheritdoc />
    /// <remarks>Virtuel ici : une classe ne peut pas substituer un membre par defaut d'interface. Les leviers lus sur un
    /// <c>TriState</c> le redefinissent.</remarks>
    public virtual bool EtatNonLu(SettingsState state) => false;

    /// <summary>Virtuel ici pour la même raison qu'<see cref="EtatNonLu"/> : null = pas d'exercice sûr.</summary>
    public virtual ExerciceProfond? PreparerExercice(HardwareInventory hw, SettingsState state) => null;

    public abstract Task<Result<TweakSnapshot>> ApplyAsync();

    /// <summary>Annule en rejouant l'op inverse sérialisée du snapshot.
    /// Prudent : snapshot malformé → SAFETY.SNAPSHOT_INVALID, ne lève jamais.</summary>
    public virtual async Task<Result> RevertAsync(TweakSnapshot snapshot)
    {
        // Une annulation part hors portée de pose : marquée, le helper la rejouerait après un arrêt sec et remettrait le levier.
        using var horsPortee = PorteeDePose.Suspendre();
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

    /// <summary>Construit l'op inverse sérialisée d'une feuille de registre DWORD : si la valeur
    /// existait, la remettre ; si elle était absente, l'inverse fidèle est une suppression.
    /// Rend la forme filaire du codec à ranger dans le snapshot.</summary>
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
    /// Le snapshot d'un levier de registre : l'op inverse (pour revenir) et l'op avant (pour prouver que la cible est en
    /// place, pas seulement que « la valeur a change »). Une cible non serialisable ne fait pas echouer l'apply deja
    /// ecrit : le verificateur rendra <c>Unknown</c>, donc aucune mesure.
    /// </summary>
    protected static TweakSnapshot SnapshotAvec(string id, string inverseJson, PrivilegedOp demande)
    {
        var cible = PrivilegedOpCodec.Serialize(demande);
        return new TweakSnapshot(id, inverseJson, cible.IsOk ? cible.Value : null);
    }

    /// <summary>
    /// Une ecriture DRS et son inverse, au profil que le pilote applique au jeu : « Base Profile » toucherait tous les jeux
    /// et le bureau pour une mesure faite sur un seul. Jeu connu au profil lisible : ce profil, et l'inverse selon la
    /// provenance lue avant l'ecriture :
    ///   * pose sur ce profil (`SetByUser`) ⇒ reecrire la valeur d'avant ;
    ///   * herite, defaut pilote, ou livre par NVIDIA ⇒ supprimer le reglage du profil (reecrire
    ///     laisserait au jeu un reglage « pose par toi » qu'il n'avait pas) ;
    ///   * provenance inconnue ⇒ refus : ni ecriture au jeu, ni repli sur le global.
    /// Jeu inconnu ou sans profil lisible : le global, lu par <paramref name="lireGlobal"/>.
    /// Simplification voulue : repli global hors jeu (outils, bancs, gate de revert) ; le fermer le jour ou plus
    /// rien n'applique un levier DRS sans jeu en cours.
    /// </summary>
    protected static Result<(PrivilegedOp Inverse, ApplyNvApiDrsSettingOp Op)> PlanifierDrs(
        INvApi nvApi, bool jeuCherche, string? jeu, uint settingId, uint cible, Func<Result<uint>> lireGlobal, string quoi)
    {
        // Jeu cherché mais pas trouvé (PC au repos, anti-triche qui refuse l'ouverture) ⇒ rien d'écrit, jamais le global.
        if (jeuCherche && jeu is null)
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"No running game was found: the {quoi} is not written, "
                + "and nothing falls back to the global profile.");

        // Une portee pour l'ecriture et son inverse : restaurer le profil modifie (`DrsRevertMatchesItsScopeGateTests`).
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
        // Un profil de jeu illisible n'est pas « pas de profil » : le global ecrirait la ou le pilote n'applique rien au
        // jeu, et la relecture globale prouverait `Applied`.
        else if (auJeu is { } echec && echec.Error?.Code != ErrorCodes.DrsNoGameProfile)
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"The game's driver profile exists but its {quoi} could not be read: nothing is written, "
                + "and nothing falls back to the global profile.");
        // Un jeu connu sans profil pilote ne replie pas sur le global (« performances maximales » au global sur la machine
        // de référence : carte au maximum hors jeu toute la journée). Seul « aucun jeu » (outils, bancs) replie.
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

        // Un inverse hors de l'énumération documentée serait refusé par le helper : rien n'est écrit, sinon le réglage
        // resterait posé pour toujours.
        if (inverse is ApplyNvApiDrsSettingOp rejeu && !ValeursDrs.Admise(settingId, rejeu.Value, portee == ProfilDeBase))
            return Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"The current {quoi} (0x{avant:X}) is outside NVIDIA's documented values and could not be restored: nothing is written.");

        return inverse is null
            ? Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Fail(ErrorCodes.TweakSdkUnavailable,
                $"Where the game's current {quoi} comes from is not established: "
                + "nothing is written at game scope, and nothing falls back to the global profile.")
            : Result<(PrivilegedOp, ApplyNvApiDrsSettingOp)>.Ok(
                (inverse, new ApplyNvApiDrsSettingOp(settingId, cible, portee)));
    }
}
