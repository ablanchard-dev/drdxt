using DrDxt.Core;
using DrDxt.Measurement.Cpu;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Cpu;

/// <summary>
/// Ce que le processeur rend a un jeu, et les trois refus qui comptent plus que le chemin nominal.
///
/// Une reference d'une autre machine est ignoree — un disque voyage. Une PANNE de lecture ne
/// declenche pas une remesure, parce que la suite ecrit et ecraserait la seule mesure d'avant que
/// le client possede. Et un deficit sous le bruit se dit comme une borne, jamais comme une absence.
/// </summary>
public class CpuBaselineServiceTests
{
    private sealed class FichierFictif(string? contenu = null, bool lectureEchoue = false) : ICpuBaselineFile
    {
        public string? Ecrit { get; private set; }

        public Result WriteAtomic(string json) { Ecrit = json; return Result.Ok(); }

        public Result<string?> Read()
            => lectureEchoue
                ? Result<string?>.Fail(ErrorCodes.IoFailure, "fichier verrouille")
                : Result<string?>.Ok(contenu);
    }

    private sealed class BancFictif(CpuBaseline? rendu) : ICpuSustainedBench
    {
        public int Appels { get; private set; }

        public CpuBaseline? Mesurer(string machineId)
        {
            Appels++;
            return rendu;
        }
    }

    private static CpuBaseline Reference(string machine, double rafale, double soutenu, double bruit = 0.5)
        => new(machine, DateTimeOffset.UtcNow, 8, rafale, soutenu, bruit);

    private static string Json(CpuBaseline b)
        => System.Text.Json.JsonSerializer.Serialize(b);

    [Fact]
    public void Le_premier_scan_mesure_puis_ENREGISTRE()
    {
        var fichier = new FichierFictif();
        var banc = new BancFictif(Reference("m1", 1817, 1615));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1");

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().NotBeNull("sans enregistrement, chaque scan rechaufferait la machine");
        deficit!.DeficitPercent.Should().BeApproximately(11.1, 0.2);
        deficit.Demontrable.Should().BeTrue();
        deficit.Lisible.Should().Contain("charge longue").And.NotContain("que ton jeu obtient",
            "la mesure charge tous les cœurs ; un jeu non, la phrase ne doit pas l'égaliser");
    }

    // 29/09/2026 (boucle « vivant et prouvé », A2) : changer la limite de puissance au BIOS ne changeait rien à
    // l'écran, la référence gardait l'ancienne mesure. Quand le réglage lu diffère de celui noté avec la référence,
    // le scan remesure seul (jeu fermé) et dit l'avant et l'après.
    private static CpuBaseline AvecPuissance(double rafale, double soutenu, double watts, string? reglage)
        => new("m1", DateTimeOffset.UtcNow.AddDays(-2), 8, rafale, soutenu, 0.5, watts, 1.5, true, reglage,
            FrequenceUnCoeurMhz: 5300, FrequenceSoutenueMhz: 5000);

    // Revue R1 (03/10) : une référence d'avant le relevé des fréquences ne montrait jamais la limite atteinte dès le début,
    // même sur la machine qui l'avait. Elle se remesure une fois, sans parler d'un réglage changé.
    [Fact]
    public void Une_reference_sans_frequences_d_avant_le_banc_qui_les_releve_se_remesure_une_fois()
    {
        var ancienne = new CpuBaseline("m1", CpuBaselineService.BancAvecFrequence.AddDays(-3), 8, 1391, 1533, 1.8, 66, 3.9, true,
            "Intel Default Settings");
        var fichier = new FichierFictif(Json(ancienne));
        var banc = new BancFictif(AvecPuissance(1695, 1343, 68, null));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: false, reglageBios: "Intel Default Settings");

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().Contain("FrequenceUnCoeurMhz").And.Contain("Intel Default Settings");
        deficit!.Lisible.Should().NotContain("Réglage du BIOS changé", "le réglage n'a pas changé");
    }

    [Fact]
    public void Une_reference_sans_frequences_prise_apres_ne_relance_jamais_le_banc()
    {
        // Machine dont les compteurs de fréquence manquent : sa référence n'en a jamais, et ne doit pas tout remesurer.
        var recente = new CpuBaseline("m1", CpuBaselineService.BancAvecFrequence.AddHours(1), 8, 1391, 1533, 1.8, 66, 3.9, true,
            "Intel Default Settings");
        var banc = new BancFictif(AvecPuissance(1695, 1343, 68, null));

        new CpuBaselineService(banc, new CpuBaselineStore(new FichierFictif(Json(recente))))
            .DeficitPourScan("m1", jeuEnCours: false, reglageBios: "Intel Default Settings");

        banc.Appels.Should().Be(0);
    }

    [Fact]
    public void Un_reglage_BIOS_change_remesure_et_dit_l_avant_et_l_apres()
    {
        var fichier = new FichierFictif(Json(AvecPuissance(1720, 1385, 68, "Intel Default Settings")));
        var banc = new BancFictif(AvecPuissance(1720, 1650, 110, null));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: false, reglageBios: "MSI Performance Settings");

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().Contain("MSI Performance Settings", "la nouvelle référence note le réglage sous lequel elle est prise");
        deficit!.Lisible.Should().Contain("Intel Default Settings").And.Contain("MSI Performance Settings")
            .And.Contain("68 W").And.Contain("110 W");
    }

    [Fact]
    public void Le_meme_reglage_ne_relance_pas_le_banc()
    {
        var fichier = new FichierFictif(Json(AvecPuissance(1720, 1385, 68, "Intel Default Settings")));
        var banc = new BancFictif(AvecPuissance(1720, 1650, 110, null));

        new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: false, reglageBios: "Intel Default Settings");

        banc.Appels.Should().Be(0, "témoin : rien n'a changé, pas quarante secondes de charge");
    }

    [Fact]
    public void Jeu_lance_un_reglage_change_ne_relance_pas_le_banc()
    {
        var fichier = new FichierFictif(Json(AvecPuissance(1720, 1385, 68, "Intel Default Settings")));
        var banc = new BancFictif(AvecPuissance(1720, 1650, 110, null));

        new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: true, reglageBios: "MSI Performance Settings").Should().NotBeNull();

        banc.Appels.Should().Be(0, "le joueur paierait la mesure en images ; le scan suivant, jeu fermé, la fera");
    }

    [Fact]
    public void Une_reference_d_avant_la_mesure_de_puissance_se_remesure_une_fois()
    {
        var ancienne = new CpuBaseline("m1", DateTimeOffset.UtcNow.AddDays(-18), 8, 817, 572, 7.4);
        var fichier = new FichierFictif(Json(ancienne));
        var banc = new BancFictif(AvecPuissance(1720, 1385, 68, null));

        new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: false, reglageBios: "Intel Default Settings");

        banc.Appels.Should().Be(1);
        fichier.Ecrit.Should().Contain("Intel Default Settings");
    }

    [Fact]
    public void Jeu_lance_sans_reference_le_banc_ne_tourne_pas()
    {
        var fichier = new FichierFictif();
        var banc = new BancFictif(Reference("m1", 1817, 1615));

        new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1", jeuEnCours: true).Should().BeNull();

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull();
    }

    [Fact]
    public void Le_scan_suivant_RELIT_au_lieu_de_rechauffer_la_machine()
    {
        var fichier = new FichierFictif(Json(Reference("m1", 1817, 1615)));
        var banc = new BancFictif(null);

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1");

        banc.Appels.Should().Be(0, "quarante secondes de charge complete a chaque ouverture du "
                                   + "produit, pour un chiffre qui ne bouge pas");
        deficit!.DeficitPercent.Should().BeApproximately(11.1, 0.2);
    }

    [Fact]
    public void Une_reference_d_une_AUTRE_machine_est_ignoree_et_declenche_une_mesure()
    {
        var fichier = new FichierFictif(Json(Reference("autre-machine", 1817, 1615)));
        var banc = new BancFictif(Reference("m1", 1800, 1790));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1");

        banc.Appels.Should().Be(1, "un disque voyage : comparer deux machines n'a aucun sens");
        deficit!.Demontrable.Should().BeFalse();
    }

    [Fact]
    public void Une_PANNE_de_lecture_ne_declenche_PAS_une_remesure()
    {
        var fichier = new FichierFictif(lectureEchoue: true);
        var banc = new BancFictif(Reference("m1", 1817, 1615));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .DeficitPourScan("m1");

        banc.Appels.Should().Be(0);
        fichier.Ecrit.Should().BeNull("la suite ECRIT : ecraser une reference parfaite a cause d'un "
                                      + "antivirus qui tenait le fichier detruirait la seule mesure "
                                      + "d'avant que le client possede");
        deficit.Should().BeNull("ne pas savoir se dit, ca ne s'invente pas");
    }

    [Fact]
    public void Un_deficit_sous_le_bruit_se_dit_comme_une_BORNE()
    {
        var banc = new BancFictif(Reference("m1", 1800, 1795, bruit: 0.5));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(new FichierFictif()))
            .DeficitPourScan("m1");

        deficit!.Demontrable.Should().BeFalse();
        deficit.Lisible.Should().Contain("la précision de cette mesure");
    }

    // -------------------------------------------------------------------
    // La fixture qui trichait — 11/09/2026.
    //
    // Les tests ci-dessus alimentent le fichier avec `Json(Reference(...))`, c'est-a-dire une
    // chaine que LE TEST serialise lui-meme. Et `FichierFictif.Read()` rend le `contenu` du
    // constructeur, jamais le `Ecrit` que le produit vient de poser : dans tout ce fichier,
    // ce que `CpuBaselineStore.Save` ecrit n'est relu par personne. Le seul test qui ecrit
    // (`Le_premier_scan_...`) se contente de verifier que `Ecrit` n'est pas nul.
    //
    // Donc deux garanties etaient NOMMEES sans etre prouvees : « le scan suivant relit au lieu
    // de rechauffer la machine » et « une reference d'une AUTRE machine est ignoree ». Si
    // `Save` ecrivait une forme que `Read` ne sait pas reparser, ou perdait l'identifiant de
    // machine, les six tests restaient VERTS — et en production le client se reprenait
    // quarante secondes de charge complete a chaque ouverture du produit.
    //
    // Le jumeau memoire, lui, le fait correctement depuis le depart
    // (`MemoryBaselineStoreTests.Une_reference_ecrite_se_relit_a_l_identique`) : son faux
    // fichier rend ce qu'on lui a ecrit. Ce test remet le CPU au meme niveau — c'est le
    // Produit qui ecrit, et le produit qui relit.
    // -------------------------------------------------------------------
    private sealed class FichierQuiGarde : ICpuBaselineFile
    {
        private string? _contenu;
        public Result WriteAtomic(string json) { _contenu = json; return Result.Ok(); }
        public Result<string?> Read() => Result<string?>.Ok(_contenu);
    }

    [Fact]
    public void Ce_que_le_produit_ECRIT_est_bien_ce_que_le_produit_RELIT()
    {
        var fichier = new FichierQuiGarde();
        var banc = new BancFictif(Reference("m1", 1817, 1615));

        // Premier scan : il mesure et il enregistre. Rien n'est pose a la main.
        var premier = new CpuBaselineService(banc, new CpuBaselineStore(fichier)).DeficitPourScan("m1");
        // Second scan sur le meme fichier : il ne doit que relire.
        var second = new CpuBaselineService(banc, new CpuBaselineStore(fichier)).DeficitPourScan("m1");

        banc.Appels.Should().Be(1,
            "le second scan doit relire ce que le PREMIER a ecrit, pas rechauffer la machine");
        second!.DeficitPercent.Should().BeApproximately(premier!.DeficitPercent, 0.001);

        var relue = new CpuBaselineStore(fichier).Read("m1");
        relue.Value.Should().NotBeNull();
        relue.Value!.MachineId.Should().Be("m1",
            "sans identifiant survivant a l'aller-retour, le refus d'une AUTRE machine ne peut pas marcher");
        relue.Value.Threads.Should().Be(8, "le deficit n'a de sens qu'a nombre de cœurs egal");
        new CpuBaselineStore(fichier).Read("autre-machine").Value.Should().BeNull();
    }

    [Fact]
    public void Un_banc_qui_ne_peut_pas_tourner_ne_casse_rien_et_ne_rend_rien()
    {
        var deficit = new CpuBaselineService(new BancFictif(null), new CpuBaselineStore(new FichierFictif()))
            .DeficitPourScan("m1");

        deficit.Should().BeNull("mesurer le processeur ne doit jamais etre la raison pour laquelle "
                                + "un scan s'arrete");
    }

    // 04/10/2026 : vu sur la machine de référence après « MSI Performance Settings » au BIOS. La remesure au repos a refait le banc
    // (62 ⇒ 128 W) et l'écran disait seulement « garde sa puissance » : le gain du geste n'était écrit nulle part.
    [Fact]
    public void Remesurer_apres_un_reglage_BIOS_change_dit_l_avant_et_l_apres()
    {
        var fichier = new FichierFictif(Json(AvecPuissance(1720, 1385, 62, "Intel Default Settings")));
        var banc = new BancFictif(AvecPuissance(1720, 1700, 128, null));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .Remesurer("m1", reglageBios: "MSI Performance Settings");

        deficit!.Lisible.Should().StartWith("Réglage du BIOS changé")
            .And.Contain("Intel Default Settings").And.Contain("62 W").And.Contain("128 W");
    }

    [Fact]
    public void Remesurer_sous_le_meme_reglage_ne_parle_pas_de_BIOS()
    {
        var fichier = new FichierFictif(Json(AvecPuissance(1720, 1385, 62, "Intel Default Settings")));
        var banc = new BancFictif(AvecPuissance(1720, 1390, 63, null));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier))
            .Remesurer("m1", reglageBios: "Intel Default Settings");

        deficit!.Lisible.Should().NotContain("Réglage du BIOS changé");
    }

    // ── 25/09/2026 : « a la demande ensuite » ────────────────────────────────────────────────
    [Fact]
    public void Remesurer_mesure_meme_quand_une_reference_existe_et_la_remplace()
    {
        var fichier = new FichierFictif(Json(Reference("m1", 1800, 1790)));
        var banc = new BancFictif(Reference("m1", 1790, 1606));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier)).Remesurer("m1");

        banc.Appels.Should().Be(1, "c'est le joueur qui l'a demande : on ne relit pas l'ancienne");
        fichier.Ecrit.Should().NotBeNull();
        deficit!.Demontrable.Should().BeTrue();
    }

    [Fact]
    public void Une_remesure_qui_echoue_garde_l_ancienne_reference()
    {
        var fichier = new FichierFictif(Json(Reference("m1", 1800, 1790)));
        var deficit = new CpuBaselineService(new BancFictif(null), new CpuBaselineStore(fichier)).Remesurer("m1");

        deficit.Should().BeNull();
        fichier.Ecrit.Should().BeNull("une mesure ratee n'efface pas la seule mesure d'avant");
    }

    [Fact]
    public void Une_remesure_trop_bruitee_se_dit_mais_garde_l_ancienne_reference()
    {
        // Remesure réelle du 25/09/2026 à 18:43, faite sous une autre charge : ±7,4 %, aucune baisse démontrable.
        var fichier = new FichierFictif(Json(Reference("m1", 1800, 1260)));
        var banc = new BancFictif(Reference("m1", 1000, 1075, bruit: 7.4));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier)).Remesurer("m1");

        fichier.Ecrit.Should().BeNull("une mesure qui ne conclut rien ne remplace pas une référence qui concluait");
        deficit!.Lisible.Should().Contain("trop varié").And.Contain("Ta mesure précédente est gardée.");
        deficit.Lisible.Should().EndWith($"Mesuré le {DateTimeOffset.UtcNow.ToLocalTime():dd/MM/yyyy}.");
    }

    // 27/09/2026 : la référence de la machine de référence (11/09, ±7,4 %) a été prise pendant une partie. La garder
    // contre une remesure bruitée mais faite jeu fermé garderait un chiffre qu'on sait faux.
    [Fact]
    public void Une_remesure_bruitee_remplace_une_reference_suspecte()
    {
        var fichier = new FichierFictif(Json(Reference("m1", 1800, 1260)));
        var banc = new BancFictif(Reference("m1", 1000, 1075, bruit: 7.4));

        var deficit = new CpuBaselineService(banc, new CpuBaselineStore(fichier)).Remesurer("m1", ancienneSuspecte: true);

        fichier.Ecrit.Should().NotBeNull();
        deficit!.Lisible.Should().Contain("trop varié").And.NotContain("gardée");
    }
}
