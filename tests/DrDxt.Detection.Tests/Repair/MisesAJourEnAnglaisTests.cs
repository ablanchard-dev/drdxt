using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Sur un Windows anglais, le docteur parle anglais : la carte Windows Update en est la première page.
public sealed class MisesAJourEnAnglaisTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Ce_qui_attend_se_dit_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));
        var etat = new EtatWindowsUpdate(
            [new MiseAJourEnAttente("KB5050000", GenreDeMiseAJour.Securite), new MiseAJourEnAttente("NVIDIA 581.57", GenreDeMiseAJour.Pilote)],
            Maintenant.AddDays(-1));

        var f = MisesAJourEnAttente.Juger(etat, Maintenant, fuseau: TimeZoneInfo.Utc);

        f.Explication.Should().Be("2 updates are waiting: 1 security fix, 1 driver (KB5050000; NVIDIA 581.57). "
            + "Last checked by Windows: Oct 11 at 12:00. A security fix closes a known hole: install it outside a game. "
            + "DrDXT installs nothing itself.");
        f.Remede.Should().StartWith("1) Settings > Windows Update > Check for updates.");
    }

    [Fact]
    public void Rien_en_attente_et_la_version_proposee_se_disent_en_anglais()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("en-US"));

        MisesAJourEnAttente.Juger(new EtatWindowsUpdate([], null), Maintenant).ValeurCourante.Should().Be("none");
        MisesAJourEnAttente.JugerProposees([new MiseAJourEnAttente("Windows 11, version 26H1", GenreDeMiseAJour.NouvelleVersion)], "25H2")
            .Single().Explication.Should().StartWith("Windows offers you \"Windows 11, version 26H1\" (you are on 25H2).");
    }
}
