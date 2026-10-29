// Hôte réconciliateur DrDXT : point d'entrée, coquille mince. La logique vit dans les classes testées
// (ReconcileTickService, SingleFlightReconcile, FeatureUpdateTrigger, BootSessionMarker, Worker) ; ici, l'hôte générique
// en service Windows (UseWindowsService), le graphe DI de production (AddDrDxtReconcilerHost), et le lancement.
using DrDxt.ReconcilerHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// SYSTEM, avant toute lecture ou écriture : plus aucune jonction posée par un compte non administrateur n'est suivie
// (ConfianceDesRedirections). Indisponible (Windows d'avant 10 22H2), c'est dit au journal du service une fois l'hôte construit.
var confiance = DrDxt.Core.ConfianceDesRedirections.Activer();

// Le service tourne pendant les parties (température servie à l'App et au relevé, plafond d'envoi) : il passe après le jeu.
try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; }
catch (Exception) { /* une priorité refusée n'empêche pas le service */ }

var builder = Host.CreateApplicationBuilder(args);

// Tourne comme service Windows quand le SCM le lance ; sinon comme application console
// (pour un smoke test interactif sans installer le service).
builder.Services.AddWindowsService(options => options.ServiceName = "DrDxtReconciler");

// Aucune source dans l'Observateur d'événements : le service a son propre journal de diagnostic.
JournalDEvenementsDuService.Retirer(builder.Services);

builder.Services.AddDrDxtReconcilerHost();

using var host = builder.Build();
if (!confiance.IsOk)
    host.Services.GetService<DrDxt.Core.IDiagnosticsLog>()?.Change("redirection-trust", "off", "unavailable", "Windows");
await host.RunAsync();
