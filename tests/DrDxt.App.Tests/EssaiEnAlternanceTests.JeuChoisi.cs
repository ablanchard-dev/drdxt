using System.IO;
using System.Text.Json;
using DrDxt.App.ViewModels;
using DrDxt.Application.Parties;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Le jeu à optimiser, choisi sous Optimiser : la ligne qui le dit, la liste « Changer », et l'essai qui se juge sur lui.</summary>
public sealed partial class EssaiEnAlternanceTests
{
    private const string CheminJeuA = @"C:\Jeux\JeuA\JeuA.exe";

    private sealed class JeuOuvertFixe(string? chemin) : IGameExecutable
    {
        public string? RunningGameExecutablePath() => chemin;
    }

    private MemoireDuJeuChoisi MemoireDuChoix(JeuChoisi? choix)
    {
        var memoire = new MemoireDuJeuChoisi(Path.Combine(_dossier, "jeu-choisi.json"));
        memoire.Choisir(choix);
        return memoire;
    }

    private ScanViewModel AvecLeChoix(JeuChoisi? choix, string? ouvert, Func<string, bool> existe)
    {
        var vm = Vm();
        vm.MemoireJeuChoisi = MemoireDuChoix(choix);
        vm.JeuAOptimiser = new JeuAOptimiser(new JeuOuvertFixe(ouvert), vm.MemoireJeuChoisi, existe);
        return vm;
    }

    [Fact]
    public void La_ligne_dit_qu_un_chemin_appris_n_existe_plus()
    {
        var vm = AvecLeChoix(new JeuChoisi("JeuA.exe", CheminJeuA), ouvert: null, existe: _ => false);

        vm.LigneJeuChoisi.Should().Be("JeuChoisi.Ligne");
        vm.DetailJeuChoisi.Should().Be("JeuChoisi.CheminDisparu");
    }

    [Fact]
    public void La_ligne_dit_quoi_faire_tant_que_le_chemin_n_est_pas_appris_et_se_tait_jeu_ouvert()
    {
        AvecLeChoix(new JeuChoisi("JeuA.exe"), ouvert: null, existe: _ => true).DetailJeuChoisi.Should().Be("JeuChoisi.SansChemin");
        AvecLeChoix(new JeuChoisi("JeuA.exe", CheminJeuA), ouvert: @"C:\Jeux\JeuB\JeuB.exe", existe: _ => true)
            .DetailJeuChoisi.Should().Be("JeuChoisi.Ferme");
        AvecLeChoix(new JeuChoisi("JeuA.exe"), ouvert: CheminJeuA, existe: _ => true).DetailJeuChoisi.Should().BeEmpty();
        AvecLeChoix(null, ouvert: CheminJeuA, existe: _ => true).LigneJeuChoisi.Should().Be("JeuChoisi.Automatique");
    }

    // Automatique d'abord, puis les jeux relevés du plus joué au moins joué, puis le jeu ouvert qui n'a pas encore de partie.
    [Fact]
    public async Task La_liste_propose_les_jeux_joues_puis_le_jeu_ouvert_et_le_choix_se_retient()
    {
        var vm = AvecLeChoix(null, ouvert: null, existe: _ => true);
        var debut = DateTimeOffset.Parse("2026-10-20T20:00:00+02:00");
        PartieRelevee Partie(string jeu, int jour, int minutes) => new(jeu, debut.AddDays(jour), debut.AddDays(jour).AddMinutes(minutes),
            4, 100, 70, BottleneckState.CpuBoundOrAppBound);
        File.WriteAllText(Path.Combine(_dossier, "parties.json"), JsonSerializer.Serialize(new List<PartieRelevee>
            { Partie("JeuA.exe", 3, 30), Partie("JeuB.exe", 2, 60), Partie("JeuB.exe", 1, 60) }));
        vm.ExeDuJeuOuvertPourLeTest = () => "JeuC.exe";

        vm.ChangerJeuCommand.Execute(null);

        vm.ListeDesJeuxOuverte.Should().BeTrue();
        vm.JeuxProposes.Select(j => j.Exe).Should().Equal(null, "JeuB.exe", "JeuA.exe", "JeuC.exe");

        await vm.ChoisirJeuCommand.ExecuteAsync("JeuA.exe");
        vm.MemoireJeuChoisi!.Lire()!.Exe.Should().Be("JeuA.exe");
        vm.ListeDesJeuxOuverte.Should().BeFalse("le choix referme la liste");

        await vm.ChoisirJeuCommand.ExecuteAsync(null);
        vm.MemoireJeuChoisi.Lire().Should().BeNull("automatique");
    }

    // Rechoisir le même jeu garde le chemin déjà appris.
    [Fact]
    public async Task Rechoisir_le_meme_jeu_garde_son_chemin()
    {
        var vm = AvecLeChoix(new JeuChoisi("JeuA.exe", CheminJeuA), ouvert: null, existe: _ => true);

        await vm.ChoisirJeuCommand.ExecuteAsync("JeuA.exe");

        vm.MemoireJeuChoisi!.Lire()!.Chemin.Should().Be(CheminJeuA);
    }
}
