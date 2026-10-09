using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>
/// Un `Kernel-Power 41` n'est pas muet : ses donnees disent s'il y a eu un ecran bleu (code d'arret),
/// un appui long sur le bouton (horodatage du bouton), ou une transition d'alimentation en cours.
/// Doc Microsoft « Event ID 41 The system has rebooted without cleanly shutting down first »,
/// mise a jour du 12/02/2026. Les XML ci-dessous reprennent la forme des evenements reels lus le
/// 16/09/2026 sur la machine de reference.
/// </summary>
public sealed class ArretBrutalTests
{
    private static readonly DateTimeOffset Quand = new(2026, 9, 2, 3, 9, 56, TimeSpan.Zero);

    private static string Xml(string bugcheck, string sleep, string bouton) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData>"
        + $"<Data Name='BugcheckCode'>{bugcheck}</Data>"
        + "<Data Name='BugcheckParameter1'>0x0</Data>"
        + $"<Data Name='SleepInProgress'>{sleep}</Data>"
        + $"<Data Name='PowerButtonTimestamp'>{bouton}</Data>"
        + "</EventData></Event>";

    [Fact]
    public void Un_code_d_arret_non_nul_est_un_ecran_bleu()
    {
        var a = ArretBrutal.DepuisXml(Quand, Xml("340", "0", "0"));

        a.CodeArret.Should().Be(340);
        a.EcranBleu.Should().BeTrue();
        a.BoutonMaintenu.Should().BeFalse();
        a.TransitionEnCours.Should().BeFalse();
    }

    [Fact]
    public void Un_horodatage_du_bouton_non_nul_est_un_appui_long()
    {
        var a = ArretBrutal.DepuisXml(Quand, Xml("0", "6", "134298540023025526"));

        a.BoutonMaintenu.Should().BeTrue();
        a.EcranBleu.Should().BeFalse();
    }

    [Fact]
    public void Tout_a_zero_est_un_arret_sans_code()
    {
        var a = ArretBrutal.DepuisXml(Quand, Xml("0", "0", "0"));

        a.EcranBleu.Should().BeFalse();
        a.BoutonMaintenu.Should().BeFalse();
        a.TransitionEnCours.Should().BeFalse();
    }

    [Theory]
    [InlineData("6", true)]
    [InlineData("0", false)]
    [InlineData("false", false)] // la doc Microsoft montre aussi « false » dans cet exemple
    [InlineData("true", true)]
    public void La_transition_d_alimentation_se_lit_en_nombre_comme_en_booleen(string valeur, bool attendu)
        => ArretBrutal.DepuisXml(Quand, Xml("0", valeur, "0")).TransitionEnCours.Should().Be(attendu);

    [Fact]
    public void Des_donnees_absentes_ne_deviennent_pas_un_ecran_bleu()
    {
        var a = ArretBrutal.DepuisXml(Quand, "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'/>");

        a.EcranBleu.Should().BeFalse();
        a.BoutonMaintenu.Should().BeFalse();
    }

    [Fact]
    public void Un_appui_long_sur_le_bouton_n_est_pas_compte_comme_instabilite()
    {
        var arrets = new[]
        {
            ArretBrutal.DepuisXml(Quand, Xml("340", "0", "0")),
            ArretBrutal.DepuisXml(Quand.AddDays(-1), Xml("0", "6", "134298540023025526")),
            ArretBrutal.DepuisXml(Quand.AddDays(-2), Xml("0", "0", "0")),
        };

        ArretBrutal.HorsBouton(arrets).Should().HaveCount(2);
    }
}
