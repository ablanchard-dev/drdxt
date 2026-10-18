using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// L'historique de Windows Update, sans les définitions de Defender ni les applications du Store.
public sealed class MisesAJourWindowsTests
{
    [Theory]
    [InlineData("Mise à jour de la sélection disjointe pour Microsoft Defender Antivirus – 2267602 Ko (version 1.459.576.0) – Canal actuel (large)", false)]
    [InlineData("9NRZT3Q9R3DL-Microsoft.WindowsAppRuntime.2", false)]
    [InlineData("Realtek SoftwareComponent Driver Update (11.0.6000.398)", false)]
    [InlineData("NVIDIA - Display - 32.0.15.6094", false)]
    [InlineData("2026-10 Mise à jour cumulative pour Windows 11 Version 25H2 pour les systèmes x64 (KB5066835)", true)]
    public void Seul_ce_qui_peut_changer_un_jeu_est_garde(string titre, bool garde)
        => MisesAJourWindows.Garder(titre).Should().Be(garde);

    // La classification « pilotes » écarte aussi un pilote au titre inattendu.
    [Fact]
    public void Un_pilote_classe_comme_tel_est_ecarte_quel_que_soit_son_titre()
        => MisesAJourWindows.Garder("Intel Corporation Extension", pilote: true).Should().BeFalse();

    [Fact, Trait("Category", "Machine")] // lit l'historique de l'agent Windows Update de la vraie machine
    public void L_historique_de_la_machine_se_lit_sans_le_bruit()
    {
        var faits = MisesAJourWindows.Lire();

        // Une machine réinstallée la veille peut n'avoir encore aucune mise à jour : vide n'est pas une panne.
        faits.Where(f => !(f.Libelle.StartsWith("Windows Update : ") && MisesAJourWindows.Garder(f.Libelle))).Should().BeEmpty();
        faits.Where(f => f.Quand > DateTimeOffset.Now).Should().BeEmpty();
    }

    // Le radar des régressions compare des numéros KB : ceux des titres d'installation, une fois chacun.
    [Fact]
    public void Les_numeros_KB_se_lisent_dans_les_titres_sans_doublon()
    {
        var t = DateTimeOffset.UnixEpoch;
        MisesAJourWindows.NumerosKb([
            new FaitDate(t, "Windows Update : 2025-08 Cumulative Update for Windows 11 Version 24H2 (KB5063878)"),
            new FaitDate(t, "Windows Update : 2025-08 Cumulative Update for Windows 11 Version 24H2 (KB5063878)"),
            new FaitDate(t, "Windows Update : Mise à jour de la pile de maintenance KB5065381"),
            new FaitDate(t, "Windows Update : Realtek - Net - 10.68.815.2023"),
        ]).Should().Equal("KB5063878", "KB5065381");
    }
}
