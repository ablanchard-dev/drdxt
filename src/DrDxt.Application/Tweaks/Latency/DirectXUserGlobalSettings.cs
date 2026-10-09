namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// DirectXUserGlobalSettings — parse/merge the Windows REG_SZ string at
// HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings.
//
// Format is a flat "key=value;key=value;..." list (e.g.
// "HighPerfAdapter=10DE&2684&...;VRROptimizeEnable=1;AutoHDREnable=1;SwapEffectUpgradeEnable=1").
// VRR (VRROptimizeEnable) and flip-model / optimizations-for-windowed-games
// (SwapEffectUpgradeEnable) share this string with AutoHDREnable and others, so a tweak must
// MERGE — set only the keys it owns and PRESERVE every other (audit M2: never clobber AutoHDR).
//
// Pure, no I/O. The first '=' separates key from value (a value may itself contain '=' / '&').
// -----------------------------------------------------------------------
internal static class DirectXUserGlobalSettings
{
    /// <summary>Returns <paramref name="original"/> with each (key, value) in
    /// <paramref name="overrides"/> applied: an existing key is updated in place (original casing
    /// kept), a new key is appended. Every untouched key is preserved verbatim. A null/empty
    /// original yields just the overrides. Output is "key=value;key=value" (no trailing ';';
    /// Windows splits on ';' and ignores empty segments either way).</summary>
    public static string Merge(string? original, IReadOnlyList<(string Key, string Value)> overrides)
    {
        var pairs = Parse(original);
        foreach (var (key, value) in overrides)
        {
            var idx = pairs.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                pairs[idx] = (pairs[idx].Key, value); // keep the original key casing
            else
                pairs.Add((key, value));
        }
        return string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>True when every (key, value) is already present in <paramref name="current"/>
    /// (key compared case-insensitively, value exactly) — the merge would change nothing.</summary>
    public static bool AlreadyHas(string? current, IReadOnlyList<(string Key, string Value)> wanted)
    {
        var pairs = Parse(current);
        return wanted.All(w => pairs.Any(p =>
            string.Equals(p.Key, w.Key, StringComparison.OrdinalIgnoreCase) && p.Value == w.Value));
    }

    private static List<(string Key, string Value)> Parse(string? s)
    {
        var result = new List<(string, string)>();
        if (string.IsNullOrWhiteSpace(s))
            return result;
        foreach (var segment in s.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = segment.IndexOf('=');
            if (eq <= 0)
                continue; // skip a malformed segment (no key) rather than corrupt the result
            result.Add((segment[..eq], segment[(eq + 1)..]));
        }
        return result;
    }
}
