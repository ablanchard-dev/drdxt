using DrDxt.Application.Autopilot;
using DrDxt.Application.DependencyInjection;
using DrDxt.Core;
using DrDxt.Profiles;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DrDxt.App.Tests;

/// <summary>
/// Le critère d'adoption d'un levier, écrit dans le code. Chaque levier que
/// le bras propose porte (1) une source, dans un profil (`GainEvidence.Source`) ; (2) un retour que la désinstallation et
/// le revert en attente savent rejouer (`RevertAuDemarrage.LeviersRejouables`) ; (3) un mode de mesure déclaré au catalogue
/// des candidats (à chaud = jugé en jeu, au redémarrage = posé sans mesure, réglage = posé et relu). Un levier sans l'un des
/// trois n'entre pas dans le bras : il reste au docteur.
/// </summary>
public sealed class ChaqueLevierPorteSourceRetourEtMesureTests
{
    // Les trois que personne ne propose, documentés ailleurs (AucunLevierMortTests, LeviersJamaisApplicablesTests) :
    // le reflex défait seulement une ancienne écriture ; fréquence et ventilateur de la carte ne vivent qu'en mode Expert.
    private static readonly HashSet<string> HorsBras = ["latency.reflex", "gpu.undervolt", "gpu.fan_curve"];

    // Un retour qui n'est pas une op du helper, avec sa raison : c'est voulu, la garde le sait.
    private static readonly Dictionary<string, string> RetourHorsHelper = new(StringComparer.Ordinal)
    {
        // Écrit sans droits par l'App (user32, profil de l'utilisateur) ; un mode non gardé dans les 15 s revient
        // seul (Windows), un mode non enregistré repart à l'ancien à la session suivante ; son propre RevertAsync rend
        // l'original. Rien n'est à rejouer au démarrage ni à la désinstallation.
        ["fixit.ecran_frequence"] = "mode d'écran, sans helper : revient seul si non confirmé, rendu par son propre levier",
    };

    private static IReadOnlyList<ITweak> VraisLeviers()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IDiagnosticsLog>());
        services.AddSingleton(Substitute.For<IBenchmarkRunner>());
        services.AddDrDxtApplication();
        return services.BuildServiceProvider().GetRequiredService<ITweakCatalog>().All().ToList();
    }

    [Fact]
    public void Chaque_levier_du_bras_porte_une_source_un_retour_et_un_mode_de_mesure()
    {
        var leviers = VraisLeviers().Select(t => t.Id).Where(id => !HorsBras.Contains(id)).ToList();
        leviers.Should().NotBeEmpty();

        var sources = new ChipProfileCatalog().All().SelectMany(p => p.Selections)
            .GroupBy(s => s.TweakId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(s => s.Evidence.Source).ToList(), StringComparer.Ordinal);
        var rejouables = RevertAuDemarrage.LeviersRejouables.ToHashSet(StringComparer.Ordinal);

        leviers.Where(id => !sources.TryGetValue(id, out var src) || src.All(string.IsNullOrWhiteSpace))
            .Should().BeEmpty("un levier sans source dans un profil ne se propose pas : il reste au docteur");
        leviers.Where(id => !rejouables.Contains(id) && !RetourHorsHelper.ContainsKey(id))
            .Should().BeEmpty("un levier dont le retour n'est pas rejouable à la désinstallation ne se pose pas, sauf raison écrite ici");
        RetourHorsHelper.Keys.Should().OnlyContain(id => leviers.Contains(id) && !rejouables.Contains(id),
            "une exception qui ne sert plus doit disparaître");
        leviers.Where(id => CatalogueDeCandidats.Decrire(id) is null)
            .Should().BeEmpty("un levier sans mode de mesure déclaré (à chaud, au redémarrage, réglage) ne se juge pas");
    }

    // La contre-épreuve : un levier fictif sans source ni retour ni mesure serait pris par la garde.
    [Fact]
    public void Un_levier_sans_source_ni_retour_ni_mesure_serait_refuse()
    {
        const string fictif = "fixit.levier_fantome";
        new ChipProfileCatalog().All().SelectMany(p => p.Selections).Should().NotContain(s => s.TweakId == fictif);
        RevertAuDemarrage.LeviersRejouables.Should().NotContain(fictif);
        CatalogueDeCandidats.Decrire(fictif).Should().BeNull();
    }
}
