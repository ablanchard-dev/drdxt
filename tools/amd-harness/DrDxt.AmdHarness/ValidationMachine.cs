using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.AmdHarness.Reporting;
using DrDxt.App;
using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks.Hardware;
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
/// « --valider » (et « --remettre ») sur la vraie machine. Les leviers sont ceux du produit, construits par
/// la composition de l'App ; le canal élevé est celui du helper, exécuté dans ce processus (le banc tourne déjà en
/// administrateur, son manifeste le demande). Aucun point de restauration : chaque levier est rendu et relu sur place.
/// </summary>
internal static class ValidationMachine
{
    /// <summary>Le journal est rejoué en administrateur ; il vit donc dans un dossier que
    /// seuls les administrateurs et SYSTEM peuvent écrire, pas dans Téléchargements où n'importe quel programme du compte
    /// pourrait y glisser un retour arrière de son choix.</summary>
    public static string CheminDuJournal => Path.Combine(DossierProtege(), "a-remettre.json");

    /// <summary>L'ancien emplacement (zips d'une ancienne version). Jamais rejoué : il est signalé, à vérifier à la main.</summary>
    public static string AncienJournal => Path.Combine(RapportLecture.Dossier(), "DrDXT-a-remettre.json");

    private static readonly SecurityIdentifier Administrateurs = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier Systeme = new(WellKnownSidType.LocalSystemSid, null);

    /// <summary>Un compte sans droits peut créer un dossier dans ProgramData avant le premier lancement,
    /// et il en reste propriétaire (donc maître de ses droits). Le dossier est créé avec les administrateurs pour
    /// propriétaire ; un dossier existant qui est un lien, ou qui appartient à un autre compte, est refusé.</summary>
    private static string DossierProtege()
    {
        var dossier = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDXT-banc");
        var info = new DirectoryInfo(dossier);
        if (!info.Exists)
        {
            var regles = new DirectorySecurity();
            regles.SetOwner(Administrateurs);
            regles.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var compte in new[] { Administrateurs, Systeme })
                regles.AddAccessRule(new FileSystemAccessRule(compte, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            info.Create(regles);
            return dossier;
        }
        var proprietaire = info.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
        if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) || (proprietaire != Administrateurs && proprietaire != Systeme))
            throw new InvalidOperationException($"le dossier {dossier} n'a pas été créé par ce test (lien, ou autre propriétaire) : "
                + "rien n'y est lu ni écrit. Supprime-le, puis relance.");
        return dossier;
    }

    public static ServiceProvider Services()
    {
        var natif = (IAdlxNative?)AdlxReglages.Partage ?? new AdlxNative();
        var executeur = new PrivilegedOpExecutor(
            new Win32RegistryActions(), new Win32PowerPlanActions(), new Win32RestorePointActions(),
            new Win32ServiceActions(), new NvApiDrsActions(), new NvApiTuningActions(),
            // La même instance ADLX que les lecteurs : un seul ADLXInitialize par processus.
            new ADLXTuningActions(natif));
        var collection = new ServiceCollection().AddDrDxtApp();
        collection.AddSingleton<IElevatedClient>(new SansFrequenceNiVentilateur(new InProcessElevatedClient(new OpAllowlist(), executeur)));
        collection.AddSingleton<IAdlxNative>(natif);
        return collection.BuildServiceProvider();
    }

    // Le moteur vit dans le produit (ValidationDeLaMachine), l'App et ce banc l'appellent.
    public static async Task<ValidationComplete.Remise> RemettreAsync(ServiceProvider sp)
    {
        // Le vrai journal d'abord : un vieux fichier dans Téléchargements ne doit pas empêcher de rendre un essai interrompu.
        var remise = await sp.GetRequiredService<ValidationDeLaMachine>().RemettreAsync(new FichierRevertEnAttente(CheminDuJournal)).ConfigureAwait(false);
        if (!File.Exists(AncienJournal)) return remise;
        return new(remise.Texte + Environment.NewLine + $"Un journal d'une ancienne version du test est dans Téléchargements ({Path.GetFileName(AncienJournal)}) : "
            + "il n'est pas rejoué (n'importe quel programme pouvait l'écrire). Vérifie à la main le réglage qu'il cite, puis supprime-le.", false);
    }

    public static Task<ValidationComplete.Resultat> ValiderAsync(ServiceProvider sp, Action<string> ecrireBloc)
    {
        // Comme dans l'App : un jeu lancé pendant le test l'arrête avant le réglage suivant.
        var jeux = sp.GetRequiredService<IGameExecutable>();
        return sp.GetRequiredService<ValidationDeLaMachine>().ValiderAsync(new FichierRevertEnAttente(CheminDuJournal), ecrireBloc,
            () => jeux.RunningGameExecutablePath() is not null);
    }
}

/// <summary>
/// La règle de la carte (jamais tension, fréquence ni ventilateur) tenue par le code de ce banc, pas seulement
/// par la liste de ses leviers : le helper sait ces écritures pour le mode Expert de l'App, le banc les refuse toutes.
/// </summary>
internal sealed class SansFrequenceNiVentilateur(IElevatedClient interieur) : IElevatedClient
{
    public static bool Interdite(PrivilegedOp op) => op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt }
        or ApplyNvApiClockOffsetOp or ApplyGpuFanCurveOp;

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Interdite(op)
            ? Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, $"le test de validation n'écrit jamais fréquence, tension ni ventilateur : {op.GetType().Name} refusée."))
            : interieur.ExecuteAsync(op, ct);

    // Une lecture ne pose rien : elle passe telle quelle (sinon les lecteurs par le helper perdraient leur canal).
    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interieur.ReadAsync(op, ct);
}
