using System.Diagnostics;
using System.IO.Pipes;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// RED TEAM — le tube usurpe. Mesure du 11/09/2026, garde POSEE le meme jour.
//
// Le defaut : l'authentification du canal privilegie etait a SENS UNIQUE. Le serveur
// identifiait l'appelant (pid → chemin + SHA-256) ; le client n'identifiait pas le
// serveur. Le nom du tube etant une chaine fixe et publique, un processus non
// privilegie tournant sous le meme compte le prenait avant le helper et repondait Ok
// a tout : l'App concluait « applique » alors que rien n'avait tourne, et le squatteur
// recevait la liste des ops demandees.
//
// La garde : HandshakingElevatedClient resout le pid du serveur du tube
// (GetNamedPipeServerProcessId) et compare son image au chemin attendu du helper, avant
// le handshake. Ce fichier prouve les deux chemins, en in-process :
//   * le TEMOIN — chemin attendu = l'image de ce processus (qui sert vraiment le tube)
//     ⇒ la session s'etablit. Sans lui, les refus ci-dessous ne prouveraient rien : une
//     garde qui refuse tout, toujours, les rendrait verts aussi ;
//   * les REFUS — meme serveur, meme code, seule l'image attendue change ⇒ refus nomme,
//     et le squatteur n'apprend rien (le refus tombe avant que l'op parte sur le fil).
//
// Ce qui n'est pas prouve ici : la traversee d'integrite reelle (App moyenne →
// helper ELEVE). Tout tourne ici a integrite egale. Voir « GATE FERME » dans
// la revue de securite interne (non publiee).
//
// Rien ici n'eleve ni ne touche la machine : un tube nomme vit en memoire, le nom est
// unique par test, et le seul processus lance est un cmd.exe inerte aussitot tue.
// -----------------------------------------------------------------------
public sealed class TubeUsurpeTests
{
    private static string TubeUnique() => "DrDXT.Usurpe." + Guid.NewGuid().ToString("N");

    // L'image de ce processus de test : c'est lui qui sert reellement les tubes ci-dessous,
    // donc c'est le « helper attendu » du cas legitime.
    private static string CeProcessus() => Environment.ProcessPath!;

    // Une image DIFFERENTE de celle qui repond : le helper reel, pose a cote des binaires de
    // test mais qui ne sert aucun de ces tubes. C'est le cas du squatteur.
    private static string AutreImage()
        => Path.Combine(AppContext.BaseDirectory, PipeServerIdentity.HelperExeName);

    // Le squatteur : un serveur de tube ordinaire, tel qu'un processus non privilegie
    // tournant sous le meme compte peut en creer un. Il ne sait rien faire — il dit oui.
    // Il tolere d'etre lache en cours de route : c'est exactement ce que fait un client
    // qui refuse la session avant de parler.
    private static async Task SquatterAsync(string tube, List<TypedPrivilegedRequest> recues, CancellationToken ct)
    {
        try
        {
            using var serveur = new NamedPipeServerStream(
                tube, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            await serveur.WaitForConnectionAsync(ct);

            // 1. Avaler le hello et repondre une cle de session de la bonne taille. Le squatteur
            //    n'a aucun secret a connaitre : c'est lui qui choisit la cle.
            await WireCodec.ReadMessageAsync<HandshakeHello>(serveur, ct);
            await WireCodec.WriteMessageAsync(serveur, new HandshakeResult
            {
                Ok = true,
                SessionKeyBase64 = Convert.ToBase64String(new byte[HandshakeProtocol.SessionKeyBytes]),
            }, ct);

            // 2. Lire l'op signee (avec sa cle, donc le HMAC lui convient par construction) et
            //    repondre « c'est fait ».
            var requete = await WireCodec.ReadMessageAsync<TypedPrivilegedRequest>(serveur, ct);
            if (requete is not null) recues.Add(requete);
            await WireCodec.WriteMessageAsync(serveur, new PrivilegedResponse { Ok = true }, ct);
        }
        catch (IOException)
        {
            // Le client a raccroche sans parler — c'est le refus, pas une panne.
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<(Result Resultat, List<TypedPrivilegedRequest> Recues)> JouerUnOpAsync(
        string? cheminAttendu)
    {
        var tube = TubeUnique();
        var recues = new List<TypedPrivilegedRequest>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var squatteur = SquatterAsync(tube, recues, cts.Token);

        var app = new HandshakingElevatedClient(tube, cheminAttendu);
        var resultat = await app.ExecuteAsync(
            new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance), cts.Token);

        await squatteur;
        return (resultat, recues);
    }

    // --- TEMOIN : le serveur est l'image attendue → la session passe. ---
    [Fact]
    public async Task Un_serveur_dont_l_image_est_celle_attendue_est_accepte()
    {
        var (resultat, recues) = await JouerUnOpAsync(CeProcessus());

        resultat.IsOk.Should().BeTrue(
            "sans ce temoin, une garde qui refuserait TOUT rendrait les refus ci-dessous verts "
            + "sans qu'aucun canal privilegie ne soit plus jamais possible");
        recues.Should().ContainSingle("le canal legitime va bien jusqu'a l'op signee");
    }

    // --- REFUS : meme serveur, autre image attendue → refus nomme, et rien ne fuit. ---
    [Fact]
    public async Task Un_serveur_usurpe_est_refuse_avant_d_apprendre_quoi_que_ce_soit()
    {
        var (resultat, recues) = await JouerUnOpAsync(AutreImage());

        resultat.IsOk.Should().BeFalse(
            "le processus qui repond n'est pas le helper attendu : c'est un squatteur");
        resultat.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        resultat.Error.Value.Message.Should().Contain("serveur du tube non authentifié",
            "un refus qui ne se nomme pas se confond avec une panne de tube");
        resultat.Error.Value.Message.Should().Contain("processus attendu",
            "le message doit dire ce qu'on attendait ET ce qu'on a trouve");

        recues.Should().BeEmpty(
            "le refus tombe AVANT le handshake : le squatteur n'apprend meme pas quelles ops "
            + "DrDXT voulait appliquer");
    }

    // --- FAIL-CLOSED : pas de chemin attendu ⇒ on ne fait confiance a personne. ---
    [Fact]
    public async Task Sans_chemin_de_helper_attendu_le_client_refuse_la_session()
    {
        var (resultat, recues) = await JouerUnOpAsync(null);

        resultat.IsOk.Should().BeFalse("omettre l'argument ne doit pas desactiver la garde");
        resultat.Error!.Value.Code.Should().Be(ErrorCodes.ElevationDenied);
        resultat.Error.Value.Message.Should().Contain("aucun chemin de helper attendu");
        recues.Should().BeEmpty();
    }

    // --- La moitie deja tenue : le helper ne partage jamais son nom en silence. ---
    [Fact]
    public void Un_squatteur_empeche_le_vrai_helper_de_servir_son_tube()
    {
        var tube = TubeUnique();

        // Le squatteur prend le nom en premier, sans aucun droit particulier.
        using var _ = new NamedPipeServerStream(
            tube, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

        // Le helper, lui, exige FirstPipeInstance : il ne partage jamais son nom en silence.
        // Il echoue donc LOYALEMENT — c'est la moitie deja tenue de cette surface.
        var creation = () => PipeSecurityFactory.CreateServerStream(tube);
        creation.Should().Throw<IOException>(
            "le helper refuse de servir un nom deja pris plutot que de coexister avec un squatteur");
    }

    // --- Le resolveur travaille sur un autre processus que lui-meme. ---
    //
    // La garde repose sur OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION) +
    // QueryFullProcessImageNameW. Se resoudre soi-meme serait un cas trop favorable : ce
    // test lance un processus tiers, dont on connait le chemin d'image exact, et exige que
    // le resolveur le retrouve. Aucune elevation, aucune ecriture : un cmd.exe inerte,
    // aussitot tue.
    [Fact]
    public void L_image_d_un_processus_tiers_est_resolue_par_son_pid()
    {
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        using var enfant = Process.Start(new ProcessStartInfo
        {
            FileName = cmd,
            Arguments = "/c pause",
            UseShellExecute = false,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        })!;

        try
        {
            var image = PipeServerIdentity.TryGetProcessImagePath(enfant.Id);
            image.Should().NotBeNull("un processus vivant doit avoir une image resoluble");
            Path.GetFullPath(image!).Should().BeEquivalentTo(Path.GetFullPath(cmd));
        }
        finally
        {
            enfant.Kill(entireProcessTree: true);
            enfant.WaitForExit(5000);
        }

        // Et un pid qui ne peut pas exister (les pids Windows sont des multiples de 4) ne rend
        // rien — le fail-closed du resolveur lui-meme : il ne devine jamais.
        PipeServerIdentity.TryGetProcessImagePath(int.MaxValue).Should().BeNull(
            "un pid sans processus n'a pas d'image : la garde doit alors refuser, pas deviner");
    }
}
