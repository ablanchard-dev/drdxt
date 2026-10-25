using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

public enum StatutDuLevier { Pose, APoser, DejaBon, NonLu, Expert, SansObjet, PasIci }

/// <summary>Un levier du catalogue sur la machine lue. <paramref name="RaisonCle"/> = clé de texte quand il n'est pas pour
/// ce matériel.</summary>
public sealed record LevierSurCeMateriel(string Id, StatutDuLevier Statut, string? RaisonCle = null)
{
    /// <summary>Posé, relu, rendu et relu sur cette machine par « Vérifier DrDXT » (<see cref="LeviersVerifies"/>).</summary>
    public bool VerifieIci { get; init; }
}

/// <summary>
/// « ce que DrDXT fait sur ton PC ». Chaque levier du catalogue a un statut sur la machine lue. Le
/// matériel d'abord : un levier qui ne peut pas exister ici le dit, avec sa raison (le joueur ne lit plus « rien à faire »
/// là où la vérité est « pas sur ton matériel »). Ensuite posé, à poser, non lu, déjà bon.
/// </summary>
public static class CouvertureDesLeviers
{
    public static IReadOnlyList<LevierSurCeMateriel> Calculer(IEnumerable<ITweak> catalogue, HardwareInventory hw,
        SettingsState etat, IReadOnlySet<string> poses, IReadOnlySet<string>? verifies = null)
        => Statuts(catalogue, hw, etat, poses)
            // « vérifié ici » seulement pour un levier lu et proposé ici (posé, à poser, déjà bon).
            .Select(l => l.Statut is not (StatutDuLevier.Pose or StatutDuLevier.APoser or StatutDuLevier.DejaBon)
                         || verifies?.Contains(l.Id) != true
                ? l : l with { VerifieIci = true })
            .ToList();

    // La carte de la machine se redessine plusieurs fois par scan, et un levier peut lire l'écran ou le pilote dans AppliesTo
    // (mesuré le 25/10/2026 sur la machine de référence : 30 à 50 ms par lecture de l'écran de jeu, 60 à 180 ms pour tout le
    // catalogue). Pour un même scan, le même inventaire et le même relevé (par référence), chaque levier se juge une fois par
    // châssis. Un nouveau scan apporte un nouveau relevé, donc un nouveau jugement.
    private sealed class Jugements
    {
        public readonly System.Collections.Concurrent.ConcurrentDictionary<(string Id, ChassisType Chassis), bool> Appliques = new();
        public readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> NonLus = new(StringComparer.Ordinal);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<HardwareInventory,
        System.Runtime.CompilerServices.ConditionalWeakTable<SettingsState, Jugements>> ParScan = new();

    private static bool Applique(ITweak t, HardwareInventory hw, ChassisType chassis, SettingsState etat)
        => ParScan.GetOrCreateValue(hw).GetOrCreateValue(etat).Appliques
            .GetOrAdd((t.Id, chassis), _ => t.AppliesTo(chassis == hw.Chassis ? hw : hw with { Chassis = chassis }, etat));

    private static bool NonLu(ITweak t, HardwareInventory hw, SettingsState etat)
        => ParScan.GetOrCreateValue(hw).GetOrCreateValue(etat).NonLus.GetOrAdd(t.Id, _ => t.EtatNonLu(etat));

    private static IEnumerable<LevierSurCeMateriel> Statuts(IEnumerable<ITweak> catalogue, HardwareInventory hw,
        SettingsState etat, IReadOnlySet<string> poses)
        => catalogue.Select(t =>
            RaisonMateriel(t.Id, hw) is { } raison
                ? new LevierSurCeMateriel(t.Id, SansObjet.Contains(raison) ? StatutDuLevier.SansObjet : StatutDuLevier.PasIci, raison)
            : poses.Contains(t.Id) ? new LevierSurCeMateriel(t.Id, StatutDuLevier.Pose)
            // L'autopilote n'écrit ni fréquence, ni tension, ni ventilateur : ces leviers vivent dans le mode Expert.
            : DrDxt.Core.CandidateSelector.ReglagesCarteInterdits.Contains(t.Id) ? new LevierSurCeMateriel(t.Id, StatutDuLevier.Expert)
            : Applique(t, hw, hw.Chassis, etat) ? new LevierSurCeMateriel(t.Id, StatutDuLevier.APoser)
            : NonLu(t, hw, etat) || TenuParLeChassis(t, hw, etat) ? new LevierSurCeMateriel(t.Id, StatutDuLevier.NonLu)
            : new LevierSurCeMateriel(t.Id, StatutDuLevier.DejaBon));

    /// <summary>Le châssis n'est pas lu (un portable dont le SMBIOS dit « Other », avec sa batterie) et le levier s'appliquerait
    /// sur un PC fixe ou un portable : ce qui le tient hors d'ici, c'est le châssis inconnu, pas un réglage déjà bon.</summary>
    private static bool TenuParLeChassis(ITweak t, HardwareInventory hw, SettingsState etat)
        => hw.Chassis == ChassisType.Unknown
           && (Applique(t, hw, ChassisType.Desktop, etat) || Applique(t, hw, ChassisType.Laptop, etat));

    private static readonly IReadOnlyDictionary<string, string> Domaines = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["latency.reflex"] = "latence",
        ["latency.render_queue_cap"] = "latence",
        ["latency.vrr_vsync_cap"] = "latence",
        ["latency.hagsOn"] = "latence",
        ["latency.gameModeOn"] = "latence",
        ["latency.gameDvrBackgroundOff"] = "latence",
        ["latency.vrrFlipModelOn"] = "latence",
        ["latency.mouse_acceleration_off"] = "latence",
        ["latency.prefer_max_perf"] = "graphique",
        ["gpu.repos_global_normal"] = "graphique",
        ["gpu.undervolt"] = "graphique",
        ["gpu.power_limit"] = "graphique",
        ["gpu.fan_curve"] = "graphique",
        ["fixit.ecran_frequence"] = "graphique",
        ["fixit.x3d_ccd"] = "processeur",
        ["fixit.power_plan"] = "processeur",
        ["fixit.plan_economie"] = "processeur",
        ["fixit.mode_boost"] = "processeur",
        ["fixit.cpu_power_limit"] = "processeur",
        ["fixit.cpu_power_cap"] = "processeur",
        ["fixit.wifi_performances"] = "reseau",
        ["fixit.sane_pagefile"] = "memoire",
        ["fixit.pagefile_ssd"] = "memoire",
        ["fixit.heures_actives"] = "windows",
    };

    /// <summary>La page où le levier se lit ; un levier sans page connue va sur Windows.</summary>
    public static string Domaine(string id) => Domaines.GetValueOrDefault(id, "windows");

    /// <summary>Pour la garde : un levier du catalogue a une page choisie, pas la page par défaut.</summary>
    public static bool DomaineChoisi(string id) => Domaines.ContainsKey(id);

    /// <summary>Les réglages qui ont une page, catalogue ou non : un geste construit hors du catalogue (rendre le repos à
    /// la carte) entre ainsi dans docs/COUVERTURE.md dès qu'il a sa page.</summary>
    public static IEnumerable<string> ReglagesDecrits => Domaines.Keys;

    // Ces raisons ne tiennent pas au matériel ; « pas sur ton matériel » mentait sur un fixe RTX.
    private static readonly HashSet<string> SansObjet = ["Levier.PasIci.ReflexDuJeu"];

    private static string? RaisonMateriel(string id, HardwareInventory hw)
    {
        var console = hw.Chassis == ChassisType.Handheld;
        var portable = hw.Chassis == ChassisType.Laptop;
        var carteDediee = CartesGraphiques.CarteDediee(hw) is not null;
        var nvidia = hw.Gpus.Any(g => g.Vendor.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        var intel = hw.Cpu.Vendor.Contains("Intel", StringComparison.OrdinalIgnoreCase);
        return id switch
        {
            // La limite de puissance du processeur, registres Intel, PC fixe seulement.
            "fixit.cpu_power_limit" when console => "Levier.PasIci.Console",
            "fixit.wifi_performances" when console => "Levier.PasIci.Console",
            "fixit.plan_economie" when console => "Levier.PasIci.Console",
            "fixit.plan_economie" when portable => "Levier.PasIci.Batterie",
            "fixit.cpu_power_limit" when portable => "Levier.PasIci.Batterie",
            "fixit.cpu_power_limit" when !intel => "Levier.PasIci.PasIntel",
            "fixit.cpu_power_cap" when console => "Levier.PasIci.Console",
            "fixit.cpu_power_cap" when portable => "Levier.PasIci.Batterie",
            "fixit.cpu_power_cap" when !intel => "Levier.PasIci.PasIntel",
            // Le ventilateur NVIDIA (NVML) en mode Expert, sur un PC fixe seulement.
            "gpu.fan_curve" when console || portable => "Levier.PasIci.VentilateurConstructeur",
            "gpu.fan_curve" when !nvidia => "Levier.PasIci.PasNvidia",
            "latency.reflex" => "Levier.PasIci.ReflexDuJeu",
            "fixit.x3d_ccd" when !(hw.Cpu.IsX3D && hw.Cpu.CcdCount >= 2) => "Levier.PasIci.PasX3d",
            // Le pilote AMD range le jeu sur le bloc à cache sous Équilibré seulement.
            "fixit.power_plan" when hw.Cpu.IsX3D && hw.Cpu.CcdCount >= 2 => "Levier.PasIci.X3dDeuxBlocs",
            "fixit.power_plan" or "fixit.mode_boost" or "latency.render_queue_cap" or "latency.vrr_vsync_cap"
                or "latency.prefer_max_perf" or "gpu.repos_global_normal" or "gpu.undervolt" or "gpu.power_limit"
                when console => "Levier.PasIci.Console",
            "fixit.power_plan" or "latency.prefer_max_perf" or "gpu.repos_global_normal" when portable => "Levier.PasIci.Batterie",
            "gpu.undervolt" or "gpu.power_limit" when portable => "Levier.PasIci.FrequencesVerrouillees",
            "latency.prefer_max_perf" or "gpu.repos_global_normal" when !nvidia => "Levier.PasIci.PasNvidia",
            "gpu.undervolt" or "gpu.power_limit" or "latency.render_queue_cap" or "latency.vrr_vsync_cap"
                when !carteDediee => "Levier.PasIci.PasDeCarteDediee",
            // Sur AMD, la limite s'écrit seulement sur une architecture validée par un rapport (GpuPowerLimitTweak).
            "gpu.power_limit" when !nvidia && !LimitePuissanceAmd.AmdValidee(hw, GpuTuningCoverage.ValideesEnVie) => "Levier.PasIci.PuissanceAmd",
            _ => null,
        };
    }
}
