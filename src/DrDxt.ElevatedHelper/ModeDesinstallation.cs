using System.Globalization;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Journal;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.Adlx;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.Reconciler;
using DrDxt.Reconciler.Desinstallation;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ElevatedHelper;

// -----------------------------------------------------------------------
// ModeDesinstallation : `DrDxt.ElevatedHelper.exe --uninstall-revert`.
//
// Lance par le desinstalleur Inno ([UninstallRun]), deja eleve : aucun tube, aucune UAC, aucun appelant a
// authentifier. Pourquoi le helper et pas l'App : l'App est une application WPF dont le demarrage a des
// effets (rendre au demarrage un levier en essai, verifier les mises a jour en ligne, ouvrir sa fenetre) ;
// le helper, lui, est deja le processus eleve qui sait executer les ops de retour, et il ne reference que
// Core et Reconciler.
//
// Ce que le mode peut faire, et rien d'autre :
//   * Lire le manifeste signe sous %ProgramData%\DrDxt, verifie par la cle publique de la machine (jamais
//     cree de cle) ;
//   * rendre les origines par l'executeur en processus, derriere l'allowlist feuille-exacte ; le redemarrage
//     firmware et l'ecriture du manifeste n'y sont pas branches ;
//   * rendre la carte NVIDIA a l'automatique (ventilateur, decalage du coeur a 0), que les baux du helper tue ne rendent plus ;
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
            Console.Error.WriteLine($"DrDxt.ElevatedHelper {Argument}: not elevated, nothing is read or put back.");
            return 2;
        }

        var racine = ProgramDataManifestStorage.DefaultRoot();
        var quand = DateTimeOffset.UtcNow;
        string texte;
        try
        {
            using var delai = new CancellationTokenSource(Budget);
            // Une seule instance ADLX pour relire et rendre les réglages AMD.
            var adlx = new DrDxt.Detection.Gpu.Adlx.AdlxNative();
            var memeCompte = MemeCompteQueLaSession();
            var lectureWin32 = new LectureDesCiblesWin32(adlx: adlx);
            // Le journal d'écriture anticipée d'abord : ce qu'une App morte a posé sans en noter l'origine n'est pas au
            // manifeste ; il est rendu avant lui.
            var journalJea = JournalDuHelper.Ouvrir(racine, TrustedManifestRoot.EnsureCreatedHardened);
            var client = GarderLaRucheDuJoueur(ClientEnProcessus(adlx, journalJea, lectureWin32.Lecteur), memeCompte);
            var lecture = GarderLaRucheDuJoueur(lectureWin32, memeCompte);
            var travail = Task.Run(async () => await RejouerLeJournalAsync(journalJea, lecture, client, quand, delai.Token)
                + await ExecuterAsync(racine, appliquerAcl: true, lecture,
                client, quand, delai.Token) + await RetirerPlafondEnvoiAsync(client, delai.Token)
                + await RendreLaCarteNvidiaAsync(client, LireLesCartesDeLaMachine(), delai.Token));
            texte = travail.Wait(Budget + TimeSpan.FromSeconds(5))
                ? travail.Result
                : Docteur.T("RD.Titre") + Environment.NewLine + Docteur.T("MD.Delai", Budget.TotalSeconds);
        }
        catch (Exception ex)
        {
            texte = Docteur.T("RD.Titre") + Environment.NewLine + $"exception {ex.GetType().Name}: {ex.Message}";
        }

        Console.Out.WriteLine(texte);
        var journal = EcrireJournal(racine, quand, texte, appliquerAcl: true);
        if (!journal.IsOk)
            Console.Error.WriteLine("report not written: " + journal.Error?.Message);
        return 0;
    }

    /// <summary>Le rejeu du journal, en texte ; vide quand il n'avait rien. Ne jette jamais.</summary>
    internal static async Task<string> RejouerLeJournalAsync(DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee? journal,
        ILectureDesCibles lecture, IElevatedClient ecriture, DateTimeOffset quand, CancellationToken ct)
    {
        try
        {
            return await JournalDuHelper.RejouerAsync(journal, lecture, ecriture, RejeuDuJournal.ProcessusVivant,
                RejeuDuJournal.CompteCourant, ct).ConfigureAwait(false) is { } r && (r.Lignes.Count > 0 || r.ManifesteNonLu is not null)
                ? r.EnTexte(quand)
                : string.Empty;
        }
        catch (Exception ex)
        {
            return $"exception {ex.GetType().Name} : {ex.Message}" + Environment.NewLine;
        }
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

        // Le manifeste est mis de cote une fois lu. S'il restait en place apres la
        // desinstallation, une reinstallation y retrouverait des origines capturees avant, deja rendues, et
        // le jour ou un service les rejouerait il reecrirait un passe que l'utilisateur a quitte. Il est
        // renomme, jamais efface. Non lu ⇒ on n'y touche pas : il n'a pas ete rendu.
        // Une origine non rendue (écriture refusée, cible illisible) reste à rendre : le manifeste garde sa place, une
        // réinstallation puis une désinstallation la retentent.
        var nonRendues = rapport.Lignes.Count(l => l.OrigineEnSuspens);
        if (rapport.ManifesteNonLu is null && nonRendues > 0)
        {
            texte += Docteur.T("MD.ManifesteGarde", nonRendues) + Environment.NewLine;
        }
        else if (rapport.ManifesteNonLu is null)
        {
            var deCote = stockage.MettreDeCoteApresRendu();
            texte += deCote.IsOk
                ? Docteur.T("MD.ManifesteDeCote") + Environment.NewLine
                : Docteur.T("MD.ManifesteNonDeCote", deCote.Error?.Message) + Environment.NewLine;
        }

        return texte;
    }

    /// <summary>Le plafond d'envoi ne passe pas par le manifeste (il n'est pose que pendant
    /// une partie, sous un bail du helper). Un helper tue en pleine partie laisse la politique jusqu'au
    /// redemarrage ; desinstaller avant ce redemarrage laisserait donc l'envoi de tous les autres programmes
    /// plafonne sans plus aucun DrDXT pour le retirer. Retrait inconditionnel et idempotent de nos deux
    /// politiques ; aucune entree ne choisit l'op.</summary>
    internal static async Task<string> RetirerPlafondEnvoiAsync(IElevatedClient ecriture, CancellationToken ct)
    {
        try
        {
            var r = await ecriture.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false);
            return r.IsOk
                ? Docteur.T("MD.PlafondRetire") + Environment.NewLine
                : Docteur.T("MD.PlafondReste", r.Error?.Message) + Environment.NewLine;
        }
        catch (Exception ex)
        {
            return Docteur.T("MD.PlafondReste", ex.GetType().Name) + Environment.NewLine;
        }
    }

    /// <summary>Le ventilateur fixe et le décalage du cœur de l'Expert sont tenus par des baux, en mémoire du helper : le
    /// désinstalleur tue le helper avant ce mode, ses baux ne se rendent plus. Volatils, un redémarrage les efface ; d'ici
    /// là, chaque carte NVIDIA lue est rendue ici, chacune pour ce qui s'y lit (aucune ailleurs).</summary>
    // Rend l'automatique et 0 MHz, pas la valeur d'avant (seul le bail mort la portait) : un décalage ou une courbe de
    // ventilateur d'un autre outil (Afterburner) revient aussi à l'automatique jusqu'à sa relance. Garder l'origine
    // voudrait un bail persistant, refusé (surface).
    internal static async Task<string> RendreLaCarteNvidiaAsync(IElevatedClient ecriture, IReadOnlyList<CarteNvidia> cartes,
        CancellationToken ct)
    {
        var texte = new StringBuilder();
        for (var i = 0; i < cartes.Count; i++)
        {
            var ops = new List<PrivilegedOp>();
            // Un ventilateur qui ne se lit pas n'a pas pu être posé par DrDXT (il relit avant d'écrire) : rien à en dire.
            if (cartes[i].VentilateursLisibles) ops.Add(new RestoreGpuFanAutoOp(i));
            if (cartes[i].DecalageDuCoeur is not (null or 0)) ops.Add(new ApplyNvApiClockOffsetOp(i, 0, 0));
            if (ops.Count == 0) continue;
            var refus = new List<string>();
            foreach (var op in ops)
            {
                try
                {
                    if (await ecriture.ExecuteAsync(op, ct).ConfigureAwait(false) is { IsOk: false } r) refus.Add(r.Error?.Message ?? "");
                }
                catch (Exception ex) { refus.Add(ex.GetType().Name); }
            }
            texte.Append(refus.Count == 0 ? Docteur.T("MD.CarteRendue", i) : Docteur.T("MD.CarteReste", i, string.Join(" ; ", refus)))
                .Append(Environment.NewLine);
        }
        return texte.ToString();
    }

    /// <summary>Ce qui se lit d'une carte NVIDIA : son décalage du cœur (null : illisible), et si ses ventilateurs se lisent.</summary>
    internal sealed record CarteNvidia(int? DecalageDuCoeur, bool VentilateursLisibles);

    /// <summary>Chaque carte se lit seule : un décalage illisible n'empêche pas de rendre son ventilateur. La lecture s'arrête
    /// à la première carte qui ne répond à rien (il n'y en a plus), et à 8 au plus.</summary>
    internal static IReadOnlyList<CarteNvidia> LireLesCartes(Func<int, int?> decalage, Func<int, bool> ventilateursLisibles)
    {
        var cartes = new List<CarteNvidia>();
        while (cartes.Count < 8)
        {
            var carte = new CarteNvidia(decalage(cartes.Count), ventilateursLisibles(cartes.Count));
            if (carte is { DecalageDuCoeur: null, VentilateursLisibles: false }) break;
            cartes.Add(carte);
        }
        return cartes;
    }

    private static IReadOnlyList<CarteNvidia> LireLesCartesDeLaMachine()
    {
        var nvapi = new DrDxt.GpuNative.NvApi.NvApiNative();
        var nvml = new DrDxt.GpuNative.Nvml.NvmlNative();
        return LireLesCartes(i => nvapi.TryReadCoreOffsetMhz(i, out var mhz) ? mhz : null,
            i => nvml.TryLireVentilateurs(i, out var v) && v.Count > 0);
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
    // ne sait que rendre (registre, plan, service, DRS du profil de base).
    // La carte s'écrit aussi, pour rendre une limite de puissance, Anti-Lag, FRTC ou la Vsync AMD gardés ; seules
    // les origines admises par OpAllowlist.ValiderOrigine passent (jamais fréquence ni tension). Hors manifeste, le ventilateur
    // et le décalage du cœur ne reviennent qu'à l'automatique et à 0 (RendreLaCarteNvidiaAsync).
    /// <summary>Élevé avec le compte d'un autre (UAC « par-dessus l'épaule »), HKCU est la ruche de cet autre : les origines
    /// de la session du joueur n'y sont pas écrites. Refusées et dites, elles gardent le manifeste en place (une origine non
    /// rendue ne le laisse pas mettre de côté).</summary>
    internal static IElevatedClient GarderLaRucheDuJoueur(IElevatedClient client, bool memeCompte) =>
        memeCompte ? client : new SansRucheEtrangere(client);

    /// <summary>La lecture aussi : lue dans la ruche d'un autre compte, une origine du joueur passait pour « respectée » ou
    /// « disparue », sans ligne non rendue, et le manifeste partait. Refusée, elle est illisible et le garde.</summary>
    internal static ILectureDesCibles GarderLaRucheDuJoueur(ILectureDesCibles lecture, bool memeCompte) =>
        memeCompte ? lecture : new LectureSansRucheEtrangere(lecture);

    private static bool EstHkcu(PrivilegedOp op) =>
        op is SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } or DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser };

    private sealed class LectureSansRucheEtrangere(ILectureDesCibles lecture) : ILectureDesCibles
    {
        private const string Pourquoi = "elevated with another account: its HKCU is not the player's.";

        public Result<string?> Lire(PrivilegedOp ecriture) =>
            EstHkcu(ecriture) ? Result<string?>.Fail(ErrorCodes.HelperOpFailed, Pourquoi) : lecture.Lire(ecriture);

        public Result<bool> CibleExiste(PrivilegedOp ecriture) =>
            EstHkcu(ecriture) ? Result<bool>.Fail(ErrorCodes.HelperOpFailed, Pourquoi) : lecture.CibleExiste(ecriture);
    }

    private sealed class SansRucheEtrangere(IElevatedClient client) : IElevatedClient
    {
        private static readonly Result Refus = Result.Fail(ErrorCodes.HelperOpFailed,
            "elevated with another account: its HKCU is not the player's; the player's session values are left for "
            + "the player to restore.");

        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            EstHkcu(op) ? Task.FromResult(Refus) : client.ExecuteAsync(op, ct);

        public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => client.ReadAsync(op, ct);
    }

    /// <summary>Le compte du jeton est-il celui de la session interactive ? Illisible = non (HKCU refusé, dit).</summary>
    internal static bool MemeCompteQueLaSession()
    {
        try
        {
            var session = LireSession(WtsUserName);
            var domaine = LireSession(WtsDomainName);
            if (string.IsNullOrEmpty(session)) return false;
            // Par SID, pas par nom : un compte Microsoft ou Azure AD peut s'afficher autrement des deux côtés.
            var sidDeLaSession = new NTAccount(domaine ?? "", session).Translate(typeof(SecurityIdentifier));
            return sidDeLaSession.Equals(WindowsIdentity.GetCurrent().User);
        }
        catch { return false; }
    }

    private const int WtsUserName = 5, WtsDomainName = 7;

    private static string? LireSession(int classe)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, -1, classe, out var tampon, out _)) return null;
        try { return System.Runtime.InteropServices.Marshal.PtrToStringUni(tampon); }
        finally { WTSFreeMemory(tampon); }
    }

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr serveur, int session, int classe, out IntPtr tampon, out int octets);

    [System.Runtime.InteropServices.DllImport("wtsapi32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern void WTSFreeMemory(IntPtr memoire);

    private static IElevatedClient ClientEnProcessus(DrDxt.Detection.Gpu.Adlx.IAdlxNative adlx,
        DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee? journal, Func<PrivilegedOp, Func<Result<string?>>?> lecteur)
        => new InProcessElevatedClient(new OpAllowlist(), JournalDuHelper.Decorer(new PrivilegedOpExecutor(
            new Win32RegistryActions(),
            new Win32PowerPlanActions(),
            new Win32RestorePointActions(),
            new Win32ServiceActions(),
            new NvApiDrsActions(),
            new NvApiTuningActions(),
            new ADLXTuningActions(adlx),
            // La desinstallation doit pouvoir retirer le plafond d'envoi.
            qos: new Win32NetworkQosActions(),
            // La desinstallation doit pouvoir reposer un element BCD que DrDXT a retire.
            bcd: new Win32BcdActions(),
            // Rendre un ventilateur resté en vitesse fixe à l'automatique.
            ventilateur: new NvmlVentilateurActions(),
            // Rendre une limite de puissance du processeur restée posée.
            cpu: new PawnIoCpuPowerActions()), journal, lecteur));
}
