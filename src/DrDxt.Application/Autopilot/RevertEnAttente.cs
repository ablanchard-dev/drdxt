using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Autopilot;

/// <summary>Un levier pose pendant une session, et ce qu'il faut pour le rendre.
/// <paramref name="Snapshot"/> <c>null</c> = l'ecriture a commence mais son etat d'origine n'a pas
/// encore ete garde (fenetre de quelques millisecondes) : on ne sait pas revenir, on le dit.</summary>
public sealed record RevertEnAttenteEntree(string TweakId, TweakSnapshot? Snapshot);

/// <summary>
/// Le filet d'une session tuee.
///
/// Un levier reste pose quatre a cinq minutes pendant un essai (un seul a la fois) : sans ce journal, une App tuee le
/// laisserait en place pour toujours. Un keep deplace l'entree de « en essai » vers « gardes » en une seule ecriture
/// atomique, pour que le demarrage suivant ne rende pas un levier garde.
///
/// Pour la desinstallation, seul fait foi le manifeste signe (`DesiredState.RevertOnly`, inscrit au keep par le helper) :
/// ce fichier vit sous %LOCALAPPDATA%, ecrivable par tout le compte, et le rejouer eleve en ferait une autorite
/// privilegiee. `Gardes` n'est donc jamais rejoue (`Lire()` ne rend que l'essai) ; il reste la trace locale du keep et la
/// seule copie de l'origine quand le helper a refuse de l'inscrire (`OrigineHorsManifeste`). Des deux cotes, la premiere
/// origine gagne.
/// </summary>
public interface IRevertEnAttente
{
    Result Poser(string tweakId, TweakSnapshot? snapshot);
    Result Lever();

    /// <summary>Lève un essai que rien ne rendra jamais, en gardant sa trace à côté : resté au journal, il refuserait tous
    /// les essais suivants.</summary>
    Result Ecarter() => Lever();

    /// <summary>Le levier en essai est garde : il quitte l'essai (le demarrage ne le rendra plus) et
    /// rejoint les leviers gardes, avec la valeur d'origine du premier keep quand il y est deja : un
    /// levier garde puis retente recapture la cible comme « origine », et ecraser la vraie serait la
    /// perdre.</summary>
    Result Garder(string tweakId, TweakSnapshot snapshot);

    /// <summary>Le levier en essai, ou <c>null</c>. Les leviers gardes n'y figurent jamais.</summary>
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

    private readonly TimeSpan _attente;

    private readonly Tweaks.Hardware.IRegistryReader? _registre;
    private readonly Func<string, string>? _nom;

    /// <param name="attente">Combien attendre le verrou d'un autre processus avant de rendre « occupé » ; 5 s par défaut.</param>
    /// <param name="registre">Relit le levier encore en essai, pour que le refus d'un autre essai dise la valeur lue et où la
    /// remettre.</param>
    /// <param name="nom">Le nom affiché d'un levier ; sans lui, son id.</param>
    public FichierRevertEnAttente(string chemin, TimeSpan? attente = null, Tweaks.Hardware.IRegistryReader? registre = null,
        Func<string, string>? nom = null)
    {
        (_registre, _nom) = (registre, nom);
        _attente = attente ?? TimeSpan.FromSeconds(5);
        _chemin = chemin;
        _fichier = new FichierJsonAtomique(chemin, CodeErreur);
    }

    public static FichierRevertEnAttente ParDefaut(Tweaks.Hardware.IRegistryReader? registre = null, Func<string, string>? nom = null) =>
        new(FichierJsonAtomique.CheminLocal("pending-revert.json"), registre: registre, nom: nom);

    // Poser, Lever et Garder lisent, modifient et réécrivent : l'App, le banc et l'outil de validation écrivent ce fichier ; le
    // verrou voisin (le même que les autres journaux du compte) empêche l'un d'effacer la mise à jour de l'autre.
    private Result SousVerrou(Func<Result> ecrire)
    {
        using var verrou = Parties.VerrouDuReleve.Attendre(_chemin + ".verrou", _attente);
        return verrou is null ? Result.Fail(CodeErreur, Docteur.T("RV.JournalOccupe")) : ecrire();
    }

    public Result Poser(string tweakId, TweakSnapshot? snapshot) => SousVerrou(() => PoserSousVerrou(tweakId, snapshot));

    private Result PoserSousVerrou(string tweakId, TweakSnapshot? snapshot)
    {
        // Un seul essai tient dans ce fichier. Poser un autre levier par-dessus un essai
        // pas encore rendu effacerait son origine pour toujours (essai démarré après une remise au démarrage refusée).
        // Le même levier aussi. Retenté après une remise ratée, il recapturerait comme « origine » la valeur
        // que DrDXT avait posée. Ne sont permis que compléter son propre essai (pose sans origine puis avec) et remplacer
        // l'origine de son propre essai par une autre : ses appelants (l'exercice, le ventilateur) ne partent que d'un
        // journal vide.
        var lu = LireContenu();
        if (lu is { IsOk: true, Value: { TweakId: { } enCours } contenu }
            && (!string.Equals(enCours, tweakId, StringComparison.Ordinal) || (contenu.Snapshot is not null && snapshot is null)))
            return Result.Fail(CodeErreur, Bloque(enCours, contenu.Snapshot));
        if (!lu.IsOk && !MettreDeCoteSiIllisible()) return Result.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
        return Ecrire(new Contenu(tweakId, snapshot, lu.Value?.Gardes ?? Array.Empty<RevertEnAttenteEntree>()));
    }

    /// <summary>Le refus d'un essai par-dessus un levier encore posé n'est pas un cul-de-sac : le levier, la valeur relue, et où
    /// le remettre à la main.</summary>
    private string Bloque(string enCours, TweakSnapshot? snapshot)
    {
        Tweaks.Relecture? relu = null;
        try { if (_registre is not null && snapshot is not null) relu = Tweaks.TweakReadback.Relire(snapshot, _registre); }
        catch (Exception) { /* relecture ratée : « non lu » */ }
        return Docteur.T("RV.EncoreEnEssai.Geste", _nom?.Invoke(enCours) ?? enCours,
            relu?.ValeurCourante ?? Docteur.T("RV.ValeurNonLue"), relu?.Cible ?? Docteur.T("RV.EndroitDuReglage"));
    }

    public Result Lever() => SousVerrou(LeverSousVerrou);

    private Result LeverSousVerrou()
    {
        var lu = LireContenu();
        if (!lu.IsOk)
            return MettreDeCoteSiIllisible() ? Result.Ok() : Result.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
        var gardes = lu.Value?.Gardes ?? Array.Empty<RevertEnAttenteEntree>();
        if (gardes.Count > 0) return Ecrire(new Contenu(null, null, gardes));
        try
        {
            if (FichierJsonAtomique.RefusEleve(_chemin, CodeErreur) is { } refus) return refus;
            File.Delete(_chemin);   // absent = deja leve, pas une erreur
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(CodeErreur, $"Suppression impossible : {ex.Message}");
        }
    }

    public Result Ecarter() => SousVerrou(() =>
    {
        try
        {
            if (FichierJsonAtomique.RefusEleve(_chemin, CodeErreur) is { } refus) return refus;
            if (File.Exists(_chemin)) File.Copy(_chemin, _chemin + ".ecarte", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Result.Fail(CodeErreur, ex.Message);   // pas de trace : l'entrée reste, le lancement suivant réessaie
        }
        return LeverSousVerrou();
    });

    public Result Garder(string tweakId, TweakSnapshot snapshot) => SousVerrou(() => GarderSousVerrou(tweakId, snapshot));

    private Result GarderSousVerrou(string tweakId, TweakSnapshot snapshot)
    {
        // Relu avant d'ecrire : un fichier illisible n'est pas une liste vide a ecraser, il est mis de cote.
        var lu = LireContenu();
        if (!lu.IsOk && !MettreDeCoteSiIllisible()) return Result.Fail(CodeErreur, lu.Error?.Message ?? "lecture impossible");
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

    // Un fichier illisible (coupure, ou fichier ecrit par un autre programme du compte) portait peut-etre les origines des
    // leviers gardes : ecrase, elles disparaissaient ; garde, il bloquait tout nouvel essai. Le demarrage l'a dit
    // (RV.JournalIllisible) ; il est mis de cote, sa trace reste. Faux si le fichier ne s'est pas lu du tout (tenu, droits) :
    // ce n'est pas un contenu illisible, rien n'est ecrit.
    private bool MettreDeCoteSiIllisible()
        => _fichier.Read().IsOk && _fichier.MettreDeCote();

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
    /// <summary>Les leviers dont chaque écriture passe par le journal d'écriture anticipée du helper : tué avant d'en garder
    /// l'origine, l'App ne sait pas la rendre, le helper si. Tenue égale à ce que le rejeu rend vraiment, levier par levier
    /// (InvariantsDesLeviersTests).</summary>
    public static readonly IReadOnlySet<string> LeviersJournalises = new HashSet<string>(StringComparer.Ordinal)
    {
        "latency.reflex", "latency.render_queue_cap", "latency.vrr_vsync_cap", "latency.hagsOn", "latency.gameModeOn",
        "latency.gameDvrBackgroundOff", "latency.mouse_acceleration_off", "latency.vrrFlipModelOn", "latency.prefer_max_perf",
        "gpu.power_limit", "fixit.x3d_ccd", "fixit.power_plan", "fixit.mode_boost", "fixit.wifi_performances",
        "fixit.plan_economie", "fixit.cpu_power_limit", "fixit.cpu_power_cap", "fixit.pagefile_ssd", "fixit.heures_actives",
    };

    /// <summary>Rend la phrase a montrer au joueur, ou <c>null</c> quand rien n'etait en attente.
    /// Le journal n'est leve que si le levier est rendu (ou s'il n'y a rien a rendre) : un revert
    /// rate se retentera au prochain demarrage. Un levier garde n'est jamais en essai : il n'est
    /// ni rendu ni efface ici.</summary>
    public static string? Executer(IRevertEnAttente journal, ITweakCatalog catalogue) => Executer(journal, catalogue, out _);

    /// <param name="remiseUsineRequise">Vrai quand le helper a refusé de rendre la fréquence de la carte AMD (au-delà de
    /// sa bande) : l'écran propose alors la remise à l'usine, sur clic seulement. Le démarrage, lui, n'agit pas.</param>
    /// <param name="nom">Le titre affiché d'un réglage, que l'App passe ; par défaut, l'identifiant.</param>
    /// <param name="raison">La raison lisible d'un échec, que l'App passe ; par défaut, son code. Le message (souvent
    /// l'anglais technique du helper) n'entre jamais dans la phrase.</param>
    /// <param name="registre">Le lecteur du registre : seule la valeur de DrDXT se rend (changée depuis, rien n'est réécrit
    /// par-dessus ; revenue à l'origine, rien à écrire), et « remis » ne se dit que relu à l'origine. Sans lui, la réussite du
    /// levier fait foi.</param>
    /// <param name="verdict">Le vérificateur de la pose, pour une feuille que le registre ne relit pas (pilote, carte, plan) :
    /// la même règle, avant et après l'écriture.</param>
    public static string? Executer(IRevertEnAttente journal, ITweakCatalog catalogue, out bool remiseUsineRequise,
        Func<string, string>? nom = null, Func<Result, string>? raison = null, Tweaks.Hardware.IRegistryReader? registre = null,
        Func<TweakSnapshot, Tweaks.EtatApplique>? verdict = null)
    {
        remiseUsineRequise = false;
        string Nom(string id) => nom?.Invoke(id) ?? id;
        string Raison(Result r) => raison?.Invoke(r) ?? r.Error?.Code ?? string.Empty;
        var lu = journal.Lire();
        if (!lu.IsOk)
            return Docteur.T("RV.JournalIllisible", Raison(lu));
        if (lu.Value is not { } entree) return null;

        if (entree.Snapshot is not { } snapshot)
        {
            journal.Lever();
            return Docteur.T(LeviersJournalises.Contains(entree.TweakId) ? "RV.SansOrigineGardee" : "RV.SansOrigine", Nom(entree.TweakId));
        }

        var levier = catalogue.Get(entree.TweakId);
        if (!levier.IsOk || levier.Value is null)
        {
            journal.Ecarter();
            return Docteur.T("RV.LevierDisparu", Nom(entree.TweakId));
        }

        // Ce fichier vit sous %LOCALAPPDATA%, écrivable par tout
        // processus du compte, et le levier rejoue son contenu par le helper élevé. Sans ce contrôle,
        // un fichier piégé ferait exécuter n'importe quel op de l'allowlist (redémarrage dans le BIOS,
        // BCD, écriture du manifeste…) sous une autorisation Windows donnée « à DrDXT ». Seuls les
        // types qu'un levier utilise réellement comme inverse passent ; tout le reste, et tout type
        // futur, est refusé. Rien n'est rejoué, le joueur est prévenu, et l'entrée est écartée.
        if (!AnnulationSure(entree.TweakId, snapshot.OriginalValueJson))
        {
            journal.Ecarter();
            return Docteur.T("RV.InverseRefuse", Nom(entree.TweakId));
        }

        var pose = Tweaks.TweakReadback.EtatDeLaPose(snapshot, registre, verdict);
        if (pose != Tweaks.PoseRelue.DeDrDxt)
        {
            journal.Lever();
            return Docteur.T(pose == Tweaks.PoseRelue.ChangeDepuis ? "RV.ChangeDepuis" : "RV.Remis", Nom(entree.TweakId));
        }

        Result rendu;
        try { rendu = levier.Value.RevertAsync(snapshot).GetAwaiter().GetResult(); }
        catch (Exception ex) { rendu = Result.Fail(CodeRevert, ex.Message); }

        if (!rendu.IsOk)
        {
            remiseUsineRequise = rendu.Error?.Code == ErrorCodes.GpuRemiseUsineRequise;
            return Docteur.T("RV.RenduEchoue", Nom(entree.TweakId), Raison(rendu));
        }

        // Rendu, mais la valeur de DrDXT est encore là (un pilote qui borne) : le journal reste, le lancement suivant réessaie.
        // Levé ici, plus rien ne l'aurait rendu.
        if (Tweaks.TweakReadback.EncorePoseRelue(snapshot, registre, verdict))
            return Docteur.T("RV.EncorePose", Nom(entree.TweakId));
        journal.Lever();
        var relu = registre is null ? Tweaks.EtatRelu.NonRelisable : Tweaks.TweakReadback.Relire(snapshot, registre).Etat;
        var remis = relu == Tweaks.EtatRelu.RevenuAvantApplication
            || relu == Tweaks.EtatRelu.NonRelisable && (verdict is null || verdict(snapshot) == Tweaks.EtatApplique.NotApplied);
        return Docteur.T(remis ? "RV.Remis" : "RV.RemisNonRelu", Nom(entree.TweakId));
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

    // Le type seul laisserait un levier de registre rejouer n'importe quelle feuille que le helper
    // permet (HAGS portant « PagingFiles vide » désactiverait le fichier d'échange). Un levier ne rejoue que les
    // feuilles que son code écrit. `CppcEnable` reste à x3d_ccd : une ancienne version l'écrivait, un ancien
    // fichier peut encore porter son inverse.
    private static bool FeuilleDuLevier(string tweakId, PrivilegedOp op)
    {
        // Même trou côté NVIDIA : le helper borne l'identifiant DRS, jamais la valeur. Un levier DRS ne
        // rejoue que ses propres réglages (un limiteur d'images rangé sous « préférer les performances »
        // briderait toute la machine).
        uint? drs = op switch
        {
            ApplyNvApiDrsSettingOp a => a.SettingId,
            DeleteNvApiDrsSettingOp d => d.SettingId,
            _ => null,
        };
        if (drs is { } id)
            return DrsParLevier.TryGetValue(tweakId, out var ids) && ids.Contains(id);

        // Côté AMD, le genre de l'op ADLX : la file d'images n'écrit que l'Anti-Lag, et ne doit rejouer
        // ni une limite de puissance ni une sous-tension.
        if (op is ApplyAdlxTuningOp adlx)
            return GenreAdlxParLevier.TryGetValue(tweakId, out var genres) && genres.Contains(adlx.Kind);

        var (ruche, chemin, nom) = op switch
        {
            SetRegistryValueOp s => (s.Hive, s.Path, s.Name),
            DeleteRegistryValueOp d => (d.Hive, d.Path, d.Name),
            _ => (default(RegistryHiveKind), (string?)null, (string?)null),
        };
        // L'ecriture de registre se reconnait a son type. Reconnue au chemin non nul, un op de
        // registre sans chemin passerait pour « pas du registre » et atteindrait le levier.
        if (op is not (SetRegistryValueOp or DeleteRegistryValueOp)) return true; // le type suffit
        if (string.IsNullOrWhiteSpace(chemin) || string.IsNullOrWhiteSpace(nom)) return false;
        return FeuillesParLevier.TryGetValue(tweakId, out var feuilles)
            && feuilles.Any(f => f.Ruche == ruche
                && string.Equals(f.Chemin, chemin.Trim().Trim('\\'), StringComparison.OrdinalIgnoreCase)
                && string.Equals(f.Nom, nom, StringComparison.Ordinal));
    }

    /// <summary>Les clés de HKLM où écrivent ces leviers : la date de dernière écriture d'une telle clé peut être celle
    /// de DrDXT, et les traces d'autres outils le disent.</summary>
    public static IEnumerable<string> ClesMachineDesLeviers(IEnumerable<string> leviers)
        => leviers.SelectMany(id => FeuillesParLevier.TryGetValue(id, out var f) ? f : [])
            .Where(f => f.Ruche == RegistryHiveKind.LocalMachine).Select(f => f.Chemin).Distinct(StringComparer.OrdinalIgnoreCase);

    // Identifiants relevés dans chaque levier (NvApiDriverSettings.h) : PREFERRED_PSTATE, VSYNCMODE et
    // FRL_FPS, PRERENDERLIMIT.
    private static readonly IReadOnlyDictionary<string, uint[]> DrsParLevier = new Dictionary<string, uint[]>(StringComparer.Ordinal)
    {
        ["latency.prefer_max_perf"] = [0x1057EB71],
        ["gpu.repos_global_normal"] = [0x1057EB71],
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
            ["fixit.pagefile_ssd"] = [(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "PagingFiles")],
            ["fixit.heures_actives"] = [(RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomDebut),
                                        (RegistryHiveKind.LocalMachine, HeuresActives.Cle, HeuresActives.NomFin)],
            ["fixit.x3d_ccd"] = [(RegistryHiveKind.CurrentUser, GameBar, "AllowAutoGameMode"),
                                 (RegistryHiveKind.CurrentUser, GameBar, "AutoGameModeEnabled"),
                                 (RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable")],
        };

    public static IEnumerable<string> LeviersRejouables => InverseParLevier.Keys;

    /// <summary>Une annulation lue dans un fichier du compte (%LOCALAPPDATA%) peut-elle servir ? Les leviers
    /// rejouent leur inverse par le helper élevé sans le relire : la garde est donc à la lecture de chaque fichier, et
    /// tout fichier qui porte une annulation passe ici. Le mode d'écran se rend hors du helper, sans droits, depuis
    /// son propre format : il n'a pas d'op à filtrer.</summary>
    public static bool AnnulationSure(string tweakId, string? json)
        => InverseRejouable(tweakId, json) || InversesHorsHelper.Contains(tweakId)
           // Un geste du docteur gardé ne rejoue que les inverses qu'il écrit lui-même (Tweaks.LevierDeGeste).
           || Tweaks.LevierDeGeste.Inverses(tweakId, json) is not null;

    private static readonly HashSet<string> InversesHorsHelper = new(StringComparer.Ordinal) { "fixit.ecran_frequence" };

    // Le type seul ne suffit pas. Un fichier nommant HAGS avec une limite de
    // puissance GPU ferait écrire le GPU au lancement. Chaque levier ne rejoue que ce que son ApplyAsync écrit
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
            ["gpu.repos_global_normal"] = [.. Drs],
            ["latency.vrr_vsync_cap"] = [.. Drs, typeof(ApplyAdlxTuningOp)],
            ["latency.render_queue_cap"] = [.. Drs, typeof(ApplyAdlxTuningOp)],
            ["fixit.sane_pagefile"] = [.. Registre],
            ["fixit.pagefile_ssd"] = [.. Registre],
            ["fixit.heures_actives"] = [.. Registre],
            ["fixit.power_plan"] = [typeof(SetActivePowerPlanOp)],
            ["fixit.plan_economie"] = [typeof(SetActivePowerPlanOp)],
            ["fixit.mode_boost"] = [typeof(SetProcessorBoostModeOp)],
            ["fixit.cpu_power_limit"] = [typeof(SetCpuPackagePowerLimitOp)],
            ["fixit.cpu_power_cap"] = [typeof(SetCpuPackagePowerLimitOp)],
            ["fixit.wifi_performances"] = [typeof(SetWirelessPowerSavingOp)],
            ["fixit.x3d_ccd"] = [.. Registre, typeof(RunServiceControlOp)],
            ["gpu.undervolt"] = [typeof(ApplyNvApiClockOffsetOp), typeof(ApplyAdlxTuningOp)],
            ["gpu.power_limit"] = [typeof(ApplyNvApiPowerLimitOp), typeof(ApplyAdlxTuningOp)],
            // Une vitesse fixe du mode Expert ne se rend qu'en rendant l'automatique.
            ["gpu.fan_curve"] = [typeof(RestoreGpuFanAutoOp)],
        };
}
