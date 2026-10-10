namespace DrDxt.App.ViewModels;

/// <summary>
/// Maps an engine tweak id to its display resx keys (title + honest summary) so the
/// dashboard change rows read as friendly names, not raw ids. The summary keys are the
/// Same ones the tweaks (D/E/F) declare via <c>ITweak.SummaryKey</c> — kept in sync by
/// the localization parity test. An unknown id falls back to the id itself for the title
/// and an empty summary, so nothing ever crashes and nothing is invented.
/// </summary>
public static class TweakDisplayKeys
{
    private static readonly IReadOnlyDictionary<string, (string TitleKey, string SummaryKey)> Map =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["latency.reflex"]          = ("tweak.latency.reflex.title",          "tweak.latency.reflex.driver_floor"),
            ["latency.render_queue_cap"] = ("tweak.latency.render_queue_cap.title", "tweak.latency.render_queue_cap.floor"),
            ["latency.vrr_vsync_cap"]  = ("tweak.latency.vrr_vsync_cap.title",   "tweak.latency.vrr_vsync_cap.combo"),
            ["gpu.undervolt"]          = ("tweak.gpu.undervolt.title",           "tweak.gpu.undervolt.efficiency"),
            ["gpu.power_limit"]        = ("tweak.gpu.power_limit.title",         "tweak.gpu.power_limit.efficiency"),
            ["gpu.fan_curve"]          = ("tweak.gpu.fan_curve.title",           "tweak.gpu.fan_curve.cooler"),
            ["fixit.x3d_ccd"]          = ("tweak.fixit.x3d_ccd.title",           "tweak.fixit.x3d_ccd.repair_routing"),
            ["fixit.power_plan"]       = ("tweak.fixit.power_plan.title",        "tweak.cpu.power_policy.raise_perf_preference"),
            // 14/09/2026 (plan 2.4) : l'ecran de resultat nomme aussi ces leviers ; le resume = leur SummaryKey.
            ["latency.prefer_max_perf"] = ("tweak.latency.prefer_max_perf.title", "tweak.latency.prefer_max_perf.enable"),
            ["latency.hagsOn"]          = ("tweak.latency.hagsOn.title",          "tweak.latency.hags.enable"),
            ["latency.gameModeOn"]      = ("tweak.latency.gameModeOn.title",      "tweak.latency.game_mode.enable"),
            ["latency.gameDvrBackgroundOff"] = ("tweak.latency.gameDvrBackgroundOff.title", "tweak.latency.game_dvr.background_off"),
            ["latency.vrrFlipModelOn"]  = ("tweak.latency.vrrFlipModelOn.title",  "tweak.latency.vrr_flipmodel.enable"),
            ["latency.mouse_acceleration_off"] = ("tweak.latency.mouse_acceleration_off.title", "tweak.latency.mouse_acceleration.off"),
            ["fixit.sane_pagefile"]     = ("tweak.fixit.sane_pagefile.title",     "tweak.fixit.sane_pagefile.reliability"),
            ["fixit.heures_actives"]    = ("tweak.fixit.heures_actives.title",    "tweak.fixit.heures_actives.summary"),
            ["fixit.mode_boost"]        = ("tweak.fixit.mode_boost.title",        "tweak.fixit.mode_boost.summary"),
            ["fixit.cpu_power_limit"]         = ("tweak.fixit.cpu_power_limit.title",         "tweak.fixit.cpu_power_limit.summary"),
            ["fixit.ecran_frequence"]   = ("tweak.fixit.ecran_frequence.title",   "tweak.fixit.ecran_frequence.confort"),
            ["gpu.repos_global_normal"] = ("tweak.gpu.repos_global_normal.title", "tweak.gpu.repos_global_normal"),
            // Levier de l'outil, sans ITweak dans l'App : pas de resume a lui.
            ["gpu.core_offset"]         = ("tweak.gpu.core_offset.title",         ""),
        };

    /// <summary>Title resx key for a tweak id, or the id itself when unknown.</summary>
    public static string TitleKey(string tweakId) =>
        Map.TryGetValue(tweakId, out var keys) ? keys.TitleKey : tweakId;

    /// <summary>Summary resx key for a tweak id, or empty when unknown.</summary>
    public static string SummaryKey(string tweakId) =>
        Map.TryGetValue(tweakId, out var keys) ? keys.SummaryKey : string.Empty;
}
