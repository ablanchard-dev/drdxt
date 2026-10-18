using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Les saccades du fTPM d'AMD : la date du BIOS contre le correctif d'AMD (PA-410), le TPM actif vérifié, jamais « touché ».
public sealed class FtpmAmdTests
{
    private static readonly GenerationAmd.Ligne Zen3 = GenerationAmd.Table.Single(l => l.Famille == 0x19 && l.ModeleMin == 0x21);
    private static readonly GenerationAmd.Ligne Zen4 = GenerationAmd.Table.Single(l => l.Famille == 0x19 && l.ModeleMin == 0x61);

    [Fact]
    public void Un_fTPM_actif_sur_un_BIOS_d_avant_le_correctif_est_a_verifier_jamais_touche()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = FtpmAmd.Juger(Zen3, FtpmAmd.IdentifiantAmd, true, new DateTime(2021, 11, 3)).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("À vérifier").And.Contain("PA-410").And.Contain("ne lit pas la version AGESA");
    }

    [Fact]
    public void Un_BIOS_posterieur_est_dit_sans_promettre_que_l_AGESA_est_corrigee()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = FtpmAmd.Juger(Zen3, FtpmAmd.IdentifiantAmd, true, new DateTime(2023, 3, 1)).Single();
        c.Statut.Should().Be(RepairStatus.Observed);
        c.Explication.Should().Contain("les notes du BIOS");
    }

    // Notre règle : on vérifie que le fTPM est le TPM actif. Un module TPM d'un autre fabricant n'est pas concerné.
    [Fact]
    public void Un_TPM_d_un_autre_fabricant_n_est_pas_concerne()
        => FtpmAmd.Juger(Zen3, 0x494E5443, true, new DateTime(2021, 1, 1)).Should().BeEmpty();

    [Fact]
    public void Un_Zen_4_ou_une_generation_non_reconnue_ne_dit_rien()
    {
        FtpmAmd.Juger(Zen4, FtpmAmd.IdentifiantAmd, true, new DateTime(2021, 1, 1)).Should().BeEmpty();
        FtpmAmd.Juger(null, FtpmAmd.IdentifiantAmd, true, new DateTime(2021, 1, 1)).Should().BeEmpty();
    }

    [Fact]
    public void TPM_non_lu_ou_date_non_lue_est_dit_non_lu()
    {
        FtpmAmd.Juger(Zen3, null, false, null).Single().Statut.Should().Be(RepairStatus.Unknown);
        FtpmAmd.Juger(Zen3, FtpmAmd.IdentifiantAmd, true, null).Single().Statut.Should().Be(RepairStatus.Unknown);
    }
}
