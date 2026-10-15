namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuTuningTarget : les valeurs de réglage GPU qu'un levier doit poser.
//
// Ici, une « sous-tension » est un petit décalage négatif de l'horloge cœur (MHz). Ces
// valeurs restent soumises au bornage et au filtrage de GpuSafeBounds avant d'atteindre le
// helper : la cible est une demande, pas une autorisation.
//
// En production, elles viennent d'un profil de puce ; dans les tests, elles sont injectées
// pour que le levier reste pur et testable.
// -----------------------------------------------------------------------
public sealed record GpuTuningTarget(int GpuIndex, int CoreOffsetMhz, int MemoryOffsetMhz);

/// <summary>La limite de puissance voulue (% du TDP) que doit poser le levier de limite de puissance.</summary>
public sealed record GpuPowerLimitTarget(int GpuIndex, int PowerLimitPct);

/// <summary>La courbe de ventilateur voulue que doit poser le levier de courbe de ventilateur.</summary>
public sealed record GpuFanCurveTarget(int GpuIndex, FanCurve Curve);
