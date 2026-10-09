#if DEBUG
// Le rejeu d'une machine n'existe qu'en Debug (MachineDeTest est compilé hors de la version livrée).
using System.IO;
using Microsoft.Extensions.DependencyInjection;
using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// 06/10/2026 (chantier B14, S8) : l'argument DEBUG « machine=&lt;nom&gt; » rejoue une machine enregistrée dans
/// l'App. Chaque fichier de tests/Shared/machines doit se charger par son nom et nourrir le scan à la place de ce PC.
/// </summary>
public class MachineDeTestDansLAppTests
{
    [Theory]
    [MemberData(nameof(MachinesEnregistreesTests.Noms), MemberType = typeof(MachinesEnregistreesTests))]
    public async Task Chaque_machine_se_charge_par_son_nom_et_remplace_ce_PC(string nom)
    {
        var m = MachineDeTest.Charger(nom);
        var d = new MachineDeTest.Detecteur(m);

        m.Nom.Should().Be(nom);
        (await d.DetectAsync()).Value.Should().BeSameAs(m.Inventaire);
        (await d.ReadAsync(m.Inventaire)).Value.Should().BeSameAs(m.Reglages);
        (await d.RunAsync(0, TimeSpan.Zero, 1, CancellationToken.None)).IsOk.Should().BeFalse("une fixture n'a pas de partie");
    }

    // Revue de la passation (P0-2) : la machine sans droits se rejoue sans droits (le bouton « relancer en
    // administrateur » se dessine) ; un fichier sans le champ est lu avec les droits.
    [Fact]
    public void La_machine_sans_droits_se_rejoue_sans_droits()
    {
        MachineDeTest.Charger("sans-admin").Eleve.Should().BeFalse();
        MachineDeTest.Charger("amd-radeon-synthetique").Eleve.Should().BeNull();
    }

    // Le branchement, pas seulement le fichier : l'écran construit par l'App rejoue les droits de la machine.
    [Fact]
    public void L_ecran_construit_par_l_App_rejoue_les_droits_de_la_machine()
    {
        MachineDeTest.Active = MachineDeTest.Charger("sans-admin");
        try
        {
            using var provider = new ServiceCollection().AddDrDxtApp().BuildServiceProvider();
            var vm = provider.GetRequiredService<ViewModels.ScanViewModel>();
            // PeutRelancerEnAdministrateur demande aussi un scan avec des angles morts ; lancer un scan ici lirait ce PC
            // (route, voisins). On lit ce que l'écran a reçu.
            typeof(ViewModels.ScanViewModel).GetField("_estEleve",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(vm).Should().Be(false);
        }
        finally { MachineDeTest.Active = null; }
    }

    // 06/10/2026 : le rejeu de 19:23 a écrit la machine AMD dans les vraies données (changements.json, repair-history.json).
    // Activer une machine sans dossier choisi redirige les données vers un dossier jetable ; un dossier déjà choisi est gardé.
    [Fact]
    public void Activer_une_machine_redirige_les_donnees_hors_de_celles_du_joueur()
    {
        var variables = new Dictionary<string, string>();
        try
        {
            var dossier = MachineDeTest.Activer("amd-radeon-synthetique", k => variables.GetValueOrDefault(k), (k, v) => variables[k] = v);

            dossier.Should().StartWith(Path.GetTempPath()).And.Contain("amd-radeon-synthetique");
            variables[DrDxt.Core.DossierDonnees.VariableDeDeveloppement].Should().Be(dossier);
            DrDxt.Core.DossierDonnees.Racine(k => variables.GetValueOrDefault(k)).Should().Be(dossier);

            var choisi = Path.Combine(Path.GetTempPath(), "dossier-choisi");
            variables[DrDxt.Core.DossierDonnees.VariableDeDeveloppement] = choisi;
            MachineDeTest.Activer("sans-admin", k => variables.GetValueOrDefault(k), (k, v) => variables[k] = v).Should().Be(choisi);
        }
        finally { MachineDeTest.Active = null; }
    }

    [Fact]
    public void Un_nom_inconnu_le_dit()
        => FluentActions.Invoking(() => MachineDeTest.Charger("machine-qui-n-existe-pas"))
            .Should().Throw<FileNotFoundException>().WithMessage("*machine-qui-n-existe-pas*");
}
#endif
