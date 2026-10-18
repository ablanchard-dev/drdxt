using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le point se crée par WMI SystemRestore.CreateRestorePoint, comme Checkpoint-Computer.
// SRSetRestorePoint n'a créé aucun point sur 4 essais sur ma machine (VSS 8193/8194), WMI 7 sur 8
// (research/north-star/point-de-restauration.md). Le code de retour non nul est un échec nommé ; zéro ne prouve pas
// le point, que SafetyManager relit.
public sealed class RestorePointEndFailureTests
{
    [Fact]
    public void Zero_est_une_demande_acceptee_et_la_description_passe()
    {
        string? recue = null;
        var r = new Win32RestorePointActions(d => { recue = d; return (0u, null); }).CreatePoint("DrDXT batch");

        r.IsOk.Should().BeTrue();
        recue.Should().Be("DrDXT batch");
    }

    [Fact]
    public void Un_code_non_nul_est_un_echec_qui_le_nomme()
    {
        var r = new Win32RestorePointActions(_ => (0x80070422u, null)).CreatePoint("DrDXT batch");

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("0x80070422", "revue 06/10 : un HRESULT dépassait int et finissait en OverflowException");
    }

    // L'activation lisait le code par int.TryParse ; un HRESULT ne s'y lit pas, et
    // l'échec était pris pour un succès. Même lecture pour les deux méthodes WMI.
    [Theory]
    [InlineData(0u, true)]
    [InlineData(0x80070422u, false)]
    [InlineData(1u, false)]
    public void Le_code_WMI_se_lit_en_entier_non_signe(uint code, bool ok)
        => Win32RestorePointActions.LireRetour(code, "SystemRestore.Enable").IsOk.Should().Be(ok);

    [Theory]
    [InlineData(null)]
    [InlineData("pas un code")]
    public void Un_code_absent_ou_illisible_n_est_pas_un_succes(object? code)
        => Win32RestorePointActions.LireRetour(code, "SystemRestore.Enable").IsOk.Should().BeFalse();

    // Après une création, les points de DrDXT
    // au-delà des 2 plus récents sont retirés ; seulement le nom fixe exact et l'ancien nom, jamais un autre point.
    [Fact]
    public void Seuls_les_plus_anciens_points_DrDXT_sont_retires_jusqu_a_en_garder_deux()
    {
        var points = new (uint, string)[]
        {
            (10, CreateRestorePointOp.NomDrDxt), (12, CreateRestorePointOp.NomDrDxt), (5, "DrDXT batch"),
            (14, CreateRestorePointOp.NomDrDxt), (20, "Windows Update"),
        };

        Win32RestorePointActions.AElaguer(points).Should().Equal(5u, 10u);
    }

    // Lu sur ma machine le 06/10 (Volsnap 95) : Windows n'efface les clichés que du plus ancien au plus récent. Retirer
    // un point DrDXT plus récent qu'un point de Windows a fait disparaître le point de Windows. DrDXT ne retire donc
    // un de ses points que s'il est le plus ancien de la machine, et s'arrête au premier point qui n'est pas à lui.
    [Fact]
    public void Un_point_d_un_autre_plus_ancien_arrete_l_elagage()
    {
        var points = new (uint, string)[]
        {
            (2, "Installed MyGame"), (10, CreateRestorePointOp.NomDrDxt), (12, CreateRestorePointOp.NomDrDxt),
            (14, CreateRestorePointOp.NomDrDxt),
        };
        Win32RestorePointActions.AElaguer(points).Should().BeEmpty("retirer 10 effacerait aussi MyGame (2)");

        var entre = new (uint, string)[]
        {
            (5, "DrDXT batch"), (11, "Windows Update"), (12, CreateRestorePointOp.NomDrDxt), (14, CreateRestorePointOp.NomDrDxt),
            (16, CreateRestorePointOp.NomDrDxt),
        };
        Win32RestorePointActions.AElaguer(entre).Should().Equal(new uint[] { 5 }, "12 est plus récent que Windows Update (11)");
    }

    [Fact]
    public void Apres_une_creation_reussie_les_anciens_sont_retires_et_jamais_apres_un_echec()
    {
        var retires = new List<uint>();
        var liste = () => (IReadOnlyList<(uint, string)>)new (uint, string)[]
            { (1, CreateRestorePointOp.NomDrDxt), (2, CreateRestorePointOp.NomDrDxt), (3, CreateRestorePointOp.NomDrDxt) };

        new Win32RestorePointActions(_ => (0u, null), liste, s => { retires.Add(s); return 0u; }).CreatePoint(CreateRestorePointOp.NomDrDxt)
            .IsOk.Should().BeTrue();
        retires.Should().Equal(1u);

        retires.Clear();
        new Win32RestorePointActions(_ => (0x80070422u, null), liste, s => { retires.Add(s); return 0u; }).CreatePoint(CreateRestorePointOp.NomDrDxt);
        retires.Should().BeEmpty("aucun point neuf : on ne retire rien");
    }

    // Le point est créé par l'outil de Windows (Checkpoint-Computer, 3 réussites sur 3 sur ma
    // machine, quand le helper en appel direct échouait 2 fois sur 2). Arguments fixes : aucun nom ne s'y injecte.
    [Fact]
    public void Checkpoint_Computer_recoit_des_arguments_fixes_et_le_nom_echappe()
    {
        var args = Win32RestorePointActions.ArgumentsCheckpoint("DrDXT : l'essai", @"C:\Windows\system32");

        args.Should().StartWith(new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command" });
        args[^1].Should().Be(Win32RestorePointActions.ModuleSysteme(@"C:\Windows\system32")
            + "Checkpoint-Computer -Description 'DrDXT : l''essai' -RestorePointType APPLICATION_INSTALL -ErrorAction Stop");
    }

    // Powershell -Command sort 1 sur toute erreur ; la raison est dans sa sortie d'erreur,
    // et c'est elle que l'écran doit dire.
    [Fact]
    public void La_phrase_d_erreur_de_Windows_remonte()
    {
        var r = new Win32RestorePointActions(_ => (1u, "Checkpoint-Computer : Le service est désactivé.\r\n   + suite technique"))
            .CreatePoint(CreateRestorePointOp.NomDrDxt);

        r.Error!.Value.Message.Should().Be("Checkpoint-Computer : Le service est désactivé.");
    }

    [Fact]
    public void Une_exception_WMI_est_un_echec_pas_un_plantage()
        => new Win32RestorePointActions(_ => throw new InvalidOperationException("wmi"))
            .CreatePoint("DrDXT batch").IsOk.Should().BeFalse();
}
