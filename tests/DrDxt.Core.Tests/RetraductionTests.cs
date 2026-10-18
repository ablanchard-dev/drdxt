using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Une machine exportée par un testeur anglophone se relit en français : chaque phrase du docteur reconnue par son gabarit
// anglais est réécrite avec son gabarit français et les mêmes valeurs ; ce qui n'est pas reconnu reste tel quel.
public sealed class RetraductionTests
{
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    private static string Dans(CultureInfo langue, Func<string> texte)
    {
        using var _ = Docteur.Portee(langue);
        return texte();
    }

    [Fact]
    public void Une_phrase_anglaise_du_docteur_se_relit_en_francais_avec_ses_valeurs()
    {
        var anglais = Dans(En, () => Docteur.T("RR.Vide", "7"));
        var francais = Dans(Fr, () => Docteur.T("RR.Vide", "7"));
        anglais.Should().NotBe(francais, "témoin : les deux langues diffèrent");

        Dans(Fr, () => Retraduction.Retraduire(anglais)!).Should().Be(francais);
    }

    [Fact]
    public void Des_phrases_mises_bout_a_bout_se_relisent_chacune()
    {
        var anglais = Dans(En, () => Docteur.T("MS.Pleine") + Docteur.T("MS.Gestes"));
        var francais = Dans(Fr, () => Docteur.T("MS.Pleine") + Docteur.T("MS.Gestes"));

        Dans(Fr, () => Retraduction.Retraduire(anglais)!).Should().Be(francais);
    }

    [Fact]
    public void Un_texte_que_rien_ne_reconnait_reste_tel_quel()
    {
        const string inconnu = "Recorded on a test bench by hand, nothing the doctor wrote.";
        Dans(Fr, () => Retraduction.Retraduire(inconnu)!).Should().Be(inconnu);
        Dans(Fr, () => Retraduction.Retraduire("")!).Should().Be("");
    }

    // Un trou de gabarit qui avale une ancienne phrase de l'autre langue donnerait une phrase à moitié traduite : refusé.
    [Fact]
    public void Un_trou_qui_avale_une_phrase_de_l_autre_langue_ne_compte_pas()
    {
        var gabarit = Dans(En, () => Docteur.T("RR.Vide", "{X}"));
        var ancienne = gabarit.Replace("{X}", "this sentence was written for your old version");
        Dans(Fr, () => Retraduction.Retraduire(ancienne)!).Should().Be(ancienne);
    }

    // Un constat enregistré porte souvent le point final que le gabarit n'a pas : la phrase se relit, le point reste.
    [Fact]
    public void Une_phrase_suivie_de_son_point_final_se_relit()
    {
        var francais = Dans(Fr, () => Docteur.T("RA.TempGpu.NonLueAmd", "AMD Radeon RX 6600")) + ".";
        var anglais = Dans(En, () => Docteur.T("RA.TempGpu.NonLueAmd", "AMD Radeon RX 6600")) + ".";
        anglais.Should().NotBe(francais, "témoin : les deux langues diffèrent");

        Dans(En, () => Retraduction.Retraduire(francais)!).Should().Be(anglais);
    }

    // Un long texte d'incident enregistré (un millier de caractères) prenait plus de 10 s par champ : le scan rejoué en anglais
    // ne finissait jamais. Le découpage est borné : au-delà, le texte reste tel quel. Le témoin a la forme du texte enregistré
    // (début, pilotes, logiciels, puis une phrase que le docteur n'écrit plus, qui empêche le découpage d'aboutir), avec des
    // valeurs inventées : il ne dépend d'aucune machine enregistrée.
    [Fact]
    public async Task Un_long_texte_se_rend_vite_meme_s_il_ne_se_decoupe_pas()
    {
        var long_ = Dans(Fr, () =>
        {
            var quand = new DateTime(2026, 1, 5, 20, 47, 0);
            var pilotes = string.Join(Docteur.T("AI.Separateur"),
                $"pilote graphique 1.2.3.4, {Docteur.T("AI.Installe", quand.AddDays(-2), Docteur.T("AI.Jours", 2))}",
                $"pilote audio 5.6.7.8, {Docteur.T("AI.Installe", quand.AddDays(-2), Docteur.T("AI.Jours", 2))}");
            var logiciels = string.Join(Docteur.T("AI.Separateur"), Enumerable.Range(1, 8)
                .Select(n => $"{Docteur.T("AI.LogicielVersion", $"Outil {n}", $"{n}.0.{n}")}, {Docteur.T("AI.LeJour", quand.AddDays(-2))}"));
            return Docteur.T("AI.Debut", "Arrêt brutal pendant une mise en veille", quand, 3, pilotes)
                + Docteur.T("AI.Logiciels", logiciels)
                + "C'est un rapprochement de dates, pas une preuve : si l'incident revient, c'est la première piste (revenir à la "
                + "version précédente, ou désinstaller pour voir).";
        });
        long_.Length.Should().BeGreaterThan(900, "témoin : un texte d'incident aussi long que celui qui figeait le rejeu");

        // Les gabarits (des milliers d'expressions compilées) se construisent au premier appel, hors du budget du découpage :
        // sous la charge de la suite complète, leur construction seule dépassait la fenêtre. Seul le découpage se chronomètre.
        Dans(En, () => Retraduction.Retraduire("x")!);
        var lecture = Task.Run(() => Dans(En, () => Retraduction.Retraduire(long_)!));

        (await Task.WhenAny(lecture, Task.Delay(TimeSpan.FromSeconds(3))) == lecture)
            .Should().BeTrue("le découpage est borné : au-delà, le texte reste tel quel");
        (await lecture).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Une_phrase_francaise_se_relit_en_anglais()
    {
        var francais = Dans(Fr, () => Docteur.T("MS.Pleine"));
        Dans(En, () => Retraduction.Retraduire(francais)!).Should().Be(Dans(En, () => Docteur.T("MS.Pleine")));
    }
}
