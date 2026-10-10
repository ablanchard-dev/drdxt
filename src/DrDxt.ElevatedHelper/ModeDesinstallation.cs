using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// ModeDesinstallation — `DrDxt.ElevatedHelper.exe --uninstall-revert` (11/09/2026).
//
// Lance par le desinstalleur Inno ([UninstallRun]), deja eleve : aucun tube, aucune UAC, aucun appelant a
// authentifier. Pourquoi le helper et pas l'App : l'App est une application WPF dont le demarrage a des
// effets (rendre au demarrage un levier en essai, verifier les mises a jour en ligne, ouvrir sa fenetre) ;
// le helper, lui, est deja le processus eleve qui sait executer les ops de retour, et il ne reference que
// Core et Reconciler.
//
// Ce que le mode peut faire, et rien d'autre :
//   * Lire le manifeste signe sous %ProgramData%\DrDxt, verifie par la cle PUBLIQUE de la machine (jamais
//     cree de cle) ;
//   * rendre les origines par l'executeur EN PROCESSUS, derriere l'allowlist feuille-exacte ; les ecritures
//     GPU (horloge, puissance, ADLX), le redemarrage firmware et l'ecriture du manifeste n'y sont pas
//     branches ;
//   * ecrire son rapport `desinstallation-<heure UTC>.txt` dans la meme racine durcie (lisible apres coup).
// Aucun argument ne choisit un chemin ou une op : la seule entree est le manifeste signe.
//
// Non bloquant : borne a 120 s, rend toujours 0 (Inno ne lit pas le code de retour), et tout echec finit
// dans le rapport.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class ModeDesinstallation
{
    public const string Argument = "--uninstall-revert";

    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(120);

    internal static int Executer()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Console.Error.WriteLine($"DrDxt.ElevatedHelper {Argument} : processus non eleve, rien n'est lu ni rendu.");
            return 2;
        }

        var racine = ProgramDataManifestStorage.DefaultRoot();
        var quand = DateTimeOffset.UtcNow;
        string texte;
        try
        {
            using var delai = new CancellationTokenSource(Budget);
            // 08/10/2026 : une seule instance ADLX pour relire et rendre les réglages AMD.
            var adlx = new DrDxt.Detection.Gpu.Adlx.AdlxNative();
            var client = ClientEnProcessus(adlx);
            var travail = Task.Run(async () => await ExecuterAsync(racine, appliquerAcl: true, new LectureDesCiblesWin32(adlx: adlx),
                client, quand, delai.Token) + await RetirerPlafondEnvoiAsync(client, delai.Token));
            texte = travail.Wait(Budget + TimeSpan.FromSeconds(5))
                ? travail.Result
                : $"DrDXT - rendu des réglages à la désinstallation{Environment.NewLine}"
                  + $"délai de {Budget.TotalSeconds:0} s dépassé : le rendu a été abandonné ; la désinstallation continue.";
        }
        catch (Exception ex)
        {
            texte = $"DrDXT - rendu des réglages à la désinstallation{Environment.NewLine}exception {ex.GetType().Name} : {ex.Message}";
        }

        Console.Out.WriteLine(texte);
        var journal = EcrireJournal(racine, quand, texte, appliquerAcl: true);
        if (!journal.IsOk)
            Console.Error.WriteLine("rapport non écrit : " + journal.Error?.Message);
        return 0;
    }

    /// <summary>Compose la lecture verify-only et le rendu ; rend le texte du rapport. Ne jette jamais.</summary>
    internal static async Task<string> ExecuterAsync(string racine, bool appliquerAcl, ILectureDesCibles lecture,
        IElevatedClient ecriture, DateTimeOffset quand, CancellationToken ct)
    {
        RapportDuRendu rapport;
        var stockage = new ProgramDataManifestStorage(racine, appliquerAcl);
        try
        {
            var verificateur = MachineManifestKey.ChargerVerificateur(racine, appliquerAcl);
            rapport = !verificateur.IsOk
                ? new RapportDuRendu($"{verificateur.Error!.Value.Code} : {verificateur.Error.Value.Message}",
                    Array.Empty<LigneDuRendu>())
                : await new RenduALaDesinstallation(
                        new SignedDesiredStateStore(stockage, signer: null,
                            verificateur.Value!, new DefaultReconcilabilityPolicy(), new DrDxt.Reconciler.SystemClock()),
                        lecture, ecriture)
                    .ExecuterAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            rapport = new RapportDuRendu($"exception {ex.GetType().Name} : {ex.Message}", Array.Empty<LigneDuRendu>());
        }

        var texte = rapport.EnTexte(quand);

        // 11/09/2026 (lot 6e) — le manifeste est mis de cote une fois lu. Il restait en place apres la
        // desinstallation : une REINSTALLATION y retrouvait des origines capturees avant, deja rendues, et
        // le jour ou un service les rejouerait il reecrirait un passe que l'utilisateur a quitte. Il est
        // renomme, jamais efface. Non lu ⇒ on n'y touche pas : il n'a pas ete rendu.
        if (rapport.ManifesteNonLu is null)
        {
            var deCote = stockage.MettreDeCoteApresRendu();
            texte += deCote.IsOk
                ? "manifeste signé : mis de côté (renommé) ; une réinstallation repart d'un manifeste neuf."
                  + Environment.NewLine
                : "manifeste signé : pas mis de côté (" + deCote.Error?.Message + ") ; une réinstallation le "
                  + "retrouverait." + Environment.NewLine;
        }

        return texte;
    }

    /// <summary>26/09/2026 : le plafond d'envoi ne passe pas par le manifeste (il n'est pose que pendant
    /// une partie, sous un bail du helper). Un helper tue en pleine partie laisse la politique jusqu'au
    /// redemarrage ; desinstaller avant ce redemarrage laissait donc l'envoi de tous les autres programmes
    /// plafonne sans plus aucun DrDXT pour le retirer. Retrait inconditionnel et idempotent de NOS deux
    /// politiques ; aucune entree ne choisit l'op.</summary>
    internal static async Task<string> RetirerPlafondEnvoiAsync(IElevatedClient ecriture, CancellationToken ct)
    {
        try
        {
            var r = await ecriture.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false);
            return r.IsOk
                ? "plafond d'envoi : aucun ne reste actif." + Environment.NewLine
                : "plafond d'envoi : pas retiré (" + r.Error?.Message + ") ; un redémarrage l'efface." + Environment.NewLine;
        }
        catch (Exception ex)
        {
            return "plafond d'envoi : pas retiré (" + ex.GetType().Name + ") ; un redémarrage l'efface." + Environment.NewLine;
        }
    }

    /// <summary>Le rapport, a cote du manifeste, dans la racine durcie : il survit a la desinstallation.</summary>
    internal static Result EcrireJournal(string racine, DateTimeOffset quand, string texte, bool appliquerAcl)
    {
        var chemin = Path.Combine(racine,
            "desinstallation-" + quand.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".txt");
        // BOM UTF-8 : le Bloc-notes l'ouvre sans deviner l'encodage.
        var octets = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(texte)).ToArray();
        if (appliquerAcl)
            return TrustedManifestRoot.WriteTrustedFile(chemin, octets);

        try
        {
            Directory.CreateDirectory(racine);
            File.WriteAllBytes(chemin, octets);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, $"Could not write '{chemin}': {ex.Message}");
        }
    }

    // Les seams GPU de reglage fin sont desactives, le firmware et l'ecrivain du manifeste absents : ce mode
    // ne sait que RENDRE (registre, plan, service, DRS du profil de base).
    // 08/10/2026 : la carte s'écrit aussi, pour RENDRE une limite de puissance, Anti-Lag, FRTC ou la Vsync AMD gardés ; seules
    // les origines admises par OpAllowlist.ValiderOrigine passent (jamais fréquence, tension ni ventilateur).
    private static IElevatedClient ClientEnProcessus(DrDxt.Detection.Gpu.Adlx.IAdlxNative adlx) => new InProcessElevatedClient(new OpAllowlist(),
        new PrivilegedOpExecutor(
            new Win32RegistryActions(),
            new Win32PowerPlanActions(),
            new Win32RestorePointActions(),
            new Win32ServiceActions(),
            new NvApiDrsActions(),
            new NvApiTuningActions(),
            new ADLXTuningActions(adlx),
            // 24/09/2026 — la desinstallation doit pouvoir RETIRER le plafond d'envoi.
            qos: new Win32NetworkQosActions(),
            // La desinstallation doit pouvoir REPOSER un element BCD que DrDXT a retire.
            bcd: new Win32BcdActions(),
            // 08/10/2026 (GPU-OC) : rendre un ventilateur resté en vitesse fixe à l'automatique.
            ventilateur: new NvmlVentilateurActions(),
            // 10/10/2026 (B1) : rendre une limite de puissance du processeur restée posée.
            cpu: new PawnIoCpuPowerActions()));
}
