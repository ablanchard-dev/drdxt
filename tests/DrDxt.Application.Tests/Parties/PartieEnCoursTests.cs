using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

/// <summary>
/// La veille marque la partie qu'elle suit et lève la marque à sa clôture ; une marque
/// restée ouverte au démarrage suivant devient une partie interrompue.
/// </summary>
public sealed class PartieEnCoursTests : IDisposable
{
    private readonly string _dossier = Directory.CreateTempSubdirectory("drdxt-encours-").FullName;
    private PartiesMemorisees Memoire() => new(Path.Combine(_dossier, "choix.json"), Path.Combine(_dossier, "parties.json"));
    public void Dispose() => Directory.Delete(_dossier, true);

    private static readonly DateTimeOffset T = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Une_marque_restee_ouverte_devient_une_partie_interrompue_et_une_seule_fois()
    {
        Memoire().NoterEnCours(new PartieInterrompue("cod.exe", T, T.AddMinutes(9)));

        Memoire().ReleverInterrompue(T.AddHours(1));
        Memoire().ReleverInterrompue(T.AddHours(2));

        Memoire().Interrompues().Should().Equal(new PartieInterrompue("cod.exe", T, T.AddMinutes(9), T.AddHours(1)));
    }

    // Les parties interrompues ne s'écrivent pas (le chemin est un dossier) : la marque reste, et le démarrage suivant
    // relèvera la partie, au lieu de l'effacer sans l'avoir gardée.
    [Fact]
    public void Une_partie_interrompue_qui_ne_s_ecrit_pas_garde_sa_marque()
    {
        Directory.CreateDirectory(Path.Combine(_dossier, "parties-interrompues.json"));
        Memoire().NoterEnCours(new PartieInterrompue("cod.exe", T, T.AddMinutes(9)));

        Memoire().ReleverInterrompue(T.AddHours(1));

        File.ReadAllText(Path.Combine(_dossier, "partie-en-cours.json")).Should().Contain("cod.exe");
    }

    [Fact]
    public void Une_partie_close_ne_laisse_rien()
    {
        Memoire().NoterEnCours(new PartieInterrompue("cod.exe", T, T));
        Memoire().NoterEnCours(null);
        Memoire().ReleverInterrompue(T.AddHours(1));
        Memoire().Interrompues().Should().BeEmpty();
    }

    [Fact]
    public async Task La_veille_marque_la_partie_puis_leve_la_marque_a_sa_cloture()
    {
        var etats = new Queue<(string, int)?>([("cod.exe", 7), ("cod.exe", 7), null]);
        var suivis = new List<PartieInterrompue?>();
        using var fin = new CancellationTokenSource();
        var heure = T;

        var veille = new VeilleDesParties(
            jeu: () => etats.Count > 0 ? etats.Dequeue() : null,
            capturer: (_, _, _) => Task.FromResult<BenchmarkResult?>(new BenchmarkResult(140, 98, 70, [], null, 1, 1)),
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: _ => fin.Cancel(),
            patienter: (_, _) => { heure = heure.AddMinutes(3); return Task.CompletedTask; },
            horloge: () => heure,
            suivre: suivis.Add);

        await veille.VeillerAsync(fin.Token);

        suivis.Should().HaveCount(3);
        suivis[0]!.Should().Be(new PartieInterrompue("cod.exe", T, T));
        suivis[1]!.DernierVu.Should().Be(T.AddMinutes(3), "chaque regard repousse le dernier vu, le début ne bouge pas");
        suivis[1]!.Debut.Should().Be(T);
        suivis[2].Should().BeNull("la partie close lève la marque");
    }

    // Une veille annulée en pleine partie (relevé éteint, App fermée) n'est pas un arrêt du PC.
    [Fact]
    public async Task Une_veille_annulee_en_pleine_partie_leve_la_marque()
    {
        var suivis = new List<PartieInterrompue?>();
        using var fin = new CancellationTokenSource();
        var veille = new VeilleDesParties(
            jeu: () => ("cod.exe", 7),
            capturer: (_, _, _) => { fin.Cancel(); throw new OperationCanceledException(); },
            classer: _ => BottleneckState.CpuBoundOrAppBound,
            enregistrer: _ => { },
            patienter: (_, _) => Task.CompletedTask,
            horloge: () => T,
            suivre: suivis.Add);

        await veille.VeillerAsync(fin.Token);

        suivis.Should().HaveCount(2);
        suivis[^1].Should().BeNull();
    }

    [Fact]
    public void Un_fichier_avec_une_entree_nulle_ne_rend_que_les_vraies()
    {
        File.WriteAllText(Path.Combine(_dossier, "parties-interrompues.json"), "[null]");
        Memoire().Interrompues().Should().BeEmpty();
    }
}
