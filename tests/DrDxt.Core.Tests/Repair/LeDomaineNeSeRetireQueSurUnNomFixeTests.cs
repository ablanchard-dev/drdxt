using System.Globalization;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests.Repair;

// Le domaine tombe quand le nom traduit le dit déjà. Un nom variable (service, carte, disque) qui contient le mot par
// hasard garde son domaine : « Remote Desktop Services » n'est pas « les services ».
public sealed class LeDomaineNeSeRetireQueSurUnNomFixeTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Un_nom_fixe_qui_dit_son_domaine_le_remplace()
    {
        using var _ = Docteur.Portee(En);
        RepairLibelles.Ligne("WINDOWS UPDATE", "en attente").Should().Be("Windows updates waiting");
    }

    [Fact]
    public void Un_nom_variable_garde_son_domaine()
    {
        using var _ = Docteur.Portee(En);
        RepairLibelles.Ligne("SERVICES", "Remote Desktop Services").Should().Be("Services · Remote Desktop Services");
    }
}
