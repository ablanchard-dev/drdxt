using DrDxt.AmdHarness.Reporting;
using DrDxt.App;
using DrDxt.Application.Autopilot;
using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.AmdHarness;

/// <summary>
/// 07/10/2026 : « --valider » (et « --remettre ») sur la vraie machine. Les leviers sont ceux du produit, construits par
/// la composition de l'App ; le canal élevé est celui du helper, exécuté dans ce processus (le banc tourne déjà en
/// administrateur, son manifeste le demande). Aucun point de restauration : chaque levier est rendu et relu sur place.
/// </summary>
internal static class ValidationMachine
{
    public static string CheminDuJournal => Path.Combine(RapportLecture.Dossier(), "DrDXT-a-remettre.json");

    public static ServiceProvider Services()
    {
        var natif = (IAdlxNative?)AdlxReglages.Partage ?? new AdlxNative();
        var executeur = new PrivilegedOpExecutor(
            new Win32RegistryActions(), new Win32PowerPlanActions(), new Win32RestorePointActions(),
            new Win32ServiceActions(), new NvApiDrsActions(), new NvApiTuningActions(),
            // La même instance ADLX que les lecteurs : un seul ADLXInitialize par processus.
            new ADLXTuningActions(natif));
        var collection = new ServiceCollection().AddDrDxtApp();
        collection.AddSingleton<IElevatedClient>(new InProcessElevatedClient(new OpAllowlist(), executeur));
        collection.AddSingleton<IAdlxNative>(natif);
        return collection.BuildServiceProvider();
    }

    // 08/10/2026 (T1b) : le moteur vit dans le produit (ValidationDeLaMachine), l'App et ce banc l'appellent.
    public static Task<ValidationComplete.Remise> RemettreAsync(ServiceProvider sp)
        => sp.GetRequiredService<ValidationDeLaMachine>().RemettreAsync(new FichierRevertEnAttente(CheminDuJournal));

    public static Task<ValidationComplete.Resultat> ValiderAsync(ServiceProvider sp, Action<string> ecrireBloc)
        => sp.GetRequiredService<ValidationDeLaMachine>().ValiderAsync(new FichierRevertEnAttente(CheminDuJournal), ecrireBloc);
}
