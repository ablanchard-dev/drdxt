using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace DrDxt.ReconcilerHost;

/// <summary>
/// L'hôte générique sous Windows écrit ses avertissements dans l'Observateur d'événements, et crée pour cela, au premier, une
/// source au nom de l'application dans le registre de la machine. Le service a son propre journal de diagnostic : il n'écrit
/// pas là, et ne laisse donc aucune source derrière lui.
/// </summary>
public static class JournalDEvenementsDuService
{
    public static void Retirer(IServiceCollection services)
    {
        foreach (var d in services.Where(d => d.ServiceType == typeof(ILoggerProvider) && d.ImplementationType == typeof(EventLogLoggerProvider)).ToList())
            services.Remove(d);
    }
}
