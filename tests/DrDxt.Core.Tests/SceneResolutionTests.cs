using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// A/a avant a/B — connaitre la sensibilite de la scene avant de tester un levier.
///
/// DrDXT teste des leviers sans savoir ce que la scene peut voir. Un +1 % teste pendant vingt
/// minutes sur une scene qui resout +/-5 % est vingt minutes perdues d'avance — et le verdict
/// « pas de gain » qui en sort est faux : il n'y avait pas de gain DETECTABLE, ce qui n'est pas
/// pareil.
///
/// La resolution n'est pas une constante du produit. Elle depend du jeu, de la scene, de la
/// machine, de la temperature, de la duree, de la phase de partie et de la charge de fond.
/// Elle se mesure, sur place, en comparant des passes du meme etat.
///
/// Et il faut separer deux choses que la moyenne confond. Mesure du 01/09/2026, machine
/// inchangee, aucun reglage touche : **136 → 148 FPS en dix minutes**. Ce n'est pas du bruit, c'est
/// une DERIVE — et « on ne moyenne pas une derive ». Une scene qui derive plus qu'elle ne bruite
/// transformera le TEMPS en effet du tweak, quel que soit le soin mis a la mesure.
/// </summary>
public class SceneResolutionTests
{
    [Fact]
    public void Une_scene_STABLE_annonce_une_resolution_fine()
    {
        // Cinq passes serrees autour de 200 fps : le bruit est faible, la scene voit petit.
        var r = SceneResolution.Probe(new[] { 200.0, 200.5, 199.6, 200.2, 199.9 });

        r.Verdict.Should().Be(SceneVerdict.Usable);
        r.ResolutionPct.Should().BeLessThan(1.0, "une scene aussi stable doit resoudre le pourcent");
        r.ResolutionPct.Should().BeGreaterThan(0);
    }

    [Fact]
    public void Une_scene_BRUYANTE_annonce_une_resolution_grossiere()
    {
        // Le rejeu in-game reel : sigma/base 6,46 % mesure sur 28 paires le 06/09.
        var r = SceneResolution.Probe(new[] { 137.0, 148.0, 131.0, 145.0, 139.0 });

        r.Verdict.Should().Be(SceneVerdict.Usable);
        r.ResolutionPct.Should().BeGreaterThan(3.0,
            "il faut que le produit SACHE qu'un micro-gain y est indetectable");
    }

    [Fact]
    public void Une_scene_qui_DERIVE_est_signalee_comme_telle()
    {
        // Cas mesure le 01/09 : 136 → 148 en dix minutes, machine inchangee. Monotone, faible
        // dispersion autour de la pente : un A/B naif attribuerait douze FPS au tweak.
        var r = SceneResolution.Probe(new[] { 136.0, 139.0, 142.0, 145.0, 148.0 });

        r.Verdict.Should().Be(SceneVerdict.DriftDominant);
        r.DriftPctPerRun.Should().BeGreaterThan(1.0, "la pente doit etre visible, pas noyee");
    }

    [Fact]
    public void Une_derive_DESCENDANTE_compte_autant_qu_une_montante()
    {
        // Le throttling thermique descend. C'est la meme maladie, dans l'autre sens.
        var r = SceneResolution.Probe(new[] { 148.0, 145.0, 142.0, 139.0, 136.0 });

        r.Verdict.Should().Be(SceneVerdict.DriftDominant);
        r.DriftPctPerRun.Should().BeLessThan(-1.0);
    }

    [Fact]
    public void Sous_TROIS_passes_aucune_resolution_n_est_annoncee()
    {
        // Meme borne que `InterRunMarginOfError`. Deux points ne disent rien d'une dispersion,
        // et une pente sur deux points est une droite parfaite : elle « prouverait » toujours.
        var r = SceneResolution.Probe(new[] { 200.0, 210.0 });

        r.Verdict.Should().Be(SceneVerdict.TooFewRuns);
        double.IsFinite(r.ResolutionPct).Should().BeFalse("inconnue, pas zero");
    }

    [Fact]
    public void Une_scene_utilisable_dit_si_un_gain_donne_y_est_DETECTABLE()
    {
        // La question que le moteur doit poser avant de depenser vingt minutes.
        var scene = SceneResolution.Probe(new[] { 137.0, 148.0, 131.0, 145.0, 139.0 });

        scene.PeutDetecter(1.0).Should().BeFalse("un +1 % est sous le bruit de cette scene");
        scene.PeutDetecter(25.0).Should().BeTrue();
    }

    [Fact]
    public void Une_scene_QUI_DERIVE_ne_peut_rien_detecter_du_tout()
    {
        // Meme un gros gain : on ne saurait pas s'il vient du reglage ou des six minutes ecoulees.
        var scene = SceneResolution.Probe(new[] { 136.0, 139.0, 142.0, 145.0, 148.0 });

        scene.PeutDetecter(50.0).Should().BeFalse(
            "tant que la scene derive, un A/B simple attribue le temps au tweak");
    }

    // ══ les trois series CI-dessous sont reelles ══════════════════════════════════════════════
    // Elles viennent des captures conservees dans `tools/p3` (Warzone, 01/09/2026, i7-14700F /
    // RTX 4060), decoupees en tranches d'une minute. Un controle synthetique prouve qu'un calcul
    // est juste ; seules de vraies donnees prouvent qu'il sert a quelque chose.

    [Fact]
    public void REEL_la_derive_de_partie_du_01_09_est_reconnue_comme_telle()
    {
        // Capture de 23h37 : 135,5 → 160,6 FPS en cinq minutes, machine inchangee, aucun reglage
        // touche. C'est exactement le piege que le produit doit voir venir — un A/B lance sur cette
        // scene aurait credite +18 % au premier levier essaye.
        var scene = SceneResolution.Probe(new[] { 135.5, 137.5, 145.8, 156.0, 160.6 });

        scene.Verdict.Should().Be(SceneVerdict.DriftDominant);
        scene.DriftPctPerRun.Should().BeGreaterThan(3.0);
        scene.PeutDetecter(18.0).Should().BeFalse(
            "meme le gain que la derive imite ne serait pas attribuable au reglage");
    }

    [Fact]
    public void REEL_une_partie_qui_oscille_reste_UTILISABLE_mais_peu_sensible()
    {
        // Capture de 22h10 : ca descend puis ca remonte puis ca s'effondre, sans pente franche.
        // Derive cumulee 6,70 % contre une resolution de 7,44 % — le bruit domine, tout juste. Le
        // produit accepte de mesurer ici, en sachant qu'il n'y verra pas un micro-gain.
        var scene = SceneResolution.Probe(new[] { 145.3, 142.1, 137.5, 149.1, 130.0 });

        scene.Verdict.Should().Be(SceneVerdict.Usable);
        scene.PeutDetecter(1.0).Should().BeFalse("+1 % est tres loin sous le bruit de cette scene");
        scene.ResolutionPct.Should().BeInRange(3.0, 12.0);
    }

    [Fact]
    public void REEL_deux_captures_sur_trois_DERIVENT_plus_qu_elles_ne_bruitent()
    {
        // Le fait produit de ce lot, et il est mesure, pas suppose. Sur les trois seules captures
        // assez longues pour etre tranchees, deux ont une derive qui depasse leur propre bruit :
        //
        //   22h07 (9 min) : derive cumulee 4,93 %  contre  resolution 1,83 %  → DERIVE
        //   22h10 (5 min) : derive cumulee 6,70 %  contre  resolution 7,44 %  → utilisable
        //   23h37 (5 min) : derive cumulee 18,68 % contre  resolution 2,36 %  → DERIVE
        //
        // Autrement dit : sur cette machine, en partie reelle, un A/B en AAA→BBB attribue le plus
        // souvent au reglage ce qui n'est que le temps ecoule. C'est ce qui rend le protocole
        // apparie (a-b-a-b) necessaire, et pas seulement elegant.
        var qui = new (string Nom, double[] Tranches)[]
        {
            ("22h07", new[] { 136.5, 134.5, 135.6, 136.1, 142.2, 142.0, 145.2, 142.2, 137.3 }),
            ("22h10", new[] { 145.3, 142.1, 137.5, 149.1, 130.0 }),
            ("23h37", new[] { 135.5, 137.5, 145.8, 156.0, 160.6 }),
        };

        var derivantes = qui.Where(x => SceneResolution.Probe(x.Tranches).Verdict == SceneVerdict.DriftDominant)
            .Select(x => x.Nom).ToList();

        derivantes.Should().BeEquivalentTo(new[] { "22h07", "23h37" });
    }

    [Fact]
    public void REEL_aucune_des_captures_conservees_ne_permet_de_prouver_un_micro_gain()
    {
        // Le constat qui justifie tout ce lot. Les trois seules captures assez longues pour etre
        // tranchees viennent de la meme session, et aucune ne peut trancher un +2 % — l'ordre de
        // grandeur d'un overclock GPU. Le produit doit le dire avant de faire jouer vingt minutes.
        var scenes = new[]
        {
            SceneResolution.Probe(new[] { 136.5, 134.5, 135.6, 136.1, 142.2, 142.0, 145.2, 142.2, 137.3 }),
            SceneResolution.Probe(new[] { 145.3, 142.1, 137.5, 149.1, 130.0 }),
            SceneResolution.Probe(new[] { 135.5, 137.5, 145.8, 156.0, 160.6 }),
        };

        scenes.Should().OnlyContain(s => !s.PeutDetecter(2.0));
    }

    [Fact]
    public void Les_passes_INEXPLOITABLES_sont_ecartees_sans_fausser_la_pente()
    {
        // Une passe a zero n'est pas une mesure basse : c'est une mesure absente. La compter
        // fabriquerait une derive descendante spectaculaire.
        var r = SceneResolution.Probe(new[] { 200.0, 0.0, 200.5, 199.6, 200.2, double.NaN, 199.9 });

        r.Runs.Should().Be(5);
        r.Verdict.Should().Be(SceneVerdict.Usable);
    }
}
