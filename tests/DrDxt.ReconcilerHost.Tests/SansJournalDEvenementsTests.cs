using FluentAssertions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.EventLog;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>
/// L'hôte générique sous Windows écrit ses avertissements dans l'Observateur d'événements, et crée pour cela une source
/// « DrDxt.ReconcilerHost » que la désinstallation laissait. Le service a son propre journal de diagnostic : il n'en crée pas.
/// </summary>
public sealed class SansJournalDEvenementsTests
{
    [Fact]
    public void Le_service_n_ecrit_pas_dans_l_observateur_d_evenements()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.Should().Contain(d => d.ImplementationType == typeof(EventLogLoggerProvider), "témoin : l'hôte l'ajoute de lui-même");

        JournalDEvenementsDuService.Retirer(builder.Services);

        builder.Services.Should().NotContain(d => d.ImplementationType == typeof(EventLogLoggerProvider));
    }
}
