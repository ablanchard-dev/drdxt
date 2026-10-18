using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

/// <summary>Le journal d'une session (pending-revert.json) illisible, après une coupure ou parce qu'un autre programme du compte
/// l'a écrit. Il peut porter les origines des leviers gardés : écrasé ou supprimé, elles disparaissaient ; il est mis de côté.</summary>
public sealed class RevertEnAttenteIllisibleTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-revert-" + Guid.NewGuid().ToString("N"));
    private string Chemin => Path.Combine(_dossier, "pending-revert.json");

    public RevertEnAttenteIllisibleTests()
    {
        Directory.CreateDirectory(_dossier);
        File.WriteAllText(Chemin, "{ pas du json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch { /* nettoyage au mieux */ }
    }

    private string[] DeCote() => Directory.GetFiles(_dossier, "pending-revert.json.mis-de-cote-*");

    [Fact]
    public void Poser_met_de_cote_au_lieu_d_ecraser()
    {
        new FichierRevertEnAttente(Chemin).Poser("x", new TweakSnapshot("x", "{}")).IsOk.Should().BeTrue();

        DeCote().Should().HaveCount(1);
        File.ReadAllText(DeCote()[0]).Should().Be("{ pas du json");
    }

    [Fact]
    public void Lever_met_de_cote_au_lieu_de_supprimer()
    {
        new FichierRevertEnAttente(Chemin).Lever().IsOk.Should().BeTrue();

        DeCote().Should().HaveCount(1);
        File.Exists(Chemin).Should().BeFalse();
    }

    [Fact]
    public void Garder_met_de_cote_au_lieu_de_bloquer()
    {
        var j = new FichierRevertEnAttente(Chemin);

        j.Garder("x", new TweakSnapshot("x", "{}")).IsOk.Should().BeTrue();

        DeCote().Should().HaveCount(1);
        j.Lire().IsOk.Should().BeTrue();
    }

    // Un fichier qui ne se lit pas du tout (tenu par un autre) n'est pas un contenu illisible : rien n'est écrit par-dessus.
    [Fact]
    public void Un_fichier_tenu_n_est_ni_ecrase_ni_mis_de_cote()
    {
        using (new FileStream(Chemin, FileMode.Open, FileAccess.Read, FileShare.None))
            new FichierRevertEnAttente(Chemin).Poser("x", new TweakSnapshot("x", "{}")).IsOk.Should().BeFalse();

        DeCote().Should().BeEmpty();
        File.ReadAllText(Chemin).Should().Be("{ pas du json");
    }
}
