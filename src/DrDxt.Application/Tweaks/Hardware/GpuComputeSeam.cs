using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Hardware;

// -----------------------------------------------------------------------
// Banc GPU : la fine frontière D3D11, derrière une couture.
//
// IGpuLoadProbe (GpuSeams.cs) est le contrat que consomme le testeur de stabilité. La
// vraie sonde (DeterministicGpuLoadProbe) n'est qu'orchestration sur cette couture : elle
// possède la charge déterministe + la somme de contrôle de réponse connue + la logique de
// décision score / périphérique perdu / somme de contrôle, et elle est testée en entier
// avec un faux IGpuComputeRunner (sans GPU).
//
// IGpuComputeRunner est la seule surface D3D11 réelle. Son seul rôle : lancer sur le GPU une
// charge de calcul bornée et déterministe et rapporter ce qui s'est passé (santé du
// périphérique + somme de contrôle calculée par le GPU + travail fait + temps écoulé).
// La vraie implémentation Vortice/Direct3D11 vit dans Detection et est couverte par un
// smoke test réel (comme la colonne PresentMon), pas par des tests unitaires.
//
// Contrat de déterminisme : pour un GpuWorkloadSpec donné, la somme de contrôle est identique
// d'une passe à l'autre sur un périphérique sain (aucun aléa tiré de l'heure). Le score de
// débit varie avec l'horloge GPU : c'est tout le but du banc.
// -----------------------------------------------------------------------

/// <summary>Santé du périphérique observée par le runner après la charge.</summary>
public enum GpuDeviceStatus
{
    /// <summary>Le périphérique a fait tourner la charge et reste sain (aucune raison de retrait).</summary>
    Healthy = 0,
    /// <summary>GetDeviceRemovedReason() a rendu HUNG / REMOVED / RESET (un TDR) →
    /// le décalage est instable.</summary>
    DeviceLost = 1,
    /// <summary>La charge n'a pas pu tourner (création du périphérique ratée, FL11 non pris
    /// en charge, lancement en erreur) → la sonde le traite comme CouldNotRun.</summary>
    Error = 2,
}

/// <summary>Une demande de charge de calcul fixe et déterministe. Même spec ⇒ même travail
/// GPU et même somme de contrôle attendue à chaque passe. <paramref name="Seed" /> est une
/// constante fixe (dans la spec, pas tirée de l'heure) : la réponse connue est reproductible.</summary>
public sealed record GpuWorkloadSpec(int ElementCount, int Iterations, uint Seed);

/// <summary>Ce que le calcul GPU a produit. <see cref="Checksum" /> est une réduction
/// déterministe sur 64 bits du tampon de sortie (vérifiée contre la réponse connue du CPU).
/// <see cref="IterationsCompleted" /> et <see cref="ElapsedMilliseconds" /> nourrissent le
/// score de débit. <see cref="DeviceStatus" /> porte l'info TDR / erreur.</summary>
public sealed record GpuComputeOutcome(
    GpuDeviceStatus DeviceStatus,
    ulong Checksum,
    int IterationsCompleted,
    double ElapsedMilliseconds);

/// <summary>La fine couture D3D11 réelle : lancer une charge de calcul déterministe et dire
/// ce qui s'est passé. Rend un Result : un périphérique absent ou en panne échoue proprement
/// (TWEAK.SDK_UNAVAILABLE) ; elle ne doit pas lever sur une perte de périphérique (c'est
/// GpuDeviceStatus.DeviceLost dans un résultat Ok). La doublure des tests unitaires
/// l'implémente ; la vraie implémentation Vortice est la seule frontière D3D.</summary>
public interface IGpuComputeRunner
{
    /// <summary>Lance la charge déterministe décrite par <paramref name="spec" /> pendant au
    /// plus <paramref name="duration" /> (ou jusqu'au déclenchement de <paramref name="ct" />),
    /// puis rapporte le résultat. La vraie implémentation ne lève jamais à travers cette frontière.</summary>
    Result<GpuComputeOutcome> Run(GpuWorkloadSpec spec, TimeSpan duration, CancellationToken ct);
}
