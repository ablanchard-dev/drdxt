using DrDxt.Application.Tweaks.Hardware;
using FluentAssertions;

namespace DrDxt.Application.Tests.Tweaks;

/// <summary>
/// Le jeu que visent les leviers au profil d'un jeu. Automatique : le jeu ouvert. Un jeu choisi : son profil seulement, par le
/// chemin complet appris en le voyant tourner, et jamais le profil d'un autre jeu ouvert.
/// </summary>
public sealed class JeuAOptimiserTests : IDisposable
{
    private const string CheminA = @"C:\Jeux\JeuA\JeuA.exe";
    private const string CheminB = @"C:\Jeux\JeuB\JeuB.exe";

    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-jeu-choisi-").FullName;
    public void Dispose() => Directory.Delete(_dossier, true);

    private sealed class EnCours : IGameExecutable
    {
        public string? Chemin;
        public string? RunningGameExecutablePath() => Chemin;
        public IReadOnlyList<string>? ModulesDuJeu() => Chemin is null ? null : ["d3d11.dll"];
    }

    private readonly EnCours _enCours = new();
    private readonly HashSet<string> _existants = new(StringComparer.OrdinalIgnoreCase) { CheminA, CheminB };
    private MemoireDuJeuChoisi Memoire => new(Path.Combine(_dossier, "jeu-choisi.json"));
    private JeuAOptimiser Jeu() => new(_enCours, Memoire, _existants.Contains);

    [Fact]
    public void Automatique_vise_le_jeu_ouvert_comme_avant()
    {
        _enCours.Chemin = CheminB;

        Jeu().RunningGameExecutablePath().Should().Be(CheminB);
        Jeu().ModulesDuJeu().Should().Equal("d3d11.dll");
        Jeu().Etat().Should().Be(EtatDuJeuChoisi.Automatique);
    }

    [Fact]
    public void Un_jeu_choisi_qui_n_est_pas_ouvert_ne_vise_jamais_le_profil_du_jeu_qui_tourne()
    {
        Memoire.Choisir(new JeuChoisi("JeuA.exe", CheminA));
        _enCours.Chemin = CheminB;

        Jeu().RunningGameExecutablePath().Should().Be(CheminA, "le profil du jeu choisi, pas celui de JeuB");
        Jeu().ModulesDuJeu().Should().BeNull("l'API graphique de JeuA ne se lit que JeuA ouvert");
        Jeu().JeuTourne().Should().BeTrue("témoin : un jeu tourne bien, JeuB");
        Jeu().Etat().Should().Be(EtatDuJeuChoisi.Ferme);
    }

    [Fact]
    public void Sans_chemin_appris_rien_n_est_vise()
    {
        Memoire.Choisir(new JeuChoisi("JeuA.exe"));
        _enCours.Chemin = CheminB;

        Jeu().RunningGameExecutablePath().Should().BeNull();
        Jeu().Etat().Should().Be(EtatDuJeuChoisi.SansChemin);
    }

    [Fact]
    public void Le_jeu_choisi_qui_tourne_apprend_son_chemin()
    {
        Memoire.Choisir(new JeuChoisi("JeuA.exe"));
        _enCours.Chemin = CheminA;

        Jeu().Etat().Should().Be(EtatDuJeuChoisi.Ouvert);
        Memoire.Lire()!.Chemin.Should().Be(CheminA);

        _enCours.Chemin = null;
        Jeu().RunningGameExecutablePath().Should().Be(CheminA, "fermé, son profil reste visé par le chemin appris");
    }

    [Fact]
    public void Un_processus_d_un_autre_nom_n_apprend_jamais_le_chemin()
    {
        Memoire.Choisir(new JeuChoisi("JeuA.exe"));
        _enCours.Chemin = CheminB;
        Jeu().RunningGameExecutablePath();
        _enCours.Chemin = @"C:\Jeux\JeuA\JeuA.exe.old";
        Jeu().RunningGameExecutablePath();

        Memoire.Lire()!.Chemin.Should().BeNull();
    }

    [Fact]
    public void Un_chemin_appris_qui_n_existe_plus_ne_vise_rien()
    {
        Memoire.Choisir(new JeuChoisi("JeuA.exe", CheminA));
        _existants.Remove(CheminA);

        Jeu().RunningGameExecutablePath().Should().BeNull("le jeu a été déplacé ou désinstallé");
        Jeu().Etat().Should().Be(EtatDuJeuChoisi.CheminDisparu);
    }

    [Theory]
    [InlineData("JeuA.exe")]
    [InlineData(@"..\JeuA\JeuA.exe")]
    [InlineData(@"C:\Jeux\JeuB\JeuB.exe")]
    public void Un_chemin_incomplet_ou_d_un_autre_exe_est_refuse(string chemin)
    {
        _existants.Add(chemin);
        Memoire.Choisir(new JeuChoisi("JeuA.exe", chemin));

        Jeu().RunningGameExecutablePath().Should().BeNull();
    }

    [Fact]
    public void Un_fichier_abime_rend_l_automatique()
    {
        File.WriteAllText(Path.Combine(_dossier, "jeu-choisi.json"), "{ pas du json");
        _enCours.Chemin = CheminB;

        Jeu().RunningGameExecutablePath().Should().Be(CheminB);
    }
}
