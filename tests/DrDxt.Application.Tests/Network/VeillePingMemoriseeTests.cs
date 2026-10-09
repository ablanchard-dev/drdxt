using DrDxt.Application.Network;
using FluentAssertions;

namespace DrDxt.Application.Tests.Network;

public sealed class VeillePingMemoriseeTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-veille-" + Guid.NewGuid().ToString("N"));
    private string Chemin => Path.Combine(_dossier, "veille-ping.json");

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    [Fact]
    public void Allumee_puis_eteinte()
    {
        var m = new VeillePingMemorisee(Chemin);
        m.EstAllumee().Should().BeFalse("rien d'ecrit : eteinte");

        m.Allumer().IsOk.Should().BeTrue();
        new VeillePingMemorisee(Chemin).EstAllumee().Should().BeTrue("relue par une autre instance, comme au lancement suivant");

        m.Eteindre().IsOk.Should().BeTrue();
        m.EstAllumee().Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("vrai")]
    [InlineData("{\"true\":1}")]
    [InlineData("truee")]
    public void Un_contenu_abime_se_lit_eteinte(string contenu)
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Chemin, contenu);
        new VeillePingMemorisee(Chemin).EstAllumee().Should().BeFalse();
    }
}
