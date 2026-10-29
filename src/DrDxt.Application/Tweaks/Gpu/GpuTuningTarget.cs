namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuTuningTarget : les valeurs de réglage GPU qu'un levier pose (une « sous-tension » = un petit décalage négatif de
// l'horloge cœur, en MHz), venues d'un profil de puce ou des tests. Bornées par GpuSafeBounds avant le helper : une
// demande, pas une autorisation.
// -----------------------------------------------------------------------
public sealed record GpuTuningTarget(int GpuIndex, int CoreOffsetMhz, int MemoryOffsetMhz);

/// <summary>La limite de puissance voulue (% du TDP) que doit poser le levier de limite de puissance.</summary>
public sealed record GpuPowerLimitTarget(int GpuIndex, int PowerLimitPct);

/// <summary>La courbe de ventilateur voulue que doit poser le levier de courbe de ventilateur.</summary>
public sealed record GpuFanCurveTarget(int GpuIndex, FanCurve Curve);
