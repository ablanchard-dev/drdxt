namespace DrDxt.Application.Tweaks.Gpu;

// GpuSafeBounds : le garde-fou devant le helper élevé pour chaque levier GPU (sous-tension, limite de puissance, courbe
// de ventilateur). Rien hors de ces bornes n'atteint le pilote : un levier borne par Clamp* et refuse
// (TWEAK.NOT_APPLICABLE) ce que Is*Safe dit hors plage. Valeurs timides : les 80 % sûrs du gain (plus frais, 1 % lows
// plus réguliers), pas la marge risquée. Pur, entièrement testé.
// Un décalage d'horloge cœur, jamais une tension : négatif, chaque point de tension tourne plus lentement (plus frais,
// moins d'images), pas « les mêmes fréquences à tension plus basse ».
public static class GpuSafeBounds
{
    // --- Décalage d'horloge cœur (MHz). Négatif = sens de la sous-tension. ---
    // Plage timide : une sous-tension modeste est permise ; un petit décalage positif est
    // toléré, mais rien d'agressif dans aucun sens.
    public const int MinCoreOffsetMhz = -200;
    public const int MaxCoreOffsetMhz = 100;

    // --- Décalage d'horloge mémoire (MHz). Prudent de part et d'autre de l'origine. ---
    public const int MinMemoryOffsetMhz = -500;
    public const int MaxMemoryOffsetMhz = 500;

    // --- Limite de puissance (% du TDP). Ne jamais affamer le GPU sous ce plancher. ---
    public const int MinPowerLimitPct = 60;
    public const int MaxPowerLimitPct = 100;

    // --- Courbe de ventilateur. Jamais silencieuse quand le GPU est chaud (anti-surchauffe). ---
    public const int HotTempThresholdC = 80;
    public const int MinFanDutyAtHotTempPct = 40;

    /// <summary>Vrai quand le décalage d'horloge cœur est dans la plage prudente.
    /// Hors plage → le levier doit refuser (TWEAK.NOT_APPLICABLE), jamais l'écrire.</summary>
    public static bool IsCoreOffsetSafe(int offsetMhz) =>
        offsetMhz >= MinCoreOffsetMhz && offsetMhz <= MaxCoreOffsetMhz;

    /// <summary>Vrai quand le décalage d'horloge mémoire est dans la plage prudente.</summary>
    public static bool IsMemoryOffsetSafe(int offsetMhz) =>
        offsetMhz >= MinMemoryOffsetMhz && offsetMhz <= MaxMemoryOffsetMhz;

    /// <summary>Borne un décalage d'horloge cœur demandé à [MinCoreOffsetMhz, MaxCoreOffsetMhz] : l'entrée d'un
    /// paramètre de profil de puce, ramenée à la borne la plus proche.</summary>
    public static int ClampCoreOffsetMhz(int offsetMhz) =>
        Math.Clamp(offsetMhz, MinCoreOffsetMhz, MaxCoreOffsetMhz);

    /// <summary>Borne un décalage d'horloge mémoire demandé à [MinMemoryOffsetMhz, MaxMemoryOffsetMhz].</summary>
    public static int ClampMemoryOffsetMhz(int offsetMhz) =>
        Math.Clamp(offsetMhz, MinMemoryOffsetMhz, MaxMemoryOffsetMhz);

    /// <summary>Borne un pourcentage de limite de puissance demandé à [MinPowerLimitPct, 100].
    /// Sous le plancher, il est remonté au plancher (ne jamais affamer le GPU) ; au-dessus de 100,
    /// il est ramené à 100.</summary>
    public static int ClampPowerLimitPct(int requestedPct) =>
        Math.Clamp(requestedPct, MinPowerLimitPct, MaxPowerLimitPct);

    /// <summary>Vrai quand le pourcentage de limite de puissance est dans [MinPowerLimitPct, 100].</summary>
    public static bool IsPowerLimitSafe(int pct) =>
        pct >= MinPowerLimitPct && pct <= MaxPowerLimitPct;

    /// <summary>Borne une courbe de ventilateur pour que chaque point au seuil de température
    /// chaude ou au-dessus ait au moins MinFanDutyAtHotTempPct de cycle. Les points frais (et
    /// ceux déjà au-dessus du plancher) restent intacts.</summary>
    public static FanCurve ClampFanCurve(FanCurve curve)
    {
        var clamped = curve.Points
            .Select(p => p.TempC >= HotTempThresholdC && p.DutyPct < MinFanDutyAtHotTempPct
                ? p with { DutyPct = MinFanDutyAtHotTempPct }
                : p)
            .ToArray();
        return new FanCurve(clamped);
    }

    /// <summary>Vrai quand aucun point chaud (>= HotTempThresholdC) n'est sous le cycle minimal
    /// sûr : la courbe ne laissera pas le GPU cuire en silence.</summary>
    public static bool IsFanCurveSafe(FanCurve curve) =>
        curve.Points.All(p => p.TempC < HotTempThresholdC || p.DutyPct >= MinFanDutyAtHotTempPct)
        // Le point qui gouverne 80 °C (le dernier à 80 °C ou moins) tient aussi le plancher : sinon (79 °C, 10 %) puis
        // (100 °C, 40 %) tournerait à 10-17 % à 85 °C (le pilote tient ou interpole), et (60 °C, 20 %) seul à 20 % à 90 °C.
        && (curve.Points.Where(p => p.TempC <= HotTempThresholdC).MaxBy(p => p.TempC) is not { } g
            || g.DutyPct >= MinFanDutyAtHotTempPct);
}

/// <summary>Un point d'une courbe de ventilateur GPU : à TempC degrés, tourner à DutyPct % de cycle.</summary>
public sealed record FanCurvePoint(int TempC, int DutyPct);

/// <summary>Une courbe de ventilateur GPU, liste ordonnée de points (température, cycle).</summary>
public sealed record FanCurve(IReadOnlyList<FanCurvePoint> Points)
{
    /// <summary>Le cycle le plus bas parmi les points au seuil de température chaude ou au-dessus
    /// (0 quand la courbe n'a pas de point chaud : rien ne peut surchauffer).</summary>
    public int MinDutyAtHotTemp
    {
        get
        {
            var hot = Points.Where(p => p.TempC >= GpuSafeBounds.HotTempThresholdC).ToArray();
            return hot.Length == 0 ? 0 : hot.Min(p => p.DutyPct);
        }
    }
}
