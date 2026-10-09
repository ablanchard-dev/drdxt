using System.Reflection;
using DrDxt.Profiles;
using FluentAssertions;
using Xunit;

namespace DrDxt.Profiles.Tests;

/// <summary>
/// `GainConfidence.Measured` est la classe la plus forte du produit : elle dit au client que le
/// chiffre affiche a ete mesure, pas cite. Elle n'a le droit d'exister que si un instrument peut
/// la produire — et le prouver en echouant quand le gain n'est pas la.
///
/// ══ ce qui a ete mesure le 05/09/2026, et qui ferme cette classe ══
///
/// Les deux instruments du produit ont ete testes contre des controles, et aucun ne peut certifier
/// un gain aujourd'hui :
///   • le rejeu d'une capture en jeu : +10 % de FPS injectes sur une serie reelle ne font pas
///     basculer le verdict — la marge combinee vaut ±30 fps pour un delta de +13,3 ;
///   • le banc deterministe : sigma/moyenne = 72,6 % sur dix passes identiques (14,8 % sur six),
///     alors que le checksum est identique 16/16. Le calcul est juste, c'est le debit qui varie.
///
/// Les deux ont leur ligne dans `GATES.md`, avec leurs causes et ce qui les debloquerait.
///
/// Une seule entree du catalogue se disait `Measured` : l'offset de +75 MHz, adosse au « +2,5 %
/// mesure sur la RTX du projet » du 02/07. Ce chiffre vient du banc dont on vient d'etablir qu'il
/// varie de 15 a 73 % entre passes identiques. Il est donc SOURCE — il cite d'ou il vient — mais
/// il n'est pas mesurable ici et maintenant, et le dire « mesure » au client serait exactement ce
/// que ce produit refuse depuis le 24/08.
///
/// Quand ce test tombe : ce n'est pas lui qu'on corrige. Soit un instrument a ete retabli — alors
/// on retire ce controle DELIBEREMENT, en fermant d'abord les deux lignes GATES ; soit quelqu'un a
/// remis un « mesure » sans instrument derriere, et c'est le catalogue qu'on corrige.
/// </summary>
public sealed class NothingIsSoldAsMeasuredWithoutAnInstrumentTests
{
    private static IReadOnlyList<(string Profile, string Tweak, GainEvidence Evidence)> AllEvidence()
    {
        // Le catalogue reel, celui que le produit compose — pas une liste recopiee ici.
        var profiles = new ChipProfileCatalog().All();

        return profiles
            .SelectMany(p => p.Selections.Select(s => (p.Id, s.TweakId, s.Evidence)))
            .ToList();
    }

    [Fact]
    public void No_catalog_entry_claims_a_gain_it_cannot_measure_today()
    {
        var all = AllEvidence();

        // Sans preuves a examiner, ce controle serait vert pour rien.
        all.Should().NotBeEmpty("le catalogue doit exposer ses profils et leurs preuves");

        all.Where(e => e.Evidence.Confidence == GainConfidence.Measured)
           .Select(e => $"{e.Profile} / {e.Tweak}")
           .Should().BeEmpty(
               "aucun instrument du produit ne peut certifier un gain aujourd'hui : le rejeu ne "
               + "bascule pas sur +10 % injectes, et le banc varie de 15 a 73 % entre passes "
               + "identiques. Une classe « mesure » sans instrument est une promesse");
    }

    [Fact]
    public void Every_claim_still_carries_where_it_comes_from()
    {
        // L'autre bord : retirer « mesure » ne doit pas retirer la PROVENANCE. Un chiffre sans
        // source serait pire que le probleme qu'on vient de corriger.
        foreach (var (profile, tweak, evidence) in AllEvidence())
        {
            evidence.Source.Should().NotBeNullOrWhiteSpace($"{profile} / {tweak} doit citer sa source");
            evidence.Claim.Should().NotBeNullOrWhiteSpace($"{profile} / {tweak} doit dire ce qu'il affirme");
        }
    }

    // 08/10/2026 (A6) : « sourcé » sans source publique, c'est la même promesse en plus discret. La fiche X3D était
    // marquée sourcée avec une note interne jamais publiée ; trois autres ne citaient rien. Une entrée Sourced doit
    // nommer d'où elle sait : une adresse, Microsoft Learn, TechPowerUp, ou l'interface officielle de NVIDIA.
    [Fact]
    public void Rien_n_est_dit_source_sans_source_publique()
        => AllEvidence()
            .Where(e => e.Evidence.Confidence == GainConfidence.Sourced
                && !System.Text.RegularExpressions.Regex.IsMatch(e.Evidence.Source, @"https://|learn\.microsoft\.com|TechPowerUp|interface officielle de NVIDIA"))
            .Select(e => $"{e.Profile} / {e.Tweak}")
            .Should().BeEmpty("une entrée sourcée cite une source qu'on peut ouvrir");

    // 08/10/2026 (chantier V2) : un pourcentage affiché au joueur porte, dans sa source, une adresse qu'il peut
    // ouvrir. La fiche X3D affichait « +17 à +21 %, +27 % Warzone » contre une source qui dit ~6 % ; Reflex « 30 à 57 % »
    // ne citait rien.
    [Fact]
    public void Un_pourcentage_affiche_porte_une_adresse_dans_sa_source()
    {
        var chiffrees = AllEvidence()
            .Where(e => System.Text.RegularExpressions.Regex.IsMatch(e.Evidence.Claim, @"\d+\s*%")).ToList();
        chiffrees.Should().NotBeEmpty("témoin : le crible voit au moins le chiffre X3D");
        chiffrees.Where(e => !e.Evidence.Source.Contains("https://", StringComparison.Ordinal))
            .Select(e => $"{e.Profile} / {e.Tweak} : {e.Evidence.Claim}")
            .Should().BeEmpty("un chiffre montré au joueur se vérifie");
    }
}
