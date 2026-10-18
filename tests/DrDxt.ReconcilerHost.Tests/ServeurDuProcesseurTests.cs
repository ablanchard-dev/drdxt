using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using DrDxt.ReconcilerHost;
using FluentAssertions;
using Xunit;

namespace DrDxt.ReconcilerHost.Tests;

// L'état du processeur servi par le service à l'App sans droits, par un tube en lecture seule.
public sealed class ServeurDuProcesseurTests
{
    private static readonly EtatCpu Etat = new(98, 77, false, false, false, true);
    private static string NomUnique() => "DrDXT.Processeur.Test." + Guid.NewGuid().ToString("N");
    private static uint MonPid => (uint)Environment.ProcessId;

    [Fact]
    public async Task Une_connexion_rend_l_etat_lu_par_le_service_au_bon_serveur_seulement()
    {
        var nom = NomUnique();
        var serveur = new ServeurDuProcesseur(() => Etat, () => { }, () => false, () => DateTimeOffset.Now, nom);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var service = serveur.ServirAsync(fin.Token, connexionsMax: 3);
        (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => DateTimeOffset.Now))).Should().Be(Etat);
        // Une seconde lecture de suite : l'instance suivante était déjà là.
        (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => DateTimeOffset.Now))).Should().Be(Etat);

        // Un tube servi par un autre processus que le service (squatteur) : rien n'est cru.
        (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid + 1, () => DateTimeOffset.Now))).Should().BeNull();
        (await service).Should().BeTrue();
    }

    // Un client qui se connecte et ne lit jamais ne fige pas le service.
    [Fact]
    public async Task Un_client_qui_ne_lit_pas_ne_bloque_pas_les_suivants()
    {
        var nom = NomUnique();
        // Le muet garde son instance 60 s, la lecture a 10 s : un serveur qui servirait un client après l'autre ne répond
        // jamais à temps, un serveur qui les sert à part répond en quelques dizaines de ms. Avec la borne réelle (1 s), un
        // serveur en série répondait lui aussi dans les 2 s, et la marge sous charge (pool de fils du test affamé) était nulle.
        var serveur = new ServeurDuProcesseur(() => Etat, () => { }, () => false, () => DateTimeOffset.Now, nom,
            delaiEcriture: TimeSpan.FromSeconds(60));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var service = serveur.ServirAsync(fin.Token, connexionsMax: 6);

        using var muet = new NamedPipeClientStream(".", nom, PipeDirection.In);
        await muet.ConnectAsync(2000);
        (await LireDansLes(nom, TimeSpan.FromSeconds(10))).Should().Be(Etat);
        fin.Cancel();
        (await service).Should().BeTrue();
    }

    private static async Task<EtatCpu?> LireDansLes(string nom, TimeSpan delai)
    {
        var limite = DateTime.UtcNow + delai;
        while (true)
        {
            if (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => DateTimeOffset.Now)) is { } lu) return lu;
            if (DateTime.UtcNow > limite) return null;
            await Task.Delay(100);
        }
    }

    // Quatre instances au plus, et un client gardait la sienne jusqu'à 1 s ; quatre connexions muettes
    // de suite faisaient échouer l'instance suivante, et le service lâchait le tube 30 s (déni de service par tout utilisateur).
    [Fact]
    public async Task Des_clients_muets_en_rafale_ne_coupent_pas_le_tube()
    {
        var nom = NomUnique();
        var serveur = new ServeurDuProcesseur(() => Etat, () => { }, () => false, () => DateTimeOffset.Now, nom);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var service = serveur.ServirAsync(fin.Token, connexionsMax: 7);

        var muets = new List<NamedPipeClientStream>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var muet = new NamedPipeClientStream(".", nom, PipeDirection.In);
                await muet.ConnectAsync(2000);
                muets.Add(muet);
            }
            (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => DateTimeOffset.Now))).Should().Be(Etat);
            (await service).Should().BeTrue();
        }
        finally { muets.ForEach(m => m.Dispose()); }
    }

    // Les ventilateurs de la carte mère (lus par le service, qui a les droits) passent par le même tube.
    [Fact]
    public async Task Les_ventilateurs_passent_aussi_par_le_tube()
    {
        var nom = NomUnique();
        VentilateurLu[] ventilateurs = [new(0, 1265, 55), new(5, 1210, 21)];
        var serveur = new ServeurDuProcesseur(() => Etat, () => { }, () => false, () => DateTimeOffset.Now, nom,
            lireVentilateurs: () => ventilateurs);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = serveur.ServirAsync(fin.Token, connexionsMax: 1);

        var lu = await Task.Run(() => ProcesseurParLeService.LireTout(nom, () => MonPid, () => DateTimeOffset.Now));
        lu!.Ventilateurs.Should().Equal(ventilateurs);
        (await service).Should().BeTrue();
    }

    // V2, incrément 2 : les limites de puissance passent par le même tube.
    [Fact]
    public async Task Les_limites_de_puissance_passent_aussi_par_le_tube()
    {
        var nom = NomUnique();
        var limites = new RegistresCpuLus(0x3, (2024UL << 32) | (1UL << 15) | 2024UL, 1000UL);
        var serveur = new ServeurDuProcesseur(() => Etat, () => { }, () => false, () => DateTimeOffset.Now, nom, () => (limites, 0UL));
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = serveur.ServirAsync(fin.Token, connexionsMax: 1);

        var lu = await Task.Run(() => ProcesseurParLeService.LireTout(nom, () => MonPid, () => DateTimeOffset.Now));
        lu!.Limites.Should().Be(limites);
        lu.CopieMmio.Should().Be(0UL);
        (await service).Should().BeTrue();
    }

    [Fact]
    public void Sans_service_rien_n_est_attendu_et_rien_n_est_lu()
        => ProcesseurParLeService.Lire(NomUnique(), () => null, () => DateTimeOffset.Now).Should().BeNull();

    [Fact]
    public void Le_tube_ne_donne_que_la_lecture_aux_utilisateurs_et_refuse_le_reseau()
    {
        var regles = ServeurDuProcesseur.Securite().GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        var utilisateurs = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var reseau = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);

        var droitsUtilisateurs = regles.Where(r => r.IdentityReference == utilisateurs && r.AccessControlType == AccessControlType.Allow)
            .Aggregate((PipeAccessRights)0, (a, r) => a | r.PipeAccessRights);
        (droitsUtilisateurs & (PipeAccessRights.WriteData | PipeAccessRights.CreateNewInstance | PipeAccessRights.ChangePermissions
            | PipeAccessRights.TakeOwnership)).Should().Be((PipeAccessRights)0);
        regles.Should().Contain(r => r.IdentityReference == reseau && r.AccessControlType == AccessControlType.Deny);
        ServeurDuProcesseur.Securite().AreAccessRulesProtected.Should().BeTrue("aucune règle héritée plus large");
    }

    [Fact]
    public void PawnIO_se_ferme_apres_dix_secondes_sans_demande()
    {
        var maintenant = new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);
        var fermetures = 0;
        var serveur = new ServeurDuProcesseur(() => Etat, () => fermetures++, () => true, () => maintenant, NomUnique());

        serveur.FermerSiInactif();
        fermetures.Should().Be(1, "jamais demandé : rien ne justifie de le garder ouvert");
    }

    // La borne des 10 s elle-même (pas fermé à 9 s, fermé à 10 s).
    [Fact]
    public async Task PawnIO_reste_ouvert_neuf_secondes_et_se_ferme_a_dix()
    {
        var maintenant = new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);
        var fermetures = 0;
        var nom = NomUnique();
        var serveur = new ServeurDuProcesseur(() => Etat, () => fermetures++, () => true, () => maintenant, nom);
        using var fin = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var service = serveur.ServirAsync(fin.Token, connexionsMax: 1);
        (await Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => maintenant))).Should().Be(Etat);
        await service;

        maintenant = maintenant.AddSeconds(9);
        serveur.FermerSiInactif();
        fermetures.Should().Be(0);
        maintenant = maintenant.AddSeconds(1);
        serveur.FermerSiInactif();
        fermetures.Should().Be(1);
    }

    // Un serveur qui accepte et ne répond jamais ne fige pas le client.
    [Fact]
    public async Task Un_serveur_muet_ne_fige_pas_le_client()
    {
        var nom = NomUnique();
        using var muet = new NamedPipeServerStream(nom, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var attente = muet.WaitForConnectionAsync();
        var lecture = Task.Run(() => ProcesseurParLeService.Lire(nom, () => MonPid, () => DateTimeOffset.Now));
        var fini = await Task.WhenAny(lecture, Task.Delay(TimeSpan.FromSeconds(5)));
        fini.Should().BeSameAs(lecture, "la lecture est bornée");
        (await lecture).Should().BeNull();
        await attente;
    }

    [Fact]
    public void Une_lecture_perimee_du_futur_ou_abimee_n_est_pas_crue()
    {
        var t = new DateTimeOffset(2026, 10, 12, 5, 0, 0, TimeSpan.Zero);
        var json = new EtatCpuServi(t, Etat).VersJson();
        EtatCpuServi.Valider(json, t.AddSeconds(5)).Should().Be(Etat);
        EtatCpuServi.Valider(json, t.AddSeconds(11)).Should().BeNull("périmée");
        EtatCpuServi.Valider(json, t.AddSeconds(-5)).Should().BeNull("datée du futur");
        EtatCpuServi.Valider(new EtatCpuServi(t, null).VersJson(), t).Should().BeNull("le service n'a rien lu");
        EtatCpuServi.Valider("{abîmé", t).Should().BeNull();
    }
}
