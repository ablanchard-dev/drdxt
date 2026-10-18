using System.Text.Json;
using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Option de développement : les temps de trame de chaque fenêtre d'une partie, pour mesurer sur de vraies parties les deux
// estimateurs du 1 % low (toutes les images, ou médiane par fenêtre). Désactivée par défaut, jamais dans une version publiée.
public sealed class ImagesDesPartiesTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-images-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private static readonly DateTimeOffset T0 = new(2026, 10, 18, 21, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Chaque_fenetre_garde_ses_images_et_dit_si_le_releve_la_compte_jouee()
    {
        var images = Enumerable.Repeat(7.0, ReleveDesParties.ImagesMinimum).ToList();
        CaptureEnPartie Fenetre(int minute, double premierPlan) => new(T0.AddMinutes(minute), 140, 100, BottleneckState.CpuBoundOrAppBound,
            ImagesMs: images, PremierPlan: premierPlan);
        var captures = new[] { Fenetre(1, 1), Fenetre(4, 1), Fenetre(7, 0) };
        var partie = ReleveDesParties.Resumer("cod.exe", captures)!;

        var chemin = ImagesDesParties.Ecrire(_dossier, partie, captures);

        using var lu = JsonDocument.Parse(File.ReadAllText(chemin));
        lu.RootElement.GetProperty("Jeu").GetString().Should().Be("cod.exe");
        var fenetres = lu.RootElement.GetProperty("Fenetres").EnumerateArray().ToList();
        fenetres.Select(f => f.GetProperty("Jouee").GetBoolean()).Should().Equal(true, true, false);
        fenetres[0].GetProperty("ImagesMs").GetArrayLength().Should().Be(ReleveDesParties.ImagesMinimum);
        Path.GetDirectoryName(chemin).Should().Be(_dossier);
    }

    [Theory]
    [InlineData(false, @"C:\jetable", false)]
    [InlineData(true, null, false)]
    [InlineData(true, @"C:\jetable", true)]
    public void L_option_n_existe_qu_en_build_de_developpement_avec_le_dossier_jetable(bool buildDeDeveloppement, string? dossierDev,
        bool activee)
        => ImagesDesParties.Activee(buildDeDeveloppement, v => v == DossierDonnees.VariableDeDeveloppement ? dossierDev : null)
            .Should().Be(activee);
}
