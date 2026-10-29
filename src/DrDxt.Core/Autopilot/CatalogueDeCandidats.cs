namespace DrDxt.Core;

/// <summary>
/// Ce que chaque levier du produit declare de lui-meme, en un seul endroit, lu par l'outil (`tools/candidatprobe`) comme
/// par l'App : un seul jugement. `EffetAttenduPct` reste <c>null</c> pour tous, faute d'effet source (un chiffre devine
/// ferait refuser ou accepter un essai sur une estimation) ; voir <see cref="Decidabilite"/>.
/// - `latency.reflex` : NVIDIA, annonce du pilote 436.02 (nvidia.com/en-us/geforce/news/gamescom-2019-game-ready-driver) :
///   « support for all GPUs in DX9 and DX11 games (in DX12 and Vulkan titles, the game decides when to
///   queue the frame) » et « reduces latency by up to 33% ». « Jusqu'a » est un maximum, pas un effet
///   attendu ; et sur DX12 (Warzone) l'effet est nul par construction. Le guide latence NVIDIA le range
///   en repli « si NVIDIA Reflex n'est pas disponible dans votre jeu ». Aucun chiffre ne vaut pour tous les jeux.
/// - `fixit.power_plan` : aucune mesure publiee trouvee (jeux, processeur, passes), des avis de forum seulement (un
///   resume de recherche attribuait « 0-2 FPS » a une page qui ne le contient pas).
/// La nature (<see cref="CandidateKind"/>) doit egaler ce que le levier dit de lui-meme
/// (`TakesEffectAfterReboot`) : un test de l'App le verifie sur tous les leviers enregistres.
/// </summary>
public static class CatalogueDeCandidats
{
    private static readonly string[] Latence = { CandidateSelector.GrandeurLatence };
    private static readonly string[] FrequenceGpu = { CandidateSelector.GrandeurFrequenceGpu };
    private static readonly string[] PuissanceGpu = { CandidateSelector.GrandeurPuissanceGpu };

    private static readonly IReadOnlyDictionary<string, CandidateDescriptor> Table =
        // La surface vient des operations privilegiees que le levier ecrit (lues dans chaque `ApplyAsync`), jamais de sa
        // famille. Une surface OS utilisateur est prouvee par `SurfaceDesLeviersTests` (App : chaque ecriture vise HKCU) ;
        // une surface fermee n'a pas besoin de preuve, au pire elle refuse de trop.
        new CandidateDescriptor[]
        {
            // GPU : ecrivent sur la carte, risque 3, stabilite a verifier.
            // Offset NVAPI (outil), `ApplyNvApiClockOffsetOp` / ADLX undervolt, puissance, courbe de ventilation.
            new("gpu.core_offset", CandidateKind.AutoHot, 3, true, FrequenceGpu, Family: CandidateFamily.Gpu,
                Surface: SurfaceTouchee.CarteGraphique),
            new("gpu.undervolt", CandidateKind.AutoHot, 3, true,
                new[] { CandidateSelector.GrandeurFrequenceGpu, CandidateSelector.GrandeurPuissanceGpu },
                Family: CandidateFamily.Gpu, Surface: SurfaceTouchee.CarteGraphique),
            new("gpu.power_limit", CandidateKind.AutoHot, 3, true, PuissanceGpu, Family: CandidateFamily.Gpu,
                Surface: SurfaceTouchee.CarteGraphique),
            new("gpu.fan_curve", CandidateKind.AutoHot, 2, true, new[] { "gpu.thermal" }, Family: CandidateFamily.Thermal,
                Surface: SurfaceTouchee.CarteGraphique),

            // Pilote NVIDIA / latence : reversibles a chaud. Tous ecrivent l'etat du pilote :
            // DRS PREFERRED_PSTATE ; HKLM\...\NVIDIA Corporation\Global\NVTweak\LowLatencyMode ;
            // DRS pre-render limit ou ADLX Anti-Lag ; DRS VSYNCMODE + FRL_FPS.
            new("latency.prefer_max_perf", CandidateKind.AutoHot, 1, false, FrequenceGpu, Family: CandidateFamily.Gpu,
                Surface: SurfaceTouchee.PiloteGraphique),
            new("latency.reflex", CandidateKind.AutoHot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.PiloteGraphique),
            new("latency.render_queue_cap", CandidateKind.AutoHot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.PiloteGraphique),
            new("latency.vrr_vsync_cap", CandidateKind.AutoHot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.PiloteGraphique),

            // Windows : l'effet n'existe qu'apres un redemarrage, rien a mesurer dans la session.
            // HAGS : HKLM\SYSTEM\...\GraphicsDrivers\HwSchMode, l'ordonnancement de la carte ⇒ pilote.
            // Game Mode, Game DVR, modele flip : HKCU seulement ⇒ OS utilisateur.
            // Fichier d'echange : HKLM\...\Memory Management\PagingFiles ⇒ memoire.
            new("latency.hagsOn", CandidateKind.AutoReboot, 2, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.PiloteGraphique),
            new("latency.gameModeOn", CandidateKind.AutoReboot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.OsUtilisateur),
            new("latency.gameDvrBackgroundOff", CandidateKind.AutoReboot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.OsUtilisateur),
            new("latency.vrrFlipModelOn", CandidateKind.AutoReboot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.OsUtilisateur),
            // HKCU Control Panel\Mouse, relu à l'ouverture de session ⇒ OS utilisateur, au redémarrage.
            new("latency.mouse_acceleration_off", CandidateKind.AutoReboot, 1, false, Latence, Family: CandidateFamily.Latency,
                Surface: SurfaceTouchee.OsUtilisateur),
            new("fixit.sane_pagefile", CandidateKind.AutoReboot, 1, false, new[] { "ram.commit" }, Family: CandidateFamily.Memory,
                Surface: SurfaceTouchee.Memoire),

            // Heures d'activite de Windows Update : HKLM\SOFTWARE\Microsoft\WindowsUpdate\UX\Settings, effet
            // immediat mais aucun sur les images ⇒ pose au clic, juge sur les redemarrages tombes en partie.
            new("fixit.heures_actives", CandidateKind.AutoReglage, 0, false, Array.Empty<string>(), Family: CandidateFamily.Repair,
                Surface: SurfaceTouchee.PlanificationWindows),

            // Processeur : politique d'alimentation et routage X3D.
            // `SetActivePowerPlanOp` ; CPPC (HKLM) + service AMD3DVCacheSvc.
            new("fixit.power_plan", CandidateKind.AutoHot, 1, false, new[] { "cpu.power" }, Family: CandidateFamily.Cpu,
                Surface: SurfaceTouchee.Processeur),
            new("fixit.x3d_ccd", CandidateKind.AutoHot, 1, false, new[] { "cpu.sched" }, Family: CandidateFamily.Scheduler,
                Surface: SurfaceTouchee.Processeur),
            // `SetProcessorBoostModeOp`, le boost coupé rendu au défaut du plan. Il change la fréquence,
            // donc les images : mesuré en jeu. Microsoft se contredit sur son effet sous HWP (learn.microsoft.com, « Options
            // for perf state engine PerfBoostMode » et « Power and performance tuning ») : aucun effet chiffré, l'A/B tranche.
            new("fixit.mode_boost", CandidateKind.AutoHot, 1, false, new[] { "cpu.power" }, Family: CandidateFamily.Cpu,
                Surface: SurfaceTouchee.Processeur),

            // `SetCpuPackagePowerLimitOp`, la PL1 montée à la PL2 que la carte autorise. Elle change la
            // fréquence tenue, donc les images (TechPowerUp : −15 % à 65 W, −2 % à 125 W) : mesuré en jeu, rendu sans gain.
            new("fixit.cpu_power_limit", CandidateKind.AutoHot, 1, false, new[] { "cpu.power" }, Family: CandidateFamily.Cpu,
                Surface: SurfaceTouchee.Processeur),

            // Le même registre vers le bas, quand la dernière partie freinée l'a été par la chaleur.
            // Posé au clic, pas en A/B d'images (il promet moins de temps freiné, que le docteur relit sur les
            // parties suivantes) ; la montée s'abstient tant qu'il a quelque chose à dire (Cible).
            new("fixit.cpu_power_cap", CandidateKind.AutoReglage, 0, false, new[] { "cpu.power" }, Family: CandidateFamily.Cpu,
                Surface: SurfaceTouchee.Processeur),

            // `SetWirelessPowerSavingOp`, la carte sans fil sortie de son economie d'energie (plan actif,
            // secteur). Microsoft : l'economie coute de la latence de transfert ; aucun effet sur les images ⇒ pose au clic,
            // relu ; la gigue vers la box se voit au scan suivant.
            new("fixit.wifi_performances", CandidateKind.AutoReglage, 0, false, new[] { "latence" }, Family: CandidateFamily.Network,
                Surface: SurfaceTouchee.PlanificationWindows),

            // Le fichier d'échange rendu au disque système (SSD) : effet au redémarrage, fiabilité et fluidité hors jeu ⇒ posé
            // au clic, relu au scan d'après le redémarrage, jamais en A/B d'images.
            new("fixit.pagefile_ssd", CandidateKind.AutoReboot, 1, false, new[] { "ram.commit" }, Family: CandidateFamily.Memory,
                Surface: SurfaceTouchee.Memoire),

            // Le plan « Économie d'énergie » rendu à Équilibré (SetActivePowerPlanOp, plan relu juste avant). Aucun gain
            // d'images publié ⇒ posé au clic et relu, pas en A/B.
            new("fixit.plan_economie", CandidateKind.AutoReglage, 0, false, new[] { "power" }, Family: CandidateFamily.Repair,
                Surface: SurfaceTouchee.PlanificationWindows),

            // Le mode de l'ecran (user32, profil de l'utilisateur, sans droits). Confort : pose et relu, jamais mesure.
            new("fixit.ecran_frequence", CandidateKind.AutoReglage, 1, false, new[] { "display.refresh" },
                Family: CandidateFamily.Repair, Surface: SurfaceTouchee.Affichage),
        }.ToDictionary(d => d.Id, StringComparer.Ordinal);

    /// <summary>Tous les identifiants decrits.</summary>
    public static IEnumerable<string> Ids => Table.Keys;

    /// <summary>Le descripteur d'un levier, avec ce que l'appelant a constate sur la machine.
    /// <c>null</c> = levier inconnu du catalogue : il ne sera pas tente, et il faut le dire.</summary>
    /// <param name="blocageConstate">Ce qui empeche ce levier ici (son `AppliesTo` refuse, une
    /// valeur d'origine illisible…). <c>null</c> = rien ne l'empeche.</param>
    public static CandidateDescriptor? Decrire(string id, string? blocageConstate = null)
        => Table.TryGetValue(id, out var d) ? d with { BlocageConstate = blocageConstate } : null;
}
