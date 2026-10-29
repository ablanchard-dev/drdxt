using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : la fine frontière D3D11. DeterministicGpuLoadProbe (IGpuLoadProbe, GpuSeams.cs) orchestre la charge, la
// somme de contrôle connue et la décision (score, périphérique perdu, somme de contrôle), testée avec un faux
// IGpuComputeRunner. IGpuComputeRunner, seule surface D3D11 réelle (Vortice, dans Detection, couverte par un smoke test
// réel), lance la charge bornée et rapporte santé, somme de contrôle, travail fait et temps. Même spec ⇒ même somme sur
// un périphérique sain ; seul le score varie, avec l'horloge GPU.
// -----------------------------------------------------------------------

/// <summary>Santé du périphérique observée par le runner après la charge.</summary>
public enum GpuDeviceStatus
{
    /// <summary>Le périphérique a fait tourner la charge et reste sain (aucune raison de retrait).</summary>
    Healthy = 0,
    /// <summary>GetDeviceRemovedReason() a rendu HUNG / REMOVED / RESET (un TDR) →
    /// le décalage est instable.</summary>
    DeviceLost = 1,
    /// <summary>La charge n'a pas pu tourner (création ratée, FL11 absent, lancement en erreur) → CouldNotRun.</summary>
    Error = 2,
}

/// <summary>Une charge de calcul fixe : même spec ⇒ même travail et même somme attendue. <paramref name="Seed" /> est
/// une constante, jamais tirée de l'heure.</summary>
public sealed record GpuWorkloadSpec(int ElementCount, int Iterations, uint Seed);

/// <summary>Ce que le calcul GPU a produit : <see cref="Checksum" /> (64 bits, contre la réponse du CPU),
/// <see cref="IterationsCompleted" /> et <see cref="ElapsedMilliseconds" /> pour le score, <see cref="DeviceStatus" />
/// pour TDR et erreur.</summary>
public sealed record GpuComputeOutcome(
    GpuDeviceStatus DeviceStatus,
    ulong Checksum,
    int IterationsCompleted,
    double ElapsedMilliseconds);

/// <summary>La couture D3D11 réelle. Périphérique absent ou en panne : TWEAK.SDK_UNAVAILABLE ; une perte de périphérique
/// n'est pas une exception mais GpuDeviceStatus.DeviceLost dans un Ok.</summary>
public interface IGpuComputeRunner
{
    /// <summary>Lance <paramref name="spec" /> pendant au plus <paramref name="duration" /> (ou jusqu'à
    /// <paramref name="ct" />), puis rapporte. Ne lève jamais.</summary>
    Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct);
}
