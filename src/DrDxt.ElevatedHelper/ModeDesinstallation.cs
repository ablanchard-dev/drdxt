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
//   * reactiver les programmes que DrDXT a coupes au demarrage, d'apres leurs traces, s'ils portent encore ses octets ;
//   * dire quels services gardent les droits que DrDXT a corriges : une correction de securite, laissee en place ;
//   * ecrire son rapport `desinstallation-<heure UTC>.txt` dans la meme racine durcie (lisible apres coup), et la marque
//     `rendu-incomplet` quand quelque chose reste a rendre : l'installeur la lit pour le dire au joueur.
// Aucun argument ne choisit un chemin ou une op : les seules entrees sont le manifeste signe et les traces des coupures
// (celle de la machine relue de confiance ; celle du compte ne fait que reactiver un programme de son HKCU, ce que le
// compte peut deja faire lui-meme). Rien ne s'ecrit dans un dossier du compte : en administrateur, une ecriture y suivrait
// une jonction posee par le compte vers la racine de confiance (TraceDuRendu).
//
// Non bloquant : borne a 120 s, rend 0 (Inno ne lit pas le code de retour) et 2 seulement s'il n'est pas eleve ;
// tout echec finit dans le rapport.
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
        // Hors délai ou en panne, rien ne dit que tout a été rendu : la marque se pose.
        var reste = new ResteARendre();
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
            var travail = Task.Run(async () => await RejouerLeJournalAsync(journalJea, lecture, client, quand, delai.Token, reste)
                + await ExecuterAsync(racine, appliquerAcl: true, lecture,
                client, quand, delai.Token, reste) + await RetirerPlafondEnvoiAsync(client, delai.Token, reste)
                + await RendreLaCarteNvidiaAsync(client, LireLesCartesDeLaMachine(), delai.Token, reste)
                + await ReactiverLesCoupuresAsync(CoupuresARendre(memeCompte), Win32RegistryActions.LireApprobation, client, delai.Token, reste)
                + (memeCompte ? "" : Docteur.T("MD.DemarrageAutreCompte") + Environment.NewLine)
                + DroitsQuiRestent(racine));
            if (travail.Wait(Budget + TimeSpan.FromSeconds(5)))
                texte = travail.Result;
            else
            {
                texte = Docteur.T("RD.Titre") + Environment.NewLine + Docteur.T("MD.Delai", Budget.TotalSeconds);
                reste.Noter();
            }
        }
        catch (Exception ex)
        {
            texte = Docteur.T("RD.Titre") + Environment.NewLine + $"exception {ex.GetType().Name}: {ex.Message}";
            reste.Noter();
        }

        Console.Out.WriteLine(texte);
        var journal = EcrireJournal(racine, quand, texte, appliquerAcl: true);
        if (!journal.IsOk)
            Console.Error.WriteLine("report not written: " + journal.Error?.Message);
        var marque = NoterLIssue(racine, quand, reste.Quelquechose, appliquerAcl: true);
        if (!marque.IsOk)
            Console.Error.WriteLine("incomplete-revert mark not updated: " + marque.Error?.Message);
        return 0;
    }

    /// <summary>Ce qu'une étape n'a pas pu rendre. Le manifeste resté en place ne couvre que ses propres lignes : une ligne du
    /// journal en suspens, une carte, le plafond d'envoi ou un programme au démarrage non rendus ne le gardent pas, et le joueur
    /// n'en apprenait rien.</summary>
    internal sealed class ResteARendre
    {
        private int _nombre;
        public bool Quelquechose => Volatile.Read(ref _nombre) > 0;
        public void Noter() => Interlocked.Increment(ref _nombre);
    }

    /// <summary>Les droits corrigés d'un service restent : rendre le fichier d'un service SYSTEM au compte rouvrirait le chemin
    /// qu'il ferme. Le rapport nomme ces services ; rien n'est écrit.</summary>
    internal static string DroitsQuiRestent(string racine)
        => DroitsDesServices.ServicesCorriges(DroitsDesServices.Racine(racine)) is { Count: > 0 } services
            ? Docteur.T("MD.DroitsRestent", string.Join(", ", services)) + Environment.NewLine
            : "";

    /// <summary>Le nom de la marque que l'installeur lit (`[Code]` de DrDXT.iss) pour dire au joueur que tout n'a pas été rendu.</summary>
    public const string MarqueRenduIncomplet = "rendu-incomplet";

    /// <summary>Pose la marque, qui nomme le rapport du jour, quand quelque chose reste ; sinon retire celle d'une
    /// désinstallation précédente, puisque cette fois tout a été rendu.</summary>
    internal static Result NoterLIssue(string racine, DateTimeOffset quand, bool incomplet, bool appliquerAcl)
    {
        var chemin = Path.Combine(racine, MarqueRenduIncomplet);
        if (incomplet)
        {
            var octets = Encoding.UTF8.GetBytes(NomDuRapport(quand) + Environment.NewLine);
            if (appliquerAcl) return TrustedManifestRoot.WriteTrustedFile(chemin, octets);
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
        try
        {
            // Supprimer un nom ne suit pas un lien : c'est l'entrée elle-même qui part.
            if (File.Exists(chemin)) File.Delete(chemin);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.IoFailure, $"Could not remove '{chemin}': {ex.Message}");
        }
    }

    private static string NomDuRapport(DateTimeOffset quand)
        => "desinstallation-" + quand.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".txt";

    /// <summary>Le rejeu du journal, en texte ; vide quand il n'avait rien. Ne jette jamais.</summary>
    internal static async Task<string> RejouerLeJournalAsync(DrDxt.ElevatedHelper.Journal.JournalDEcritureAnticipee? journal,
        ILectureDesCibles lecture, IElevatedClient ecriture, DateTimeOffset quand, CancellationToken ct, ResteARendre? reste = null)
    {
        try
        {
            if (await JournalDuHelper.RejouerAsync(journal, lecture, ecriture, RejeuDuJournal.ProcessusVivant,
                    RejeuDuJournal.CompteCourant, ct).ConfigureAwait(false) is not { } r || (r.Lignes.Count == 0 && r.ManifesteNonLu is null))
                return string.Empty;
            // Un journal illisible ou une ligne en suspens : son entrée reste dans `jea`, la désinstallation suivante la retente.
            if (r.ManifesteNonLu is not null || r.Lignes.Any(l => l.OrigineEnSuspens)) reste?.Noter();
            return r.EnTexte(quand, Docteur.T("MD.JournalLu"), Docteur.T("MD.JournalNonLu"));
        }
        catch (Exception ex)
        {
            reste?.Noter();
            return $"exception {ex.GetType().Name} : {ex.Message}" + Environment.NewLine;
        }
    }

    /// <summary>Compose la lecture verify-only et le rendu ; rend le texte du rapport. Ne jette jamais.</summary>
    internal static async Task<string> ExecuterAsync(string racine, bool appliquerAcl, ILectureDesCibles lecture,
        IElevatedClient ecriture, DateTimeOffset quand, CancellationToken ct, ResteARendre? reste = null)
    {
        RapportDuRendu rapport;
        var stockage = new ProgramDataManifestStorage(racine, appliquerAcl);
        // La clé reste d'une installation précédente, le manifeste a été mis de côté : rien n'est « lu et vérifié », rien
        // n'est « mis de côté », et rien ne reste à rendre.
        var sansManifeste = false;
        try
        {
            var verificateur = MachineManifestKey.ChargerVerificateur(racine, appliquerAcl);
            sansManifeste = verificateur.IsOk && !File.Exists(Path.Combine(racine, ProgramDataManifestStorage.NomDuFichier));
            rapport = !verificateur.IsOk
                ? new RapportDuRendu($"{verificateur.Error!.Value.Code} : {verificateur.Error.Value.Message}",
                    Array.Empty<LigneDuRendu>())
                : sansManifeste
                ? new RapportDuRendu(null, Array.Empty<LigneDuRendu>())
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

        var texte = sansManifeste ? rapport.EnTexte(quand, Docteur.T("MD.AucunManifeste")) : rapport.EnTexte(quand);

        // Le manifeste est mis de cote une fois lu. S'il restait en place apres la
        // desinstallation, une reinstallation y retrouverait des origines capturees avant, deja rendues, et
        // le jour ou un service les rejouerait il reecrirait un passe que l'utilisateur a quitte. Il est
        // renomme, jamais efface. Non lu ⇒ on n'y touche pas : il n'a pas ete rendu.
        // Une origine non rendue (écriture refusée, cible illisible) reste à rendre : le manifeste garde sa place, une
        // réinstallation puis une désinstallation la retentent.
        var nonRendues = rapport.Lignes.Count(l => l.OrigineEnSuspens);
        if (rapport.ManifesteNonLu is null && nonRendues > 0)
        {
            reste?.Noter();
            texte += Docteur.T("MD.ManifesteGarde", nonRendues) + Environment.NewLine;
        }
        else if (rapport.ManifesteNonLu is null && !sansManifeste)
        {
            var deCote = stockage.MettreDeCoteApresRendu();
            if (!deCote.IsOk) reste?.Noter();
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
    internal static async Task<string> RetirerPlafondEnvoiAsync(IElevatedClient ecriture, CancellationToken ct, ResteARendre? reste = null)
    {
        try
        {
            var r = await ecriture.ExecuteAsync(new RemoveUploadCapOp(), ct).ConfigureAwait(false);
            if (!r.IsOk) reste?.Noter();
            return r.IsOk
                ? Docteur.T("MD.PlafondRetire") + Environment.NewLine
                : Docteur.T("MD.PlafondReste", r.Error?.Message) + Environment.NewLine;
        }
        catch (Exception ex)
        {
            reste?.Noter();
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
        CancellationToken ct, ResteARendre? reste = null)
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
            if (refus.Count > 0) reste?.Noter();
            texte.Append(refus.Count == 0 ? Docteur.T("MD.CarteRendue", i) : Docteur.T("MD.CarteReste", i, string.Join(" ; ", refus)))
                .Append(Environment.NewLine);
        }
        return texte.ToString();
    }

    /// <summary>Un programme coupé au démarrage ne passe pas par le manifeste : sa trace le porte, avec les octets exacts que
    /// DrDXT a écrits (l'heure de la coupure comprise). Il n'est réactivé que si la valeur les porte encore : recoupé ou
    /// réactivé à la main depuis, ou disparu, il est laissé. Aucun échec n'arrête les autres.</summary>
    internal static async Task<string> ReactiverLesCoupuresAsync(IReadOnlyList<CoupureDuDemarrage> coupures,
        Func<CoupureDuDemarrage, byte[]?> octetsCourants, IElevatedClient ecriture, CancellationToken ct, ResteARendre? reste = null)
    {
        var texte = new StringBuilder();
        foreach (var c in coupures)
        {
            string ligne;
            try
            {
                if (octetsCourants(c) is not { } courant || !string.Equals(Convert.ToHexString(courant), c.Octets, StringComparison.OrdinalIgnoreCase))
                    ligne = Docteur.T("MD.DemarrageLaisse", c.Nom);
                else
                {
                    var r = await ecriture.ExecuteAsync(new SetStartupApprovalOp(c.Ruche, c.Liste, c.Nom, true), ct).ConfigureAwait(false);
                    if (!r.IsOk) reste?.Noter();
                    ligne = r.IsOk ? Docteur.T("MD.DemarrageReactive", c.Nom) : Docteur.T("MD.DemarrageReste", c.Nom, r.Error?.Message);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                reste?.Noter();
                ligne = Docteur.T("MD.DemarrageReste", c.Nom, ex.GetType().Name);
            }
            texte.Append(ligne).Append(Environment.NewLine);
        }
        return texte.ToString();
    }

    /// <summary>La trace qu'une réactivation met à jour : celle de la machine seulement, dans la racine de confiance. Celle du
    /// compte n'est jamais réécrite en administrateur : son dossier (%LOCALAPPDATA%\DrDXT) appartient au compte, qui peut en faire
    /// une jonction vers la racine de confiance et y faire poser un fichier choisi. Sa coupure, réactivée, y reste notée : une
    /// réinstallation la propose encore, et la réactiver de nouveau ne change rien.</summary>
    internal static TraceDesCoupures? TraceDuRendu(RegistryHiveKind ruche) =>
        ruche == RegistryHiveKind.LocalMachine ? TraceDesCoupures.DeLaMachine() : null;

    /// <summary>Les coupures de la machine, et celles du compte quand il est celui de la session : élevé avec un autre
    /// compte, la trace du joueur est dans un dossier que ce processus ne lit pas.</summary>
    private static IReadOnlyList<CoupureDuDemarrage> CoupuresARendre(bool memeCompte) =>
        [.. TraceDesCoupures.DeLaMachine().Lire(), .. memeCompte ? TraceDesCoupures.DeLaSession().Lire() : []];

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
        var chemin = Path.Combine(racine, NomDuRapport(quand));
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

    // Le firmware et l'ecrivain du manifeste sont absents : ce mode ne sait que rendre (registre, plan, service, DRS du
    // profil de base).
    // La carte s'écrit aussi, pour rendre une limite de puissance, Anti-Lag, FRTC ou la Vsync AMD gardés ; seules
    // les origines admises par OpAllowlist.ValiderOrigine passent (jamais fréquence ni tension). Hors manifeste, le ventilateur
    // et le décalage du cœur ne reviennent qu'à l'automatique et à 0 (RendreLaCarteNvidiaAsync).
    /// <summary>Élevé avec le compte d'un autre (UAC « par-dessus l'épaule »), HKCU est la ruche de cet autre : les origines
    /// de la session du joueur n'y sont pas écrites. Refusées et dites, elles gardent le manifeste en place (une origine non
    /// rendue ne le laisse pas mettre de côté).</summary>
    public static IElevatedClient GarderLaRucheDuJoueur(IElevatedClient client, bool memeCompte) =>
        memeCompte ? client : new SansRucheEtrangere(client);

    /// <summary>La lecture aussi : lue dans la ruche d'un autre compte, une origine du joueur passait pour « respectée » ou
    /// « disparue », sans ligne non rendue, et le manifeste partait. Refusée, elle est illisible et le garde.</summary>
    internal static ILectureDesCibles GarderLaRucheDuJoueur(ILectureDesCibles lecture, bool memeCompte) =>
        memeCompte ? lecture : new LectureSansRucheEtrangere(lecture);

    private static bool EstHkcu(PrivilegedOp op) =>
        op is SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } or DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser }
            or SetStartupApprovalOp { Hive: RegistryHiveKind.CurrentUser };

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
    public static bool MemeCompteQueLaSession()
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
            new Win32RegistryActions(TraceDuRendu),
            new Win32PowerPlanActions(),
            new Win32RestorePointActions(),
            new Win32ServiceActions(),
            new NvApiDrsActions(),
            new NvApiTuningActions(),
            new ADLXTuningActions(adlx),
            // La desinstallation doit pouvoir retirer le plafond d'envoi.
            qos: new Win32NetworkQosActions(),
            // L'executeur exige ses actions BCD ; la desinstallation ne rend aucun element BCD (il n'est ni au manifeste ni au
            // journal) : le README le dit dans « Ce qui reste ».
            bcd: new Win32BcdActions(),
            // Rendre un ventilateur resté en vitesse fixe à l'automatique.
            ventilateur: new NvmlVentilateurActions(),
            // Rendre une limite de puissance du processeur restée posée.
            cpu: new PawnIoCpuPowerActions()), journal, lecteur));
}
