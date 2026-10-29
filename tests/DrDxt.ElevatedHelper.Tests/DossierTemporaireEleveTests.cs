using DrDxt.Lanceur;
using FluentAssertions;
using Xunit;
using DrDxt.Tests.Shared;

namespace DrDxt.ElevatedHelper.Tests;

// Les fils des processus élevés (dism et PowerShell du helper, logman de l'App élevée, ses .etl) héritent du TEMP que le
// lanceur leur donne. Celui du compte (%LOCALAPPDATA%\Temp) s'écrit sans droits : un programme du compte y prépare un
// fichier ou une jonction qu'un processus élevé ouvrirait. Le lanceur ne le donne plus : SystemTemp (SYSTEM et
// Administrateurs seuls), sinon ProgramData\DrDxt\temp s'il est sûr, sinon Windows\Temp (les comptes y créent, mais ne
// touchent pas aux fichiers des autres).
public sealed class DossierTemporaireEleveTests
{
    private const string Windows = @"C:\Windows";
    private const string Donnees = @"C:\ProgramData";

    [Fact]
    public void SystemTemp_d_abord_quand_il_est_sur()
        => EnvironnementPropre.DossierTemporaire(Windows, Donnees, d => d == @"C:\Windows\SystemTemp", _ => { })
            .Should().Be(@"C:\Windows\SystemTemp");

    [Fact]
    public void Sans_SystemTemp_le_dossier_de_DrDXT_s_il_est_sur_cree_sous_lui()
    {
        var crees = new List<string>();
        var sur = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\ProgramData\DrDxt", @"C:\ProgramData\DrDxt\temp" };

        EnvironnementPropre.DossierTemporaire(Windows, Donnees, sur.Contains, crees.Add).Should().Be(@"C:\ProgramData\DrDxt\temp");
        crees.Should().Contain(@"C:\ProgramData\DrDxt\temp").And.HaveCount(1, "créé une fois, sous le dossier sûr");
    }

    [Fact]
    public void Un_dossier_de_DrDXT_qui_n_est_pas_sur_ne_sert_pas_et_rien_n_y_est_cree()
    {
        var crees = new List<string>();

        EnvironnementPropre.DossierTemporaire(Windows, Donnees, _ => false, crees.Add).Should().Be(@"C:\Windows\Temp",
            "jamais le TEMP du compte");
        crees.Should().BeEmpty("on ne crée rien sous un dossier que le compte contrôle");
    }

    [FactAdmin]
    public void Un_dossier_du_compte_n_est_pas_sur_et_SystemTemp_l_est_quand_il_existe()
    {
        EnvironnementPropre.DossierSur(Path.GetTempPath()).Should().BeFalse("le TEMP du compte s'écrit sans droits");
        EnvironnementPropre.DossierSur(@"C:\Dossier\Qui\N\Existe\Pas").Should().BeFalse();
        var systemTemp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "SystemTemp");
        if (Directory.Exists(systemTemp))
            EnvironnementPropre.DossierSur(systemTemp).Should().BeTrue("témoin : SYSTEM et Administrateurs seuls");
    }
}
