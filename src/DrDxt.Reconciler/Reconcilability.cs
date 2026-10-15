using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Les réglages que le réconciliateur a le droit de posséder.
//
// Portée de durabilité : seules les surfaces volatiles côté OS sont réconciliées (registre hors
// stratégies, services, plan d'alimentation, tâches planifiées). Les réglages persistés dans le
// firmware (XMP/EXPO, undervolt BIOS) vivent dans le BIOS et sont hors portée : le manifeste ne
// doit jamais les porter.
//
// Les surfaces protégées (liste noire) ne peuvent jamais être touchées, même si un levier finit
// d'une façon ou d'une autre dans le manifeste en les visant (garde stricte).
// -----------------------------------------------------------------------

/// <summary>La surface côté OS qu'écrit un levier réconcilié. Reprend les quatre ops d'écriture
/// qu'expose l'ElevatedHelper (registre / service / plan d'alimentation / tâche planifiée).
/// <see cref="Firmware"/> n'est là que pour que la politique puisse le rejeter : le
/// réconciliateur n'écrit jamais le firmware.</summary>
public enum ReconcileSurface
{
    Registry = 0,
    Service = 1,
    PowerPlan = 2,
    ScheduledTask = 3,

    /// <summary>Persisté dans le firmware (BIOS). Non réconciliable : rejeté à l'enregistrement.</summary>
    Firmware = 99,
}

/// <summary>Décide si un levier peut être enregistré / réconcilié et à quelle surface OS il
/// correspond. La politique par défaut est une table fermée indexée par tweakId ; un tweakId
/// inconnu est traité par prudence (non réconciliable) : un id bidon ne peut jamais être
/// réappliqué en silence.</summary>
public interface IReconcilabilityPolicy
{
    /// <summary>La surface qu'écrit un levier réconciliable connu, ou null si le levier est
    /// inconnu / non réconciliable.</summary>
    ReconcileSurface? SurfaceFor(string tweakId);

    /// <summary>Vrai quand le levier vise une surface protégée (Defender / Windows Update /
    /// BITS…) et ne doit jamais être touché.</summary>
    bool IsProtected(string tweakId);

    /// <summary>Le constructeur GPU auquel ce levier est lié (« NVIDIA » / « AMD »), ou null pour un
    /// levier hors GPU. Permet à la classification de dérive de traiter un changement de
    /// DriverVersion par constructeur comme une cause (non utilisateur) de classe WindowsReset pour
    /// ce levier (I8, T8b/T8c). Un null par défaut garde simples les politiques hors GPU.</summary>
    string? VendorFor(string tweakId) => null;

    /// <summary>Commodité : un levier est réconciliable si et seulement s'il a une surface connue
    /// hors firmware et qu'il n'est pas protégé.</summary>
    bool IsReconcilable(string tweakId)
        => !IsProtected(tweakId)
           && SurfaceFor(tweakId) is { } s
           && s != ReconcileSurface.Firmware;
}
