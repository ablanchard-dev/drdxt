using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Le relevé tourne aussi DrDXT fermé ; l'App ouverte en même temps ne doit pas relever la même partie
// une seconde fois. Un seul relevé tient le verrou ; l'autre attend.
public sealed class VerrouDuReleveTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-verrou-" + Guid.NewGuid().ToString("N"));
    private string Chemin => Path.Combine(_dossier, "releve-parties.verrou");

    public void Dispose()
    {
        try { Directory.Delete(_dossier, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Un_verrou_tenu_ne_se_prend_pas_une_seconde_fois_et_se_rend_a_la_fermeture()
    {
        using (var premier = VerrouDuReleve.Prendre(Chemin))
        {
            premier.Should().NotBeNull();
            VerrouDuReleve.Prendre(Chemin).Should().BeNull();
        }
        using var apres = VerrouDuReleve.Prendre(Chemin);
        apres.Should().NotBeNull();
    }

    [Fact]
    public async Task Sans_le_verrou_la_veille_attend_puis_veille_quand_il_se_libere()
    {
        var essais = new Queue<bool>([false, false, true]);
        var attentes = 0;
        var veilles = 0;

        await VerrouDuReleve.VeillerSeulAsync(
            prendre: () => essais.Dequeue() ? new MemoryStream() : null,
            veiller: _ => { veilles++; return Task.CompletedTask; },
            patienter: (_, _) => { attentes++; return Task.CompletedTask; },
            CancellationToken.None);

        attentes.Should().Be(2);
        veilles.Should().Be(1);
    }

    [Fact]
    public async Task Verrou_jamais_libre_aucune_veille()
    {
        using var fin = new CancellationTokenSource();
        var attentes = 0;
        var veilles = 0;

        await VerrouDuReleve.VeillerSeulAsync(
            prendre: () => null,
            veiller: _ => { veilles++; return Task.CompletedTask; },
            patienter: (_, _) => { if (++attentes == 3) fin.Cancel(); return Task.CompletedTask; },
            fin.Token);

        veilles.Should().Be(0);
    }

    // Le verrou vit dans le dossier du joueur et se supprime à sa fermeture. L'App (élevée parfois) l'ouvrait en suivant un
    // lien symbolique posé d'avance sous son nom : la suppression à la fermeture retirait alors la cible du lien. Ouvert
    // sans suivre de point de reparse, c'est le nom seul qui part.
    [Fact]
    public void Un_lien_symbolique_pose_sous_le_nom_du_verrou_ne_fait_jamais_supprimer_sa_cible()
    {
        Directory.CreateDirectory(_dossier);
        var temoin = Path.Combine(_dossier, "temoin.txt");
        File.WriteAllText(temoin, "intact");
        File.CreateSymbolicLink(Chemin, temoin);

        VerrouDuReleve.Prendre(Chemin)?.Dispose();

        File.ReadAllText(temoin).Should().Be("intact");
    }
}
