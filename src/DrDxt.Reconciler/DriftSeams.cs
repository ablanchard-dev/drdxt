using DrDxt.Core;

namespace DrDxt.Reconciler;

// -----------------------------------------------------------------------
// Coutures de lecture du réconciliateur : toutes en mode utilisateur, sans admin, sans jamais
// écrire (les écritures passent par IElevatedClient avec des ops typées). Les adaptateurs de
// production les adossent aux lecteurs existants de l'App (IRegistryReader / IPowerPlanReader /
// IServiceStateReader / IHardwareDetector). Les tests unitaires utilisent des faux en mémoire.
// -----------------------------------------------------------------------

/// <summary>Lit la valeur sérialisée actuelle d'un levier réconciliable (même forme JSON que
/// <see cref="AppliedTweak.DesiredValueJson"/>) pour que le réconciliateur compare voulu et
/// réel et confirme qu'une écriture a pris.</summary>
public interface ISettingProbe
{
    /// <summary>Vrai quand cette sonde sait lire la surface du levier donné.</summary>
    bool CanProbe(string tweakId);

    /// <summary>Lit la valeur sérialisée actuelle. Illisible → Fail (aucune supposition).</summary>
    Task<Result<string>> ReadCurrentValueAsync(string tweakId, CancellationToken ct);
}

/// <summary>Lit le signal de mise à jour de fonctionnalité Windows : CurrentBuild + UBR dans
/// HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion. Un changement depuis la valeur capturée à
/// l'apply est le signal WindowsReset principal et fiable.</summary>
public interface IOsBuildProbe
{
    /// <summary>Rend un jeton stable comme « 22631.3447 » (CurrentBuild.UBR).
    /// Illisible → Fail.</summary>
    Result<string> ReadCurrentBuild();
}

/// <summary>Lit la version du pilote installé pour un constructeur GPU (NVIDIA / AMD), depuis
/// Win32_VideoController / le registre du constructeur. Un changement depuis l'apply est le
/// signal de dérive par mise à jour du pilote, par constructeur (I8 / plan T8b/T8c). Le
/// constructeur se compare à GpuInfo.Vendor sans tenir compte de la casse.</summary>
public interface IGpuDriverProbe
{
    /// <summary>Le DriverVersion actuel de l'adaptateur dont le constructeur correspond à
    /// <paramref name="vendor"/>, ou Ok(null) si aucun adaptateur de ce type n'est présent.
    /// Illisible → Fail.</summary>
    Result<string?> ReadDriverVersion(string vendor);
}

/// <summary>Horloge UTC injectable : le réconciliateur calcule les fenêtres de série de
/// démarrages et les horodatages sur elle, jamais directement sur DateTimeOffset.UtcNow, pour
/// que les tests mènent le temps de façon déterministe (même convention que l'IClock du helper).</summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Horloge de production : la vraie horloge murale UTC.</summary>
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
