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

    /// <summary>Ce que le banc a le droit d'écrire, selon le geste : <see cref="Geste.Valider"/> jamais de fréquence ni de
    /// ventilateur ; <see cref="Geste.Frequence"/> la fréquence maximale AMD seule ; <see cref="Geste.Remettre"/> ce que
    /// les deux ont pu laisser.</summary>
    public enum Geste { Valider, Frequence, Remettre }

    public static ServiceProvider Services(Geste geste = Geste.Valider)
    {
        var natif = (IAdlxNative?)AdlxReglages.Partage ?? new AdlxNative();
        var executeur = new PrivilegedOpExecutor(
            new Win32RegistryActions(), new Win32PowerPlanActions(), new Win32RestorePointActions(),
            new Win32ServiceActions(), new NvApiDrsActions(), new NvApiTuningActions(),
            // La même instance ADLX que les lecteurs : un seul ADLXInitialize par processus. La même origine du démarrage
            // que le helper du produit, donc la même bande.
            new ADLXTuningActions(natif, OrigineRetenue));
        var collection = new ServiceCollection().AddDrDxtApp();
        ServiceProvider? fournisseur = null;
        int? MaxFreq(int gpu) => fournisseur?.GetRequiredService<IAdlxGpuTuning>().GetGfxMaxFreqMhz(gpu) is { IsOk: true } r ? r.Value : null;
        collection.AddSingleton(ClientDuGeste(geste, new InProcessElevatedClient(new OpAllowlist(), executeur), MaxFreq, OrigineRetenue));
        collection.AddSingleton<IAdlxNative>(natif);
        fournisseur = collection.BuildServiceProvider();
        return fournisseur;
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

    /// <summary>Le canal élevé du geste, filtré par ce que ce geste a le droit d'écrire.</summary>
    /// <param name="retenir">La fréquence retenue au premier passage depuis le démarrage (reçoit l'index et la valeur lue) :
    /// la même mémoire que le helper, pour que la borne du banc soit la sienne et ne suive jamais une fréquence déjà montée.</param>
    internal static IElevatedClient ClientDuGeste(Geste geste, IElevatedClient interieur, Func<int, int?> maxFreqLue, Func<int, int, int?> retenir)
    {
        int? Origine(int gpu) => maxFreqLue(gpu) is { } lue ? retenir(gpu, lue) : null;
        return geste switch
        {
            Geste.Frequence => new SeulementLaFrequenceAmd(interieur, Origine),
            Geste.Remettre => new RemiseDuBanc(interieur, Origine),
            _ => new SansFrequenceNiVentilateur(interieur),
        };
    }

    /// <summary>La mémoire du helper : clé volatile de HKLM, effacée au redémarrage (<see cref="OrigineDuDemarrage"/>).</summary>
    private static int? OrigineRetenue(int gpu, int lue)
        => OrigineDuDemarrage.Retenir("AdlxMaxFreq" + gpu, unchecked((ulong)lue)) is { } o ? unchecked((int)o) : null;

    /// <summary>« --valider-frequence » : la fréquence maximale d'une carte AMD seule, baissée puis rendue.</summary>
    public static Task<ValidationComplete.Resultat> ValiderFrequenceAsync(ServiceProvider sp, Action<string> ecrireBloc)
    {
        var jeux = sp.GetRequiredService<IGameExecutable>();
        return sp.GetRequiredService<ValidationDeLaMachine>().ValiderFrequenceAsync(new FichierRevertEnAttente(CheminDuJournal), ecrireBloc,
            () => jeux.RunningGameExecutablePath() is not null);
    }

    /// <param name="profond">« --valider-profond » : un réglage déjà en place qui sait s'exercer est mis hors de sa cible,
    /// posé, puis rendu à son origine exacte ; même journal, donc REMETTRE.cmd le couvre.</param>
    public static Task<ValidationComplete.Resultat> ValiderAsync(ServiceProvider sp, Action<string> ecrireBloc, bool profond = false)
    {
        // Comme dans l'App : un jeu lancé pendant le test l'arrête avant le réglage suivant.
        var jeux = sp.GetRequiredService<IGameExecutable>();
        return sp.GetRequiredService<ValidationDeLaMachine>().ValiderAsync(new FichierRevertEnAttente(CheminDuJournal), ecrireBloc,
            () => jeux.RunningGameExecutablePath() is not null, profond: profond);
    }
}

/// <summary>
/// La règle de la carte (jamais tension, fréquence ni ventilateur) tenue par le code de ce banc, pas seulement
/// par la liste de ses leviers : le helper sait ces écritures pour le mode Expert de l'App, le banc les refuse toutes.
/// </summary>
internal sealed class SansFrequenceNiVentilateur(IElevatedClient interieur) : IElevatedClient
{
    // La remise à l'usine de la carte est un geste du joueur seulement : le banc ne la fait jamais.
    public static bool Interdite(PrivilegedOp op) => op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt or AdlxTuningKind.RemiseUsine }
        or ApplyNvApiClockOffsetOp or ApplyGpuFanCurveOp;

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Interdite(op)
            ? Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, $"le test de validation n'écrit jamais fréquence, tension ni ventilateur : {op.GetType().Name} refusée."))
            : interieur.ExecuteAsync(op, ct);

    // Une lecture ne pose rien : elle passe telle quelle (sinon les lecteurs par le helper perdraient leur canal).
    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interieur.ReadAsync(op, ct);
}

/// <summary>
/// « --valider-frequence » n'écrit que la fréquence maximale d'une carte AMD (ADLX), rien d'autre : ni tension, ni
/// ventilateur, ni décalage NVIDIA, ni aucun autre réglage. Et jamais plus de 100 MHz au-dessus de la fréquence retenue au
/// démarrage : au-delà, le helper refuse lui-même (GpuRemiseUsineRequise, la remise à l'usine n'étant qu'un geste du joueur),
/// et le banc refuse avant lui ; une borne qui suivrait la fréquence lue laisserait un journal rejoué monter par paliers.
/// Refusé, l'essai se dit à vérifier.
/// </summary>
internal sealed class SeulementLaFrequenceAmd(IElevatedClient interieur, Func<int, int?> origine) : IElevatedClient
{
    public const int MonteeMaxMhz = 100;

    public static bool Permise(PrivilegedOp op, Func<int, int?> origine)
        => op is ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt } a && DansLaBande(a, origine);

    internal static bool DansLaBande(ApplyAdlxTuningOp a, Func<int, int?> origine)
        => origine(a.GpuIndex) is { } o && (long)a.IntValue <= (long)o + MonteeMaxMhz;

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Permise(op, origine)
            ? interieur.ExecuteAsync(op, ct)
            : Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, $"le test de fréquence n'écrit que la fréquence maximale AMD, dans sa bande : {op.GetType().Name} refusée."));

    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interieur.ReadAsync(op, ct);
}

/// <summary>
/// La remise d'un essai interrompu : ce que « --valider » pose, plus la fréquence maximale AMD que « --valider-frequence »
/// a pu laisser baissée, dans la même bande. Jamais tension, ventilateur ni décalage NVIDIA.
/// </summary>
internal sealed class RemiseDuBanc(IElevatedClient interieur, Func<int, int?> origine) : IElevatedClient
{
    public static bool Permise(PrivilegedOp op, Func<int, int?> origine) => op switch
    {
        ApplyAdlxTuningOp { Kind: AdlxTuningKind.Undervolt } a => SeulementLaFrequenceAmd.DansLaBande(a, origine),
        _ => !SansFrequenceNiVentilateur.Interdite(op),
    };

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct)
        => Permise(op, origine)
            ? interieur.ExecuteAsync(op, ct)
            : Task.FromResult(Result.Fail(ErrorCodes.HelperOpFailed, $"la remise du test ne rend que ce qu'il a posé : {op.GetType().Name} refusée."));

    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => interieur.ReadAsync(op, ct);
}
