using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// 07/10/2026 (D7) : l'historique de Windows Update, sans les définitions de Defender ni les applications du Store.
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

    // Revue F-42 : la classification « pilotes » écarte aussi un pilote au titre inattendu.
    [Fact]
    public void Un_pilote_classe_comme_tel_est_ecarte_quel_que_soit_son_titre()
        => MisesAJourWindows.Garder("Intel Corporation Extension", pilote: true).Should().BeFalse();

    [Fact, Trait("Category", "Machine")] // lit l'historique de l'agent Windows Update de la vraie machine
    public void L_historique_de_la_machine_se_lit_sans_le_bruit()
    {
        var faits = MisesAJourWindows.Lire();

        faits.Should().NotBeEmpty();
        faits.Should().OnlyContain(f => f.Libelle.StartsWith("Windows Update : ") && MisesAJourWindows.Garder(f.Libelle));
        faits.Should().OnlyContain(f => f.Quand <= DateTimeOffset.Now);
    }
}
