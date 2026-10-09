using System.Text;
using DrDxt.App.Platform;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>25/09/2026 — le constat du magasin de composants. Machine de reference : « repairable ».</summary>
public class SanteWindowsTests
{
    private static Func<Result<byte[]>> Lu(string etat) => () => Result<byte[]>.Ok(Encoding.UTF8.GetBytes(etat));

    [Fact]
    public void Un_magasin_reparable_est_suspect_et_donne_la_reparation_Microsoft()
    {
        var c = SanteWindows.Constat(Lu(SanteDesComposants.Reparable));
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        c.Explication.Should().Contain("/RestoreHealth").And.Contain("sfc /scannow");
    }

    // 29/09/2026, troisième critique : « Magasin de composants » et « DISM /CheckHealth » en tête parlaient à un
    // technicien. Le titre dit ce que c'est, la première phrase dit le fait ; l'outil et ses commandes suivent.
    [Fact]
    public void Le_constat_parle_au_joueur_avant_de_citer_l_outil()
    {
        var c = SanteWindows.Constat(Lu(SanteDesComposants.Reparable));
        var premiere = c.Explication[..c.Explication.IndexOf(". ", StringComparison.Ordinal)];

        premiere.Should().NotContain("DISM");
        RepairLibelles.Nom(c.Nom).Should().Be("Fichiers de réparation de Windows");
    }

    [Fact]
    public void Un_magasin_sain_est_le_defaut_attendu()
        => SanteWindows.Constat(Lu(SanteDesComposants.Sain)).Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Non_lu_n_est_pas_sain()
        => SanteWindows.Constat(() => Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "helper absent"))
            .Statut.Should().Be(RepairStatus.Unknown);

    // 07/10/2026 (D8) : après « Réinstaller maintenant », la preuve est datée par la dernière installation de Windows.
    [Fact]
    public void Le_constat_lu_dit_la_date_de_la_derniere_installation_et_le_non_lu_ne_dit_rien()
    {
        var reinstalle = new DateTimeOffset(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);
        SanteWindows.Constat(Lu(SanteDesComposants.Sain), () => reinstalle).Explication.Should().Contain("le 08/10/2026");
        SanteWindows.Constat(() => Result<byte[]>.Fail(ErrorCodes.HelperOpFailed, "helper absent"), () => reinstalle)
            .Explication.Should().NotContain("2026");
        SanteWindows.Constat(Lu(SanteDesComposants.Reparable), () => null).Explication.Should().NotContain("Dernière installation");
        SanteWindows.Constat(Lu(SanteDesComposants.Sain), () => reinstalle).Explication.Should().Contain("mise à niveau",
            "une mise à niveau de Windows réécrit la même date (revue F-49)");
    }
}
