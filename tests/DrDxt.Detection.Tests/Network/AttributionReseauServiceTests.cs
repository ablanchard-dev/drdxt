using DrDxt.Core;
using DrDxt.Detection.Network;
using DrDxt.Measurement.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

/// <summary>
/// D'ou vient l'instabilite — et tout ce qui fait que le produit se tait.
///
/// Ces tests portent moins sur la phrase que sur les SILENCES. Un service de sante qui, faute
/// de mesure, rend une phrase rassurante est pire que celui qui ne dit rien : le client lit
/// « reseau », croit que c'est verifie, et ferme la question.
/// </summary>
public class AttributionReseauServiceTests
{
    private sealed class CartesFictives(
        string? passerelle, bool lectureEchoue = false, bool sansCarte = false,
        string? secondeSortie = null)
        : INetworkAdapterReader
    {
        /// <summary>Ce que la machine publie EN tout, cartes virtuelles comprises. `secondeSortie`
        /// simule un Wi-Fi connecte en meme temps, ou un tunnel VPN monte.</summary>
        public Result<IReadOnlyList<string>> ReadAllDefaultGateways()
        {
            if (lectureEchoue)
                return Result<IReadOnlyList<string>>.Fail(ErrorCodes.WmiFail, "namespace injoignable");
            var toutes = new List<string>();
            if (passerelle is not null) toutes.Add(passerelle);
            if (secondeSortie is not null) toutes.Add(secondeSortie);
            return Result<IReadOnlyList<string>>.Ok(toutes);
        }

        public Result<IReadOnlyList<NetworkAdapterState>> Read()
            => lectureEchoue
                ? Result<IReadOnlyList<NetworkAdapterState>>.Fail(ErrorCodes.WmiFail, "namespace injoignable")
                : Result<IReadOnlyList<NetworkAdapterState>>.Ok(sansCarte
                    ? []
                    : [
                        // La premiere carte n'a pas de passerelle : Ethernet et Wi-Fi peuvent etre
                        // connectees ensemble, et celle qui porte la route n'est pas en tete.
                        new NetworkAdapterState("Wi-Fi", "Intel AX211", false,
                            TriState.Unknown, TriState.Unknown, TriState.Unknown),
                        new NetworkAdapterState("Ethernet", "Realtek 2.5GbE", true,
                            TriState.Unknown, TriState.Unknown, TriState.Unknown),
                    ]);

        public Result<string?> ReadDefaultGateway(NetworkAdapterState adapter)
            => Result<string?>.Ok(adapter.IsWired ? passerelle : null);
    }

    private sealed class CompteurFictif(double? gigueSaut, double? gigueInternet, bool refuse = false,
        double? secondSaut = null, double? secondInternet = null)
        : INetworkLatencyMeter
    {
        public readonly List<string> Cibles = [];

        public Task<Result<NetworkLatencyReport>> MeasureAsync(
            string host, int probeCount, TimeSpan interval, TimeSpan timeout, CancellationToken ct,
            int warmupProbes = NetworkLatencyMeter.DefaultWarmupProbes)
        {
            Cibles.Add(host);
            if (refuse)
                return Task.FromResult(Result<NetworkLatencyReport>.Fail(ErrorCodes.MeasureNoSamples, "aucune reponse"));

            // La seconde comparaison (appels 3 et 4) reprend les mêmes valeurs, sauf si le test en donne d'autres.
            var seconde = Cibles.Count > 2;
            var gigue = host == "1.1.1.1"
                ? (seconde ? secondInternet ?? gigueInternet : gigueInternet)
                : (seconde ? secondSaut ?? gigueSaut : gigueSaut);
            return Task.FromResult(Result<NetworkLatencyReport>.Ok(
                new NetworkLatencyReport(host, 10, 10, 0, 12, 10, 20, 18, gigue)));
        }
    }

    private static AttributionReseauService Service(
        CompteurFictif compteur, string? passerelle = "192.168.1.1",
        bool lectureEchoue = false, bool sansCarte = false, string? secondeSortie = null)
        => new(new CartesFictives(passerelle, lectureEchoue, sansCarte, secondeSortie), compteur, "1.1.1.1");

    [Fact]
    public async Task La_passerelle_vient_de_la_carte_qui_PORTE_la_route()
    {
        var compteur = new CompteurFictif(6.0, 1.0);

        await Service(compteur).LigneAsync();

        // Une accusation se vérifie : la comparaison est refaite une fois.
        compteur.Cibles.Should().Equal("192.168.1.1", "1.1.1.1", "192.168.1.1", "1.1.1.1");
    }

    [Fact]
    public async Task Une_accusation_qui_ne_se_reproduit_pas_n_accuse_personne()
    {
        // Mesuré le 25/09/2026 sur la machine de reference : sous charge processeur, 3 comparaisons sur 10
        // accusaient la box ou l'opérateur, 0 sur 30 au repos. La seconde comparaison ne dit plus la même chose.
        var ligne = await Service(new CompteurFictif(6.0, 1.0, secondSaut: 1.0, secondInternet: 1.05)).LigneAsync();

        ligne.Should().Contain("rien à désigner").And.NotContain("premier saut (192.168.1.1) est").And.NotContain("commence");
    }

    [Fact]
    public async Task Sans_accusation_la_comparaison_n_est_pas_refaite()
    {
        var compteur = new CompteurFictif(1.20, 1.29);
        await Service(compteur).LigneAsync();
        compteur.Cibles.Should().HaveCount(2);
    }

    [Theory]
    [InlineData(6.0, 1.0)]
    [InlineData(1.0, 9.0)]
    [InlineData(1.20, 1.29)]
    public async Task La_phrase_ne_repete_pas_le_mot_reseau_et_commence_par_une_majuscule(double saut, double internet)
    {
        // 25/09/2026 : vue sur photo, « Réseau : mesuré, … » sous le titre « Réseau » puis
        // « Mesuré sur ta machine » ; dans le rapport, « réseau : mesuré » sous « Réseau mesuré ».
        var ligne = await Service(new CompteurFictif(saut, internet)).LigneAsync();

        ligne.Should().NotBeNull();
        ligne!.Should().NotStartWith("réseau").And.NotStartWith("Réseau");
        char.IsUpper(ligne[0]).Should().BeTrue();
    }

    [Fact]
    public async Task Une_gigue_deja_presente_au_premier_saut_est_dite_AVEC_son_caveat()
    {
        var ligne = await Service(new CompteurFictif(6.0, 1.0)).LigneAsync();

        ligne.Should().Contain("premier saut").And.Contain("192.168.1.1");
        ligne.Should().Contain("piste à confirmer",
            "une box repond a l'ICMP avec son propre processeur : le verdict ne voyage jamais seul");
    }

    [Fact]
    public async Task Une_gigue_qui_apparait_APRES_la_passerelle_n_accuse_pas_la_machine()
    {
        var ligne = await Service(new CompteurFictif(1.0, 9.0)).LigneAsync();

        ligne.Should().Contain("au-delà de la passerelle").And.Contain("Rien à régler sur cette machine");
    }

    [Fact]
    public async Task Deux_fenetres_qui_se_valent_ne_designent_PERSONNE()
    {
        // Le verdict normal d'un lien sain. Sans lui, tout reseau mesurable heritait d'un
        // coupable : un ecart de 0,09 ms accusait le lien local.
        var ligne = await Service(new CompteurFictif(1.20, 1.29)).LigneAsync();

        ligne.Should().Contain("rien à désigner").And.NotContain("piste à confirmer");
    }

    [Fact]
    public async Task Une_fenetre_sans_gigue_exploitable_ne_produit_AUCUNE_phrase()
    {
        var ligne = await Service(new CompteurFictif(null, 1.0)).LigneAsync();

        ligne.Should().BeNull("la mesure a eu lieu et ne tranche pas ; une phrase ici ressemblerait "
                              + "a un bilan de sante");
    }

    [Fact]
    public async Task DEUX_sorties_ouvertes_ferment_le_verdict_au_lieu_de_le_deviner()
    {
        // Le defaut que ce test fige. Le verdict de l'analyseur ne tient a rien d'autre qu'a
        // ceci : le premier saut est contenu dans le chemin complet. C'est une hypothese sur le
        // ROUTAGE, pas une propriete de la mesure — et elle tombe des que la machine a deux
        // sorties (Ethernet + Wi-Fi connectes ensemble, ou un tunnel VPN monte). La passerelle
        // mesuree peut alors appartenir a un chemin que la reference internet n'emprunte pas :
        // on comparerait deux trajets differents et on accuserait un lien qui ne porte rien.
        var compteur = new CompteurFictif(6.0, 1.0);

        var ligne = await Service(compteur, secondeSortie: "10.8.0.1").LigneAsync();

        ligne.Should().BeNull("rien ici ne dit laquelle des deux porte la route");
        compteur.Cibles.Should().BeEmpty("et on ne depense pas quatre secondes de sondes pour un "
                                         + "verdict qu'on sait deja invalide");
    }

    [Fact]
    public async Task Une_passerelle_qui_n_appartient_qu_a_un_TUNNEL_n_est_pas_le_lien_local()
    {
        // La sortie unique existe, mais aucune carte physique ne la publie : mesurer « le lien
        // local » repondrait sur le tunnel.
        var ligne = await Service(new CompteurFictif(6.0, 1.0), sansCarte: true).LigneAsync();

        ligne.Should().BeNull();
    }

    [Fact]
    public async Task Sans_passerelle_lisible_le_produit_ne_mesure_RIEN()
    {
        var compteur = new CompteurFictif(6.0, 1.0);

        var ligne = await Service(compteur, passerelle: null).LigneAsync();

        ligne.Should().BeNull();
        compteur.Cibles.Should().BeEmpty("sans premier saut il n'y a rien a comparer : envoyer "
                                         + "quand meme la fenetre internet couterait deux secondes "
                                         + "pour un verdict impossible");
    }

    [Fact]
    public async Task Une_lecture_de_cartes_en_panne_ne_dit_rien_et_ne_leve_rien()
    {
        var ligne = await Service(new CompteurFictif(6.0, 1.0), lectureEchoue: true).LigneAsync();
        ligne.Should().BeNull();

        (await Service(new CompteurFictif(6.0, 1.0), sansCarte: true).LigneAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Une_mesure_refusee_ne_dit_rien()
    {
        var ligne = await Service(new CompteurFictif(6.0, 1.0, refuse: true)).LigneAsync();

        ligne.Should().BeNull("« non mesure » ne se remplace pas par « reseau sain »");
    }

    [Theory]
    [InlineData(6.0, 1.0)]    // instable des le premier saut
    [InlineData(1.0, 9.0)]    // au-dela de la passerelle
    [InlineData(1.20, 1.29)]  // rien a designer
    public async Task Chaque_verdict_dit_que_ce_sont_des_pings_pas_le_jeu(double saut, double internet)
    {
        // 25/09/2026 : la cle d'avertissement ICMP n'avait de texte nulle part ; le joueur lisait une
        // gigue sans savoir qu'elle ne vient pas des paquets de son jeu.
        var ligne = await Service(new CompteurFictif(saut, internet)).LigneAsync();

        ligne.Should().Contain("pas les paquets du jeu vers son serveur");
    }
}
