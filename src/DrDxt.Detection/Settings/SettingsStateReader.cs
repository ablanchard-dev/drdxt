using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Network;

using DrDxt.Detection.Hardware;

namespace DrDxt.Detection.Settings;

public sealed class SettingsStateReader : ISettingsStateReader
{
    private readonly IPowerProvider _power;
    private readonly IRegistryProvider _registry;
    private readonly INetworkAdapterReader _network;
    // Optionnels en queue : toutes les constructions existantes tiennent, et leur absence
    // laisse FrameCap a null : « jamais calcule », jamais « rien a signaler ».
    private readonly INvApi? _nvApi;
    private readonly IDisplayInfo? _display;
    private readonly IUnexpectedShutdownLog? _shutdowns;
    private readonly IGameExecutable? _game;
    private readonly IHagsEffectiveReader? _hags;
    // La limite d'images d'une carte AMD (FRTC / Chill par ADLX), quand NVAPI n'en rend pas.
    private readonly IAdlx? _adlx;
    // Les vidages d'écran bleu, pour la garde de stabilité partagée avec Curve Optimizer.
    private readonly IDumpCreationLog? _vidages;

    private const uint PreferredPstateId = 0x1057EB71; // PREFERRED_PSTATE_ID

    public SettingsStateReader(IPowerProvider power, IRegistryProvider registry,
        INetworkAdapterReader network, INvApi? nvApi = null, IDisplayInfo? display = null,
        IUnexpectedShutdownLog? shutdowns = null, IGameExecutable? game = null,
        IHagsEffectiveReader? hags = null, IAdlx? adlx = null,
        // La limite de puissance du processeur (PawnIO). null = non lue : le levier ne s'arme pas.
        DrDxt.Application.Tweaks.Hardware.ICpuPowerLimitReader? limitePuissance = null, IDumpCreationLog? vidages = null)
        => (_power, _registry, _network, _nvApi, _display, _shutdowns, _game, _hags, _adlx, _limitePuissance, _vidages)
            = (power, registry, network, nvApi, display, shutdowns, game, hags, adlx, limitePuissance, vidages);

    private readonly DrDxt.Application.Tweaks.Hardware.ICpuPowerLimitReader? _limitePuissance;

    /// <summary>Vrai quand la couture du jeu en cours est branchee : sans elle, les reglages du
    /// pilote se lisent au profil global, quel que soit le jeu. « Enregistre » n'est pas
    /// « branche » : c'est ce qu'observe le test de composition.</summary>
    public bool ReadsAtGameScope => _game is not null;

    public Task<Result<SettingsState>> ReadAsync(HardwareInventory hw) => Task.Run(() =>
    {
        var power = _power.GetActiveSchemeGuid();
        if (!power.IsOk)
            return Result<SettingsState>.Fail(power.Error!.Value.Code, power.Error.Value.Message);

        var xmp = XmpInferencer.Infer(hw.Memory);
        // ReBar vient du GPU principal déjà détecté, pas relu ici.
        // NVIDIA → Unknown (étiquette honnête).
        var rebar = hw.Gpus.Count > 0 ? hw.Gpus[0].ReBar : TriState.Unknown;

        // Uptime = temps depuis la dernière initialisation du noyau. Environment.TickCount64 est
        // exactement GetTickCount64, et ne repart pas à zéro sans vrai redémarrage, c'est tout
        // l'intérêt : avec le démarrage rapide il continue de compter, et l'inférence refuse de conclure.
        var restart = RestartStateInferencer.Infer(
            _registry.GetServicingRebootPending(),
            _registry.GetPendingFileRenames(),
            _registry.GetFastStartup(),
            TimeSpan.FromMilliseconds(Environment.TickCount64),
            sinceChange: null);   // diagnostic à froid : décrit la machine, pas un changement

        // Le cap d'images : mesure sur cette machine, il valait 141 (= 144 - 3, la regle de
        // faible latence du second ecran) alors que le principal tourne a 200 Hz.
        // Une seule enumeration d'ecrans pour les deux verdicts qui en dependent. Deux appels
        // donneraient deux instantanes differents, et le cap pourrait etre juge contre un etat
        // d'affichage que le levier de frequence ne voit pas : deux verdicts qui se contredisent
        // sur la meme machine.
        IReadOnlyList<DisplayInfo>? displays = null;
        if (_display is not null)
        {
            var read = _display.GetAllDisplays();
            // Le motif `is { IsOk: true }` traite un Result NUL sans exploser : c'est une couture,
            // et une couture rend ce qu'elle veut. Ecrit d'abord sans cette garde, et deux tests
            // sont tombes sur un NullReferenceException.
            displays = read is { IsOk: true } ? read.Value : null;
        }

        // L'ecran tourne-t-il sous sa frequence maximale ? Mesure sur la machine de reference :
        // panneau 200 Hz pilote a 143 Hz trois jours de suite. Lu sur l'ecran principal : le mesurer
        // sur le mauvais ecran serait aussi inutile que d'en changer le mode.
        // Les frequences de tous les ecrans, pas seulement du principal. Elles sont
        // deja dans `displays` : ne pas les transporter rendait la consommation au repos
        // indetectable. Une frequence a 0 est gardee telle quelle : c'est l'advisor qui decide
        // qu'une lecture ratee ne conseille rien, pas le lecteur qui la maquille.
        var attachedRefreshHz = displays?.Select(d => d.RefreshHz).ToList();

        RefreshHeadroom? displayRefresh = null;
        if (displays?.FirstOrDefault(d => d.IsPrimaryGameDisplay) is { } panel)
        {
            // MaxRefreshHz vaut 0 quand il n'a pas ete lu. Une liste vide fait rendre Unknown a
            // l'heuristique, ce qui est exactement la bonne reponse. Ne jamais le remplacer par
            // RefreshHz : ca transformerait « pas lu » en « deja au maximum ».
            var modes = panel.MaxRefreshHz > 0 ? new[] { panel.MaxRefreshHz } : Array.Empty<int>();
            displayRefresh = DisplayRefreshHeuristic.Evaluate(panel.RefreshHz, modes);
        }

        // Le cap d'images : mesure sur cette machine, il valait 141 (= 144 - 3, la regle de
        // faible latence du second ecran) alors que le principal tourne a 200 Hz.
        FrameCapState? frameCap = null;
        if ((_nvApi is not null || _adlx is not null) && _display is not null)
        {
            // Au profil du jeu quand on le connait, comme
            // LirePreferMaxPerf. VrrVsyncCap pose le plafond au profil du jeu depuis `5619aa8` : lu au
            // global, le scan suivant ne voyait pas le plafond que le produit avait lui-meme pose.
            const uint FrameRateLimitId = 0x10835002; // FRL_FPS_ID
            var jeu = _nvApi is null ? null : _game?.RunningGameExecutablePath();
            var auJeu = jeu is null ? null : _nvApi!.GetDrsForApplication(FrameRateLimitId, jeu) is { IsOk: true } lu
                ? ((uint Value, DrsOriginState Origin)?)lu.Value
                : null;
            var cap = auJeu is { } j ? Result<uint>.Ok(j.Value)
                : _nvApi?.GetFrameRateLimit() ?? Result<uint>.Fail(ErrorCodes.TweakSdkUnavailable, "NVAPI absent.");
            // Pas de limite NVIDIA lue, celle du pilote AMD (FRTC / Chill) si ADLX la lit. Pas d'origine.
            var capAmd = cap is { IsOk: true } ? null : _adlx?.GetFrameRateLimit();
            if (capAmd is { IsOk: true }) cap = Result<uint>.Ok((uint)capAmd.Value);
            // L'origine informe l'utilisateur sur ce qu'il faut faire du cap (le corriger
            // soi-meme, ou ne pas toucher un reglage livre par NVIDIA pour ce jeu). Elle n'entre
            // jamais dans le verdict : d'ou vient un cap ne change pas s'il coute des images.
            var origin = auJeu is { } o ? Result<DrsOriginState>.Ok(o.Origin)
                : capAmd is { IsOk: true } || _nvApi is null
                    ? Result<DrsOriginState>.Fail(ErrorCodes.TweakSdkUnavailable, Docteur.T("SSR.OrigineNonLue"))
                    : _nvApi.GetFrameRateLimitOrigin();
            frameCap = FrameCapInferencer.Infer(
                cap is { IsOk: true } ? (int)cap.Value : null,
                displays,
                origin is { IsOk: true } ? origin.Value : null);
        }

        var pstate = LirePreferMaxPerf();

        // Le VRR lu au pilote sur l'ecran du jeu. Non lu : la cle de registre, comme avant
        // (Unknown sur NVIDIA), jamais un Off fabrique.
        var ecranJeu = displays?.FirstOrDefault(d => d.IsPrimaryGameDisplay);

        // « actif » (NVAPI) dit si le VRR tourne a cet instant : au bureau, avec G-Sync
        // en « plein ecran seulement », il vaut Off alors que G-Sync est active. Le scan afficherait
        // alors « VRR inactif, a activer dans le pilote » a qui vient de l'activer (machine de
        // reference : VRR_MODE = 1, actif = Off). Le reglage du pilote, lu, fait foi.
        var vrrPilote = _nvApi?.GetGsyncMode() is { IsOk: true } mode
            ? (mode.Value == 0 ? TriState.Off : TriState.On)
            : (TriState?)null;

        var state = new SettingsState(
            XmpExpo: xmp,
            ReBar: rebar,
            PowerPlanGuid: power.Value!,
            Hags: _registry.GetHags(),
            // L'etat effectif (D3DKMT). La cle reste lue : c'est elle que le levier
            // ecrit et restaure. Sans lecteur branche : Unknown, jamais Off.
            HagsEffective: _hags?.Lire() ?? TriState.Unknown,
            GameMode: _registry.GetGameMode(),   // passe-plat : l'Unknown survit jusqu'au levier
            AccelerationSouris: _registry.GetAccelerationSouris(),
            ParkedCoreIndexes: _registry.GetParkedCoreIndexes(),
            Vrr: vrrPilote ?? (ecranJeu?.VrrEnabled is TriState.On or TriState.Off ? ecranJeu.VrrEnabled : _registry.GetVrr()),
            VrrCapableDisplay: ecranJeu?.VrrPossible ?? TriState.Unknown,
            PagefileConfigured: _registry.GetPagefile(),
            FichierDEchangeADeplacer: _registry.GetFichierDEchangeADeplacer(),
            // Echec de lecture => null, pas une liste vide : « pas lu » et « aucune carte
            // connectee » ne se confondent pas, et l'un des deux ne doit rien conseiller.
            NetworkAdapters: _network.Read() is { IsOk: true } nics ? nics.Value : null,
            // La grandeur dure derriere XmpExpo, pour que la boucle de verification
            // juge un delta mesure (2133 -> 3800) au lieu de comparer deux inferences. 0 quand
            // aucun module n'a rendu de frequence, jamais « 0 MHz ».
            MemoryConfiguredMhz: hw.Memory.Count > 0 ? hw.Memory.Max(m => m.ConfiguredMhz) : 0,
            // LR : les redemarrages sans arret propre. Trois etats, pas deux : pas de seam et
            // journal illisible rendent tous deux `null` (jamais lu), une machine propre rend une
            // liste vide. Traduire un echec de lecture en liste vide ferait dire au produit
            // « rien a signaler » quand il veut dire « je n'ai pas pu regarder ».
            UncleanShutdowns: _shutdowns?.Since(
                DateTimeOffset.UtcNow.AddDays(-MemoryStabilityAdvisor.WindowDays))
                is { IsOk: true } log ? log.Value : null,
            // La garde XMP lit les mêmes incidents que la garde Curve Optimizer ; sans lecteur, null : « non lu ».
            IncidentsDeStabilite: _shutdowns is not null && _vidages is not null
                ? DrDxt.Detection.Repair.GardeDeStabilite.Dates(DrDxt.Detection.Repair.GardeDeStabilite.Lire(_shutdowns, _vidages, DateTimeOffset.UtcNow))
                : null,
            // Sans cette ligne, tout le diagnostic de consommation au repos serait mort : la liste
            // calculee plus haut serait jetee et l'etat dirait « jamais lu ». Une chaine complete
            // avec un null au milieu se comporte comme une chaine qui marche.
            AttachedRefreshHz: attachedRefreshHz,
            // L'etat courant du mode de gestion d'alimentation du pilote. Sans lui, le
            // levier qui l'ecrit se proposait sans savoir si la cible etait deja en place : sur la
            // machine de reference elle l'etait, et le produit aurait vendu un no-op.
            NvidiaPreferMaxPerf: pstate.Etat,
            NvidiaPreferMaxPerfOrigin: pstate.Origine,
            NvidiaDrsScope: pstate.Scope,
            // Jamais `TriState.On` quand `IsUefi` est faux : `FirmwareProvider.IsUefi()` rend `false`
            // dans quatre cas, pas un : demarrage legacy reel (type 1), echec de `GetFirmwareType`,
            // `type == 0` (la valeur documentee quand Windows ne sait pas), et exception. Dans trois cas
            // sur quatre la donnee n'a pas ete lue ; en conclure « CSM actif » promouvrait un Unknown.
            //
            // Un `bool` ne distingue pas « legacy » de « pas su ». Le rendre correct demande que
            // `IFirmwareProvider` cesse d'aplatir le type firmware en booleen (6 appelants).
            // En attendant, on n'affirme rien.
            Csm: TriState.Unknown,
            Restart: restart,
            FrameCap: frameCap,
            DisplayRefresh: displayRefresh,
            // Passe-plat, comme GameMode : l'Unknown survit jusqu'au levier, qui decide
            // seul quoi en faire. L'ecraser ici en un booleen ferait disparaitre la seule
            // information qui empeche de fabriquer un deficit.
            SysMain: _registry.GetSysMain(),
            MmcssIntact: _registry.GetMmcssIntact(),
            HeuresActives: _registry.GetHeuresActives(),
            // Sans droits ; non lu = null, le levier du boost ne s'arme pas.
            ModeDeBoost: Guid.TryParse(power.Value, out var plan) ? _power.LireModeDeBoost(plan) : null,
            // PL1/PL2 du processeur, copie MMIO comprise ; non lu = null.
            LimitePuissance: _limitePuissance?.LireRegistres() is { IsOk: true, Value: { } limite } ? limite.Lue : null,
            // L'économie d'énergie de la carte sans fil (secteur) du plan actif ; non lu = null.
            EconomieSansFil: Guid.TryParse(power.Value, out var planSansFil) ? _power.LireEconomieSansFil(planSansFil) : null);

        return Result<SettingsState>.Ok(state);
    });

    /// <summary>« Prefer maximum performance » deja actif (On), « Adaptive » (Off), ou non lu.
    /// Sans pilote NVIDIA disponible, Unknown, jamais Off : « je n'ai pas lu » n'est pas
    /// « le mode economique est actif », et un candidat fonde sur une absence de lecture est un
    /// candidat fonde sur rien.</summary>
    private (TriState Etat, DrsOriginState? Origine, string? Scope) LirePreferMaxPerf()
    {
        if (_nvApi is null || !_nvApi.IsAvailable) return (TriState.Unknown, null, null);

        // Au scope du jeu quand on le connait : c'est ce profil que le pilote
        // applique. Un profil de jeu peut porter sa propre valeur ; lire le global dirait alors
        // « deja au maximum » a un jeu qui ne l'est pas, ou l'inverse.
        // Jeu inconnu, ou jeu sans profil propre : le pilote applique le global, on le lit, et le
        // scope null le dit.
        var jeu = _game?.RunningGameExecutablePath();
        if (jeu is not null && _nvApi.GetDrsForApplication(PreferredPstateId, jeu) is { IsOk: true } auJeu)
            return (Traduire(auJeu.Value.Value), auJeu.Value.Origin, jeu);

        // Absent du profil de base : au défaut du pilote, PREFERRED_PSTATE_DEFAULT = OPTIMAL_POWER (5), donc pas au maximum.
        var lu = _nvApi.GetPreferredPstate();
        return lu.IsOk ? (Traduire(lu.Value), null, null)
            : _nvApi.PreferredPstateAuDefaut() ? (Traduire(5), null, null)
            : (TriState.Unknown, null, null);
    }

    /// <summary>NvApiDriverSettings.h (github.com/NVIDIA/nvapi) définit PREFERRED_PSTATE de 0 à 5,
    /// avec OPTIMAL_POWER (5) pour défaut, pas ADAPTIVE (0). Seul PREFER_MAX (1) est « au maximum » ; toute autre valeur
    /// de l'en-tête, lue, dit « pas au maximum ». Une valeur hors de l'en-tête reste non lue.</summary>
    private static TriState Traduire(uint pstate) =>
        pstate switch { 1 => TriState.On, <= 5 => TriState.Off, _ => TriState.Unknown };
}
