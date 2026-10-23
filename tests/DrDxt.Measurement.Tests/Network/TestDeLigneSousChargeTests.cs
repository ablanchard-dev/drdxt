using System.Globalization;
using System.Net;
using System.Net.Http;
using DrDxt.Core;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Network;

/// <summary>
/// Le test de bufferbloat actif. La règle « la file est dans la carte » vaut pour tout lien, 1 Gbit/s compris : sur un
/// lien de 1 Gbit/s saturé à 95 %, en Ethernet, accuser la box serait faux.
/// </summary>
public sealed class TestDeLigneSousChargeTests
{
    private static PhaseSousCharge Phase(double ms, int n = 20, double? debit = null, double? box = null)
        => new(Enumerable.Repeat<double?>(ms, n).ToList(), box is { } b ? Enumerable.Repeat<double?>(b, n).ToList() : [], debit, 0);

    [Fact]
    public void Un_lien_de_1_Gbit_sature_met_la_file_dans_la_carte_pas_dans_la_box()
    {
        var v = TestDeLigneSousCharge.Juger(Phase(9, box: 1), Phase(65, debit: 950, box: 30), lienMbps: 1000);
        v.Mesurable.Should().BeTrue();
        v.HausseInternetMs.Should().Be(56);
        v.HausseBoxMs.Should().Be(29);
        v.FileDansLaCarte.Should().BeTrue("à 95 % du lien, quelle que soit sa vitesse, la carte est le goulot probable");
    }

    [Fact]
    public void Sous_85_pour_cent_du_lien_la_carte_n_est_pas_accusee()
    {
        var v = TestDeLigneSousCharge.Juger(Phase(9), Phase(65, debit: 500), lienMbps: 1000);
        v.Monte.Should().BeTrue();
        v.FileDansLaCarte.Should().BeFalse();
    }

    [Fact]
    public void Un_ping_qui_monte_moins_que_le_seuil_n_est_pas_un_bufferbloat()
    {
        var v = TestDeLigneSousCharge.Juger(Phase(9), Phase(9 + Bufferbloat.SeuilMs - 1, debit: 950), 1000);
        v.Monte.Should().BeFalse();
        v.FileDansLaCarte.Should().BeFalse();
    }

    // Le serveur injoignable ou qui refuse : aucune charge n'est passée, les pings « en charge » ont été pris au repos.
    [Fact]
    public void Une_charge_qui_n_a_rien_fait_passer_ne_se_juge_pas_et_le_dit()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var v = TestDeLigneSousCharge.Juger(Phase(9), Phase(9, debit: 0), 1000);
        v.Mesurable.Should().BeFalse();
        v.Monte.Should().BeFalse();
        var m = new MesureSousCharge(Phase(9), Phase(9, debit: 0), Phase(9, debit: 0));
        TestDeLigneSousCharge.Phrase(m, 1000, DateTimeOffset.Now).Should().Contain("pas été chargée").And.NotContain("rien à corriger");
    }

    [Fact]
    public void Trop_peu_de_reponses_ne_se_juge_pas()
    {
        var v = TestDeLigneSousCharge.Juger(Phase(9), Phase(80, n: TestDeLigneSousCharge.MinReponses - 1, debit: 900), 1000);
        v.Mesurable.Should().BeFalse();
        v.Monte.Should().BeFalse();
    }

    [Fact]
    public void Le_compte_rendu_nomme_le_serveur_et_juge_chaque_sens()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var m = new MesureSousCharge(Phase(9), Phase(65, debit: 950) with { Octets = 300_000_000 }, Phase(10, debit: 40) with { Octets = 50_000_000 });
        var phrase = TestDeLigneSousCharge.Phrase(m, 1000, DateTimeOffset.Now);
        phrase.Should().Contain("speed.cloudflare.com").And.Contain("350 Mo").And.Contain("carte du PC").And.Contain("rien à corriger");
    }

    private sealed class Sonde : INetworkProbe
    {
        public int Appels;
        public Task<NetworkProbeSample> ProbeAsync(string host, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Appels);
            return Task.FromResult(new NetworkProbeSample(host == "192.168.1.1" ? 1 : 10));
        }
    }

    private sealed class Serveur : HttpMessageHandler
    {
        public readonly List<(HttpMethod Methode, Uri Adresse, long Octets)> Demandes = [];
        // La première requête reçue : un test qui annule « pendant la charge » attend ce signal, pas une durée.
        public readonly TaskCompletionSource PremiereDemande = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Le réseau rend la main à chaque requête ; servie en mémoire, elle se terminait sur place, et les flux sans
            // plafond tournaient sans jamais céder leur fil : la suite du test attendait un fil du pool pendant que le tas
            // montait de centaines de Mo, au-delà de l'attente de 10 s sur un runner à 4 cœurs.
            await Task.Yield();
            long octets = 0;
            if (request.Content is { } c)
            {
                var corps = await c.ReadAsByteArrayAsync(ct);
                corps.Should().OnlyContain(b => b == 0, "rien de la machine ne part, seulement des octets nuls");
                octets = corps.Length;
            }
            lock (Demandes) Demandes.Add((request.Method, request.RequestUri!, octets));
            PremiereDemande.TrySetResult();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[1024]) };
        }
    }

    [Fact]
    public async Task Le_test_ne_parle_qu_au_serveur_de_mesure_declare()
    {
        var serveur = new Serveur();
        var m = await TestDeLigneSousCharge.MesurerAsync(new HttpClient(serveur), new Sonde(), "1.1.1.1", "192.168.1.1",
            CancellationToken.None, duree: TimeSpan.FromMilliseconds(400));

        serveur.Demandes.Should().NotBeEmpty();
        serveur.Demandes.Should().OnlyContain(d => d.Adresse.Host == TestDeLigneSousCharge.Hote && d.Adresse.Scheme == "https");
        serveur.Demandes.Should().Contain(d => d.Methode == HttpMethod.Get).And.Contain(d => d.Methode == HttpMethod.Post);
        m.Repos.DebitMbps.Should().BeNull("au repos, rien n'est chargé");
        m.Repos.Internet.Should().NotBeEmpty();
        m.Repos.Passerelle.Should().NotBeEmpty();
    }

    [Fact]
    public async Task Chaque_sens_s_arrete_au_plafond_annonce()
    {
        // Le plafond est une promesse du README (400 Mo par sens) : il tient à un tampon près par flux, pas à une
        // requête entière ni au délai d'un ping.
        const long plafond = 300_000;
        var m = await TestDeLigneSousCharge.MesurerAsync(new HttpClient(new Serveur()), new Sonde(), "1.1.1.1", null,
            CancellationToken.None, duree: TimeSpan.FromSeconds(2), plafond: plafond);

        var marge = TestDeLigneSousCharge.Flux * 64 * 1024;
        m.Montee.Octets.Should().BeGreaterThan(0).And.BeLessThanOrEqualTo(plafond + marge);
        m.Descente.Octets.Should().BeLessThanOrEqualTo(plafond + marge);
    }

    [Fact]
    public async Task Annuler_arrete_le_trafic()
    {
        var serveur = new Serveur();
        // Annulé dès que la charge a commencé (la première requête reçue), pas après une durée fixe qu'une machine chargée
        // pouvait dépasser avant la première requête. Attente bornée large : un test qui ne charge jamais échoue au lieu de pendre.
        // Sans plafond : le faux serveur sert en mémoire, et sur une machine chargée les 400 Mo par sens passaient avant
        // l'annulation ; la mesure finissait alors sans exception. Seule l'annulation peut l'arrêter avant 30 s.
        using var annuler = new CancellationTokenSource();
        var test = TestDeLigneSousCharge.MesurerAsync(new HttpClient(serveur), new Sonde(), "1.1.1.1", null, annuler.Token,
            duree: TimeSpan.FromSeconds(30), plafond: long.MaxValue, repos: TimeSpan.FromMilliseconds(50));
        // Annulé même si l'attente expire : sinon la mesure tournerait en fond pendant les tests suivants.
        try { await serveur.PremiereDemande.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { await annuler.CancelAsync(); }

        await test.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
        int apres;
        lock (serveur.Demandes) apres = serveur.Demandes.Count;
        apres.Should().BeGreaterThan(0, "témoin : la charge avait commencé");
        await Task.Delay(300);
        lock (serveur.Demandes) serveur.Demandes.Count.Should().Be(apres, "plus aucune requête après l'annulation");
    }
}
