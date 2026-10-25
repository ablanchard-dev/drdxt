using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

/// <summary>Les constats écrits dans Core suivent la langue de l'App, comme ceux de Detection.</summary>
public class ConstatsDeCoreEnAnglaisTests
{
    [Fact]
    public void Puissance_memoire_taches_et_connexion_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        LimiteDePuissance.Juger(null).Explication.Should().StartWith("the processor's power limit could not be read");
        MemoireSaturee.Juger(null).Explication.Should().Be("memory pressure could not be read.");
        TachesEnPartie.Juger([], []).Explication.Should().StartWith("no game recorded");
        ConnexionDeJeu.Juger(null).Explication.Should().StartWith("network adapters could not be read");
    }

    [Fact]
    public void Heures_defender_echange_et_jeux_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        HeuresActives.Texte(17, 11).Should().Be("17:00 to 11:00");
        AnalysesDefender.Juger(null, [], true, 30).Explication.Should().StartWith("the Microsoft Defender log could not be read");
        FichierDEchangeSurDisqueDur.Juger([], true).Explication.Should().Be("Windows reports no page file in use.");
        JeuxSurDisqueDur.Juger([]).Explication.Should().StartWith("no game found in Steam");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Ventilateurs_et_droits_de_dossier_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        // Le nom suit « fan » dans les phrases anglaises : « fan no. 0 (CPU_FAN, processor) stopped ».
        VentilateursEnPartie.Nom(0).Should().Be("no. 0 (CPU_FAN, processor)");
        VentilateursEnPartie.Nommer(new VentilateurEnPartie(3, 900, 0, 12, 600, "CPU Fan")).Should().Be("\"CPU Fan\"");
        CheminProtege.Verifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")).Should().Be("the executable does not exist");
    }

    [Fact]
    public void Bride_du_gpu_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        GpuLimiter.Phrase(GpuLimiterObserved.GpuSature).Should().Be("the GPU was busy most of each frame");
    }

    [Fact]
    public void Le_bilan_du_tri_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        RepairTriage.Trier([new RepairFinding("CPU", "x", null, null, RepairStatus.Unknown, "y")]).Phrase
            .Should().Be("no state could be checked, but 1 could not be read");
    }

    // Une langue que le docteur ne parle pas retombe sur l'anglais en entier : texte, dates et nombres.
    [Theory]
    [InlineData("de-DE", "en-US")]
    [InlineData("fr-CA", "fr-CA")]
    [InlineData("en-GB", "en-GB")]
    public void Une_langue_non_traduite_revient_a_l_anglais(string windows, string attendue)
        => Docteur.Parlee(CultureInfo.GetCultureInfo(windows)).Name.Should().Be(attendue);

    [Fact]
    public void En_francais_rien_ne_change()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));

        MemoireSaturee.Juger(null).Explication.Should().Be("la pression mémoire n'a pas pu être lue.");
    }
}
