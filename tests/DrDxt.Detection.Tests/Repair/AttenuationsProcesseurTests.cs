using System.Globalization;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les protections contre l'exécution spéculative, décodées comme le module SpeculationControl de Microsoft, jamais un levier.
public sealed class AttenuationsProcesseurTests
{
    [Fact]
    public void Les_drapeaux_se_lisent_comme_le_module_de_Microsoft()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        var c = AttenuationsProcesseur.Juger(new(0x4001, 0x20));
        c.Explication.Should().Contain("active, avec retpoline").And.Contain("pas concerné");
        AttenuationsProcesseur.Juger(new(0x02, 0x15)).Explication.Should().Contain("stratégie du système").And.Contain("avec PCID");
    }

    // Notre règle : jamais de geste ni de remède sur une protection de sécurité, quelle que soit sa valeur.
    [Theory]
    [InlineData(0x00u, 0x10u)]
    [InlineData(0x01u, 0x11u)]
    [InlineData(0x04u, 0x00u)]
    public void Aucune_protection_n_est_jamais_proposee_a_la_coupe(uint speculation, uint kva)
    {
        var c = AttenuationsProcesseur.Juger(new(speculation, kva));
        c.Action.Should().Be(RepairAction.Aucune);
        c.Remede.Should().BeNull();
        c.Statut.Should().Be(RepairStatus.Observed);
    }

    // Relevé sur la machine de référence (i7-14700F) le 16/10/2026 : 201 = 0x7D8F93F9, 196 = 0x3020.
    [Fact]
    public void Le_releve_de_la_machine_de_reference_dit_une_protection_active_et_Meltdown_sans_objet()
    {
        using var _ = Docteur.Portee(CultureInfo.GetCultureInfo("fr-FR"));
        AttenuationsProcesseur.Juger(new(0x7D8F93F9, 0x3020)).Explication.Should().Contain("(Spectre variante 2) : active.")
            .And.Contain("pas concerné");
    }

    [Fact]
    public void Rien_de_lu_est_non_lu()
        => AttenuationsProcesseur.Juger(new(null, null)).Statut.Should().Be(RepairStatus.Unknown);
}
