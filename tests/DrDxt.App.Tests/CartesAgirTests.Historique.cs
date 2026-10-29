using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>L'historique : changements, parties, incidents et mises à jour de la machine ; la console de réparation.</summary>
public sealed partial class CartesAgirTests
{
    // La frise rassemble changements, parties et incidents, datés, le plus récent en haut.
    [Fact]
    public async Task L_historique_rassemble_changements_parties_et_incidents()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            var t = DateTimeOffset.Now;
            vm.JournalChangements.Ajouter(new DrDxt.Application.Parties.Changement(t.AddHours(-5), "le BIOS"));
            vm.LireIncidents = () => ([t.AddDays(-3)], []);
            await vm.AfficherHistoriqueAsync([new DrDxt.Application.Parties.PartieRelevee("cod.exe", t.AddHours(-2), t.AddHours(-1), 6, 140, 100, BottleneckState.CpuBoundOrAppBound)]);

            vm.AHistorique.Should().BeTrue();
            vm.LignesHistorique.Should().Equal(["Historique.Bilan"], "plan 3b.5 : le bilan de la semaine en tête");
            // La frise est une liste structurée, son genre donne la couleur du point.
            vm.FriseHistorique.Select(e => e.Genre).Should().Equal(
                DrDxt.Application.Parties.GenreDEvenement.Partie, DrDxt.Application.Parties.GenreDEvenement.Changement,
                DrDxt.Application.Parties.GenreDEvenement.ArretBrutal);
            vm.FriseHistorique[0].Texte.Should().Be("Call of Duty", "le nom du jeu à gauche");
            vm.FriseHistorique[0].Detail.Should().Be("Historique.PartieFps", "les FPS à droite");
            vm.FriseHistorique[1].Texte.Should().Be("le BIOS");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Une mise à jour faite sans DrDXT se juge sur les parties d'avant et d'après, bornée par les autres.
    [Fact]
    public void Une_mise_a_jour_de_la_machine_se_juge_en_jeu_bornee_par_les_autres_changements()
    {
        var t = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        DrDxt.Application.Parties.PartieRelevee P(double h, double low1) => new("cod.exe", t.AddHours(h), t.AddHours(h).AddMinutes(20), 6, 120, low1, BottleneckState.CpuBoundOrAppBound);
        string L(string k) => k == "Changement.Juge" ? "{0}|{2}|{8}/{9}" : k == "Changement.PasEncore" ? "{0}|attente {2}/{3}" : k;
        var parties = new[] { P(-30, 95), P(-20, 96), P(-10, 94), P(10, 80), P(20, 82), P(30, 81), P(50, 120) };
        var pilote = new DrDxt.Detection.Repair.FaitDate(t, "pilote NVIDIA (617.14)");
        var memeMinute = new DrDxt.Detection.Repair.FaitDate(t.AddSeconds(30), "pilote NVIDIA (617.14)");
        // Un levier de DrDXT posé à +40 h : la partie à +50 h n'est plus à mettre au compte du pilote.
        var levier = new DrDxt.Application.Parties.Changement(t.AddHours(40), "levier");

        ScanViewModel.LignesMisesAJour([pilote, memeMinute], parties, [levier], L)
            .Should().Equal("pilote NVIDIA (617.14)|Changement.Pire|3/3");
        ScanViewModel.LignesMisesAJour([pilote], parties.Take(4).ToList(), [], L)
            .Should().Equal("pilote NVIDIA (617.14)|attente 1/3");
        ScanViewModel.LignesMisesAJour([new DrDxt.Detection.Repair.FaitDate(t.AddHours(-40), "trop tôt")], parties, [], L)
            .Should().BeEmpty("aucune partie d'avant : elle ne se jugera jamais");
    }

    [Fact]
    public async Task L_historique_dit_ce_qu_une_mise_a_jour_a_coute_en_jeu()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            var t = DateTimeOffset.Now;
            vm.LireMisesAJourDeLaMachine = () => [new DrDxt.Detection.Repair.FaitDate(t.AddHours(-1), "Windows Update : pilote")];
            await vm.AfficherHistoriqueAsync([new DrDxt.Application.Parties.PartieRelevee("cod.exe", t.AddHours(-3), t.AddHours(-2), 6, 140, 100, BottleneckState.CpuBoundOrAppBound)]);

            vm.LignesHistorique[0].Should().Be("Changement.PasEncore");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // Les journaux d'installation ne se relisent pas à chaque fin de partie.
    [Fact]
    public async Task Les_mises_a_jour_de_la_machine_se_lisent_une_fois_par_heure_au_plus()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var t = DateTimeOffset.Now;
        vm.Horloge = () => t;
        var lectures = 0;
        vm.LireMisesAJourDeLaMachine = () => { lectures++; return []; };

        await vm.AfficherHistoriqueAsync([]);
        await vm.AfficherHistoriqueAsync([]);
        lectures.Should().Be(1);
        t = t.AddHours(2);
        await vm.AfficherHistoriqueAsync([]);
        lectures.Should().Be(2);
    }

    // Ce qui a changé depuis le dernier scan passe en tête, même sans partie ni changement noté.
    [Fact]
    public async Task L_historique_commence_par_ce_qui_a_change_depuis_le_dernier_scan()
    {
        var chemin = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"frise-{Guid.NewGuid():N}.json");
        try
        {
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.JournalChangements = new DrDxt.Application.Parties.JournalDesChangements(chemin);
            vm.LireChangementsDepuisLeScan = () => ["pilote changé"];
            await vm.AfficherHistoriqueAsync([]);

            vm.AHistorique.Should().BeTrue();
            vm.LignesHistorique.Should().Equal("Historique.DepuisLeScan");
        }
        finally { System.IO.File.Delete(chemin); }
    }

    // « prêt à jouer » dit les gênes mesurées, puis la ligne de ce qui a été mesuré.
    [Fact]
    public async Task Pret_a_jouer_dit_les_genes_puis_ce_qui_a_ete_mesure()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.MesurerAvantPartie = _ => Task.FromResult(new DrDxt.Application.Parties.EtatAvantPartie(
            [("TiWorker", 80)], 1, 0, 12, 0, null, 16UL << 30));
        await vm.VerifierPretAJouerCommand.ExecuteAsync(null);
        vm.LignesPretAJouer.Should().Equal("PretAJouer.AGenes", "PretAJouer.Voisins", "PretAJouer.Mesure");

        vm.MesurerAvantPartie = _ => Task.FromResult(new DrDxt.Application.Parties.EtatAvantPartie([], 1, 0, 12, 0, 55, 16UL << 30));
        await vm.VerifierPretAJouerCommand.ExecuteAsync(null);
        vm.LignesPretAJouer.Should().Equal("PretAJouer.Ok", "PretAJouer.Mesure");
    }

    [Fact]
    public void La_console_appelle_les_outils_par_leur_chemin_absolu_et_sans_profil()
    {
        var psi = ScanViewModel.ConsoleDeReparation(@"C:\Windows\system32");
        psi.FileName.Should().Be(@"C:\Windows\system32\WindowsPowerShell\v1.0\powershell.exe");
        psi.Arguments.Should().StartWith("-NoProfile ")
            .And.Contain(@"& 'C:\Windows\system32\Dism.exe' /Online /Cleanup-Image /RestoreHealth")
            .And.Contain(@"& 'C:\Windows\system32\sfc.exe' /scannow")
            .And.NotContain("$env:");
    }

    // Installée, la console passe par le lanceur natif, avec sa commande fixe.
    [Fact]
    public void Installee_la_console_passe_par_le_lanceur_natif_sans_argument_venu_de_l_app()
    {
        var dossier = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "drdxt-console-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dossier);
        try
        {
            var lanceur = System.IO.Path.Combine(dossier, "DrDxt.Lanceur.exe");
            System.IO.File.WriteAllText(lanceur, "");
            var psi = ScanViewModel.ConsoleDeReparation(@"C:\Windows\system32", lanceur);
            psi.FileName.Should().Be(lanceur);
            psi.Verb.Should().Be("runas");
            psi.ArgumentList.Should().Equal("reparer-windows");
            ScanViewModel.ConsoleDeReparation(@"C:\Windows\system32", System.IO.Path.Combine(dossier, "absent.exe")).FileName
                .Should().EndWith("powershell.exe", "sans lanceur à côté, la console s'ouvre seule");
        }
        finally { System.IO.Directory.Delete(dossier, true); }
    }
}
