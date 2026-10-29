using DrDxt.App.ViewModels;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>Le rapport exporté et le résultat affiché : ce qu'ils portent, ce qu'ils taisent, et l'heure du scan.</summary>
public sealed partial class CartesAgirTests
{
    [Fact]
    public async Task Le_rapport_exporte_ne_porte_pas_le_nom_du_joueur()
    {
        // Un constat dont la valeur contient un chemin du profil : le rapport est fait pour être envoyé.
        var chemin = System.IO.Path.Combine("C:" + System.IO.Path.DirectorySeparatorChar, "Users", "alicejoueuse", "AppData", "jeu.cfg");
        var constat = new RepairFinding("STABILITE", "fichier du jeu", chemin, null, RepairStatus.Suspicious, "lu.");
        string? ecrit = null;
        var vm = Vm(new[] { constat }, helper: null, writeReport: (_, texte) => { ecrit = texte; return "rapport.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().NotBeNull();
        ecrit.Should().Contain("Fichier du jeu", "témoin : le constat est bien dans le rapport");
        ecrit.Should().NotContain("alicejoueuse");
        ecrit.Should().Contain("jeu.cfg", "la fin du chemin reste lisible, seul le nom est masqué");
    }

    [Fact]
    public async Task Le_rapport_exporte_ne_porte_ni_le_nom_du_pc_ni_une_adresse_ip()
    {
        var detail = $"partage \\\\{Environment.MachineName}\\jeux, routeur 192.168.1.42";
        var constat = new RepairFinding("RESEAU", "chemin du jeu", detail, null, RepairStatus.Suspicious, "lu.");
        string? ecrit = null;
        var vm = Vm(new[] { constat }, helper: null, writeReport: (_, texte) => { ecrit = texte; return "rapport.txt"; });
        await vm.ScanCommand.ExecuteAsync(null);

        await vm.ExportReportCommand.ExecuteAsync(null);

        ecrit.Should().Contain("Chemin du jeu", "témoin : le constat est bien dans le rapport");
        ecrit.Should().NotContain(Environment.MachineName).And.NotContain("192.168.1.42");
    }

    [Fact]
    public void Copier_met_la_commande_dans_le_presse_papiers_et_rien_d_autre()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        var copies = new List<string>();
        vm.VersPressePapiers = copies.Add;

        vm.CopierCommand.Execute("icacls \"C:\\x.exe\" /reset");
        vm.CopierCommand.Execute("");

        copies.Should().Equal("icacls \"C:\\x.exe\" /reset");
    }

    // Les points affichés disent de quand ils datent.
    [Fact]
    public async Task Le_resultat_dit_l_heure_du_scan()
    {
        var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
        vm.Maintenant = () => new DateTime(2026, 9, 29, 11, 24, 0);
        vm.DateDuScan.Should().BeNullOrEmpty("témoin : avant le scan, pas de date");

        await vm.ScanCommand.ExecuteAsync(null);

        vm.DateDuScan.Should().Be("Scan.Date 29/09 · 11:24");
    }

    // Le format régional de Windows peut porter un autre calendrier (ar-SA : hégirien) ; la date suit la langue du docteur.
    [Fact]
    public async Task L_heure_du_scan_ne_suit_pas_le_calendrier_du_format_regional()
    {
        var avant = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ar-SA");
            var vm = Vm(Array.Empty<RepairFinding>(), helper: null);
            vm.Maintenant = () => new DateTime(2026, 9, 29, 11, 24, 0);

            await vm.ScanCommand.ExecuteAsync(null);

            vm.DateDuScan.Should().Be("Scan.Date 29/09 · 11:24");
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = avant; }
    }
}
