using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>Un levier pose pendant une session, et ce qu'il faut pour le rendre.
/// <paramref name="Snapshot"/> <c>null</c> = l'ecriture a commence mais son etat d'origine n'a pas
/// encore ete garde (fenetre de quelques millisecondes) : on ne sait pas revenir, on le DIT.</summary>
public sealed record RevertEnAttenteEntree(string TweakId, TweakSnapshot? Snapshot);

/// <summary>
/// PHASE 18 F6 (L8 a) — le filet d'une session tuee.
///
/// Une session tient un levier pose quatre a cinq minutes. Sans ce journal, une App tuee ou un
/// crash pendant ce temps laissait le levier en place pour toujours, et l'etat d'origine du joueur
/// n'existait plus que dans la memoire d'un processus mort. Un seul levier EN ESSAI a la fois.
///
/// 11/09/2026 — et les leviers gardes. Apres un keep, rien ne levait l'essai : le demarrage suivant
/// rendait le levier garde. L'effacer n'aurait pas suffi : ce fichier est le seul endroit ou vit la
/// valeur d'origine d'un levier garde en session (le manifeste signe ne recoit que les corrections au
/// redemarrage). Un keep DEPLACE donc l'entree de « en essai » vers « gardes », en une seule ecriture
/// atomique, et plus aucun essai ne l'efface.
///
/// 11/09/2026 (nuit, 5e lot) — qui fait foi, tranche. Au keep, `TweakCandidate` inscrit l'origine au
/// manifeste signe (`DesiredState.RevertOnly`) par le helper eleve. Pour la DESINSTALLATION, c'est le seul
/// qui fait foi : il est signe par la cle de la machine et vit dans la racine admin, alors que ce fichier-ci
/// vit sous %LOCALAPPDATA%, ecrivable par tout processus du compte — le rejouer eleve ferait d'un fichier
/// utilisateur une autorite privilegiee. `Gardes` n'est donc jamais rejoue (verifie : aucun lecteur hors de
/// cette classe ; `Lire()` ne rend que l'essai). Il ne double pas `RevertOnly` pour rien : il reste la trace
/// LOCALE du keep, et la seule copie de l'origine quand le helper a refuse de l'inscrire (manifeste illisible
/// avant reprise, helper injoignable, outil de campagne sans manifeste) — ce que `OrigineHorsManifeste` dit.
/// Meme regle des deux cotes : la premiere origine gagne.
/// </summary>
public interface IRevertEnAttente
{
    Result Poser(string tweakId, TweakSnapshot? snapshot);
    Result Lever();

    /// <summary>Le levier en essai est garde : il quitte l'essai (le demarrage ne le rendra plus) et
    /// rejoint les leviers gardes, avec la valeur d'origine du premier keep quand il y est deja — un
    /// levier garde puis retente recapture la cible comme « origine », et ecraser la vraie serait la
    /// perdre.</summary>
    Result Garder(string tweakId, TweakSnapshot snapshot);

    /// <summary>Le levier EN ESSAI, ou <c>null</c>. Les leviers gardes n'y figurent jamais.</summary>
    Result<RevertEnAttenteEntree?> Lire();
}

/// <summary>Le journal sur disque (ecriture atomique : jamais un fichier lu a moitie).</summary>
public sealed class FichierRevertEnAttente : IRevertEnAttente
{
    public const string CodeErreur = "REVERT_EN_ATTENTE";

    /// <summary>Ce qui est ecrit. `TweakId`/`Snapshot` en tete : un fichier ecrit avant les leviers
    /// gardes (un essai seul) se relit tel quel, `Gardes` absent.</summary>
    private sealed record Contenu(string? TweakId, TweakSnapshot? Snapshot,
        IReadOnlyList<RevertEnAttenteEntree>? Gardes = null);

    private readonly string _chemin;
    private readonly FichierJsonAtomique _fichier;

    public FichierRevertEnAttente(string chemin)
    {
        _chemin = chemin;
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
    }

    public static FichierRevertEnAttente ParDefaut() =>
        new(FichierJsonAtomique.CheminLocal("pending-revert.json"));

    public Result Poser(string tweakId, TweakSnapshot? snapshot)
    {
        // 08/10/2026 (revue n° 10) : un seul essai tient dans ce fichier. Poser un AUTRE levier par-dessus un essai
        // pas encore rendu effaçait son origine pour toujours (essai démarré après une remise au démarrage refusée).
        // Revue n° 11 : le MÊME levier aussi. Retenté après une remise ratée, il recapturait comme « origine » la valeur
        // que DrDXT avait posée. Seul compléter son propre essai (pose sans origine puis avec) est permis.
        if (LireContenu() is { IsOk: true, Value: { TweakId: { } enCours } contenu }
            && (!string.Equals(enCours, tweakId, StringComparison.Ordinal) || (contenu.Snapshot is not null && snapshot is null)))
            return Result.Fail(CodeErreur, $"« {enCours} » est encore en essai et pas rendu : il doit l'être avant tout autre essai.");
        return Ecrire(new Contenu(tweakId, snapshot, GardesActuels()));
    }

    public Result Lever()
    {
        var gardes = GardesActuels();
        if (gardes.Count > 0) return Ecrire(new Contenu(null, null, gardes));
        try
        {
            File.Delete(_chemin);   // absent = deja leve, pas une erreur
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(CodeErreur, $"Suppression impossible : {ex.Message}");
        }
    }

    public Result Garder(string tweakId, TweakSnapshot snapshot)
    {
        // Relu avant d'ecrire : un fichier illisible n'est pas une liste vide a ecraser.
        var lu = LireContenu();
        if (!lu.IsOk) return Result.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
        var gardes = (lu.Value?.Gardes ?? Array.Empty<RevertEnAttenteEntree>()).ToList();
        if (!gardes.Any(g => string.Equals(g.TweakId, tweakId, StringComparison.Ordinal)))
            gardes.Add(new RevertEnAttenteEntree(tweakId, snapshot));
        return Ecrire(new Contenu(null, null, gardes));
    }

    public Result<RevertEnAttenteEntree?> Lire()
    {
        var lu = LireContenu();
        if (!lu.IsOk) return Result<RevertEnAttenteEntree?>.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
        return Result<RevertEnAttenteEntree?>.Ok(lu.Value?.TweakId is { } id
            ? new RevertEnAttenteEntree(id, lu.Value.Snapshot)
            : null);
    }

    private Result Ecrire(Contenu c) => _fichier.WriteAtomic(JsonSerializer.Serialize(c));

    // Simplification voulue : un fichier illisible au moment de POSER un essai est ecrase, comme avant ; le demarrage
    // l'a deja signale. Seul Garder refuse de l'ecraser, parce qu'il y ajouterait une trace.
    private IReadOnlyList<RevertEnAttenteEntree> GardesActuels()
        => LireContenu() is { IsOk: true, Value.Gardes: { } g } ? g : Array.Empty<RevertEnAttenteEntree>();

    private Result<Contenu?> LireContenu()
    {
        var lu = _fichier.Read();
        if (!lu.IsOk) return Result<Contenu?>.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
        if (lu.Value is null) return Result<Contenu?>.Ok(null);
        try
        {
            return Result<Contenu?>.Ok(JsonSerializer.Deserialize<Contenu>(lu.Value));
        }
        catch (JsonException ex)
        {
            // Illisible n'est pas « rien en attente » : l'appelant doit le dire.
            return Result<Contenu?>.Fail(CodeErreur, $"Journal illisible : {ex.Message}");
        }
    }
}

/// <summary>Au demarrage : rendre ce qu'une session interrompue a laisse pose.</summary>
public static class RevertAuDemarrage
{
    /// <summary>Rend la phrase a montrer au joueur, ou <c>null</c> quand rien n'etait en attente.
    /// Le journal n'est leve que si le levier est rendu (ou s'il n'y a rien a rendre) : un revert
    /// rate se retentera au prochain demarrage. Un levier garde n'est jamais en essai : il n'est
    /// ni rendu ni efface ici.</summary>
    public static string? Executer(IRevertEnAttente journal, ITweakCatalog catalogue)
    {
        var lu = journal.Lire();
        if (!lu.IsOk)
            return $"Le journal de la dernière session n'a pas pu être lu ({lu.Error?.Message}) : "
                   + "un réglage d'essai est peut-être encore en place.";
        if (lu.Value is not { } entree) return null;

        if (entree.Snapshot is not { } snapshot)
        {
            journal.Lever();
            return $"Un essai de « {entree.TweakId} » a été interrompu avant que son état d'origine soit "
                   + "gardé : DrDXT ne peut pas le remettre seul. Vérifie ce réglage.";
        }

        var levier = catalogue.Get(entree.TweakId);
        if (!levier.IsOk || levier.Value is null)
            return $"« {entree.TweakId} » est resté en place après une session interrompue, et ce levier "
                   + "n'existe plus dans cette version : il n'a pas pu être remis.";

        // 25/09/2026 (revue de sécurité) : ce fichier vit sous %LOCALAPPDATA%, écrivable par tout
        // processus du compte, et le levier rejoue son contenu par LE HELPER élevé. Sans ce contrôle,
        // un fichier piégé faisait exécuter n'importe quel op de l'allowlist (redémarrage dans le BIOS,
        // BCD, écriture du manifeste…) sous une autorisation Windows donnée « à DrDXT ». Seuls les
        // types qu'un levier utilise réellement comme inverse passent ; tout le reste, et tout type
        // futur, est refusé. Le journal reste en place : le joueur est prévenu, rien n'est rejoué.
        if (!InverseRejouable(entree.TweakId, snapshot.OriginalValueJson))
            return $"Un essai de « {entree.TweakId} » est resté en attente, mais ce que le journal demande de "
                   + "rejouer n'est pas une remise en état de ce levier : DrDXT ne l'exécute pas. Vérifie ce "
                   + "réglage, et le fichier pending-revert.json de ton profil.";

        Result rendu;
        try { rendu = levier.Value.RevertAsync(snapshot).GetAwaiter().GetResult(); }
        catch (Exception ex) { rendu = Result.Fail(CodeRevert, ex.Message); }

        if (!rendu.IsOk)
            return $"« {entree.TweakId} » est resté en place après une session interrompue, et le remettre a "
                   + $"échoué ({rendu.Error?.Message}). Nouvel essai au prochain démarrage.";

        journal.Lever();
        return $"La dernière session a été interrompue : « {entree.TweakId} » a été remis comme avant.";
    }

    private const string CodeRevert = "REVERT_AU_DEMARRAGE";

    /// <summary>L'inverse lu dans le fichier (un op, ou une liste composite) ne contient que des types que
    /// Ce levier écrit pour se remettre. Levier inconnu ou contenu illisible ⇒ refusé.</summary>
    public static bool InverseRejouable(string tweakId, string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || !InverseParLevier.TryGetValue(tweakId, out var admis)) return false;
        IReadOnlyList<PrivilegedOp>? ops = null;
        try
        {
            var seul = PrivilegedOpCodec.Deserialize(json);
            if (seul.IsOk && seul.Value is { } op) ops = [op];
            else if (CompositeSnapshotCodec.Decode(json) is { IsOk: true, Value: { } liste }) ops = liste;
        }
        catch (Exception) { return false; }
        return ops is { Count: > 0 } && ops.All(op => admis.Contains(op.GetType()) && FeuilleDuLevier(tweakId, op));
    }

    // 26/09/2026 : le type seul laissait un levier de registre rejouer n'importe quelle feuille que le helper
    // permet (HAGS portant « PagingFiles vide » désactivait le fichier d'échange). Un levier ne rejoue que les
    // feuilles que son code écrit. `CppcEnable` reste à x3d_ccd : il l'écrivait jusqu'au 24/09, un ancien
    // fichier peut encore porter son inverse.
    private static bool FeuilleDuLevier(string tweakId, PrivilegedOp op)
    {
        // Même trou côté NVIDIA : le helper borne l'identifiant DRS, jamais la valeur. Un levier DRS ne
        // rejoue que ses propres réglages (un limiteur d'images rangé sous « préférer les performances »
        // bridait toute la machine).
        uint? drs = op switch
        {
            ApplyNvApiDrsSettingOp a => a.SettingId,
            DeleteNvApiDrsSettingOp d => d.SettingId,
            _ => null,
        };
        if (drs is { } id)
            return DrsParLevier.TryGetValue(tweakId, out var ids) && ids.Contains(id);

        // Côté AMD, le genre de l'op ADLX : la file d'images n'écrit que l'Anti-Lag, et rejouait pourtant
        // une limite de puissance ou une sous-tension.
        if (op is ApplyAdlxTuningOp adlx)
            return GenreAdlxParLevier.TryGetValue(tweakId, out var genres) && genres.Contains(adlx.Kind);

        var (ruche, chemin, nom) = op switch
        {
            SetRegistryValueOp s => (s.Hive, s.Path, s.Name),
            DeleteRegistryValueOp d => (d.Hive, d.Path, d.Name),
            _ => (default(RegistryHiveKind), (string?)null, (string?)null),
        };
        // 26/09/2026 : l'ecriture de registre se reconnait a son TYPE. Reconnue au chemin non nul, un op de
        // registre sans chemin passait pour « pas du registre » et atteignait le levier.
        if (op is not (SetRegistryValueOp or DeleteRegistryValueOp)) return true; // le type suffit
        if (string.IsNullOrWhiteSpace(chemin) || string.IsNullOrWhiteSpace(nom)) return false;
        return FeuillesParLevier.TryGetValue(tweakId, out var feuilles)
            && feuilles.Any(f => f.Ruche == ruche
                && string.Equals(f.Chemin, chemin.Trim().Trim('\\'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Nom, nom, StringComparison.Ordinal));
    }

    // Identifiants relevés dans chaque levier (NvApiDriverSettings.h) : PREFERRED_PSTATE, VSYNCMODE et
    // FRL_FPS, PRERENDERLIMIT.
    private static readonly IReadOnlyDictionary<string, uint[]> DrsParLevier = new Dictionary<string, uint[]>(StringComparer.Ordinal)
    {
        ["latency.prefer_max_perf"] = [0x1057EB71],
        ["latency.vrr_vsync_cap"] = [0x00A879CF, 0x10835002],
        ["latency.render_queue_cap"] = [0x007BA09E],
    };

    private static readonly IReadOnlyDictionary<string, AdlxTuningKind[]> GenreAdlxParLevier =
        new Dictionary<string, AdlxTuningKind[]>(StringComparer.Ordinal)
        {
            ["latency.render_queue_cap"] = [AdlxTuningKind.AntiLag],
            ["latency.vrr_vsync_cap"] = [AdlxTuningKind.FrameRateTarget, AdlxTuningKind.VerticalRefresh],
            ["gpu.undervolt"] = [AdlxTuningKind.Undervolt],
            ["gpu.power_limit"] = [AdlxTuningKind.PowerLimit],
        };

    private const string GameBar = @"Software\Microsoft\GameBar";

    private static readonly IReadOnlyDictionary<string, (RegistryHiveKind Ruche, string Chemin, string Nom)[]> FeuillesParLevier =
        new Dictionary<string, (RegistryHiveKind, string, string)[]>(StringComparer.Ordinal)
        {
            ["latency.hagsOn"] = [(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")],
            ["latency.gameModeOn"] = [(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode"),
                                      (RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled")],
            ["latency.gameDvrBackgroundOff"] = [(RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "HistoricalCaptureEnabled")],
            ["latency.vrrFlipModelOn"] = [(RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings")],
            ["latency.mouse_acceleration_off"] = [(RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseSpeed"),
                                                (RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1"),
                                                (RegistryHiveKind.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2")],
            ["latency.reflex"] = [(RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")],
            ["fixit.sane_pagefile"] = [(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles")],
            ["fixit.heures_actives"] = [(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut),
                                        (RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin)],
            ["fixit.x3d_ccd"] = [(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode"),
                                 (RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable")],
        };

    public static IEnumerable<string> LeviersRejouables => InverseParLevier.Keys;

    // 26/09/2026 (revue de sécurité) : le type seul ne suffisait pas. Un fichier nommant HAGS avec une limite de
    // puissance GPU faisait écrire le GPU au lancement. Chaque levier ne rejoue que ce que son ApplyAsync écrit
    // comme inverse, relevé dans son code. Registre : BuildRegistryInverse rend Set ou Delete. DRS :
    // PlanifierDrs rend Apply ou Delete. GPU : NVAPI, ou ADLX sur une carte AMD.
    private static readonly Type[] Registre = [typeof(SetRegistryValueOp), typeof(DeleteRegistryValueOp)];
    private static readonly Type[] Drs = [typeof(ApplyNvApiDrsSettingOp), typeof(DeleteNvApiDrsSettingOp)];

    private static readonly IReadOnlyDictionary<string, HashSet<Type>> InverseParLevier =
        new Dictionary<string, HashSet<Type>>(StringComparer.Ordinal)
        {
            ["latency.hagsOn"] = [.. Registre],
            ["latency.gameModeOn"] = [.. Registre],
            ["latency.gameDvrBackgroundOff"] = [.. Registre],
            ["latency.vrrFlipModelOn"] = [.. Registre],
            ["latency.mouse_acceleration_off"] = [.. Registre],
            ["latency.reflex"] = [.. Registre],
            ["latency.prefer_max_perf"] = [.. Drs],
            ["latency.vrr_vsync_cap"] = [.. Drs, typeof(ApplyAdlxTuningOp)],
            ["latency.render_queue_cap"] = [.. Drs, typeof(ApplyAdlxTuningOp)],
            ["fixit.sane_pagefile"] = [.. Registre],
            ["fixit.heures_actives"] = [.. Registre],
            ["fixit.power_plan"] = [typeof(SetActivePowerPlanOp)],
            ["fixit.mode_boost"] = [typeof(SetProcessorBoostModeOp)],
            ["fixit.cpu_power_limit"] = [typeof(SetCpuPackagePowerLimitOp)],
            ["fixit.wifi_performances"] = [typeof(SetWirelessPowerSavingOp)],
            ["fixit.x3d_ccd"] = [.. Registre, typeof(RunServiceControlOp)],
            ["gpu.undervolt"] = [typeof(ApplyNvApiClockOffsetOp), typeof(ApplyAdlxTuningOp)],
            ["gpu.power_limit"] = [typeof(ApplyNvApiPowerLimitOp), typeof(ApplyAdlxTuningOp)],
            // 08/10/2026 (GPU-OC) : une vitesse fixe du mode Expert ne se rend qu'en rendant l'automatique.
            ["gpu.fan_curve"] = [typeof(RestoreGpuFanAutoOp)],
        };
}
