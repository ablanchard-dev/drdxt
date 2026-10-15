namespace DrDxt.Application.Tweaks.Latency;

// -----------------------------------------------------------------------
// DirectXUserGlobalSettings : lit et fusionne la chaîne REG_SZ de Windows sous
// HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings.
//
// Le format est une liste plate « clé=valeur;clé=valeur;... » (par exemple
// "HighPerfAdapter=10DE&2684&...;VRROptimizeEnable=1;AutoHDREnable=1;SwapEffectUpgradeEnable=1").
// Le VRR (VRROptimizeEnable) et le flip-model / les optimisations pour les jeux en fenêtré
// (SwapEffectUpgradeEnable) partagent cette chaîne avec AutoHDREnable et d'autres : un levier
// doit fusionner, poser seulement ses clés et garder toutes les autres (jamais écraser AutoHDR).
//
// Pur, sans E/S. Le premier '=' sépare la clé de la valeur (une valeur peut contenir '=' / '&').
// -----------------------------------------------------------------------
internal static class DirectXUserGlobalSettings
{
    /// <summary>Rend <paramref name="original"/> avec chaque (clé, valeur) de
    /// <paramref name="overrides"/> appliquée : une clé existante est mise à jour sur place (casse
    /// d'origine gardée), une clé nouvelle est ajoutée à la fin. Chaque clé non touchée est gardée
    /// telle quelle. Un original null ou vide donne seulement les remplacements. Sortie
    /// « clé=valeur;clé=valeur » (pas de ';' final ; Windows coupe sur ';' et ignore les segments vides).</summary>
    public static string Merge(string? original, IReadOnlyList<(string Key, string Value)> overrides)
    {
        var pairs = Parse(original);
        foreach (var (key, value) in overrides)
        {
            var idx = pairs.FindIndex(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                pairs[idx] = (pairs[idx].Key, value); // garde la casse d'origine de la clé
            else
                pairs.Add((key, value));
        }
        return string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"));
    }

    /// <summary>Vrai quand chaque (clé, valeur) est déjà présente dans <paramref name="current"/>
    /// (clé comparée sans tenir compte de la casse, valeur exactement) : la fusion ne changerait rien.</summary>
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
                continue; // sauter un segment malformé (sans clé) plutôt que d'abîmer le résultat
            result.Add((segment[..eq], segment[(eq + 1)..]));
        }
        return result;
    }
}
