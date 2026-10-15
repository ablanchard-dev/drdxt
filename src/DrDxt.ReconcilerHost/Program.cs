// -----------------------------------------------------------------------
// Hôte réconciliateur DrDXT : point d'entrée (coquille mince).
//
// Toute la logique vit dans les classes testées (ReconcileTickService / SingleFlightReconcile
// / FeatureUpdateTrigger / BootSessionMarker / Worker). Ce fichier ne fait que :
//   * construire l'hôte générique,
//   * l'enregistrer pour tourner comme service Windows (UseWindowsService),
//   * brancher tout le graphe DI de production (AddDrDxtReconcilerHost),
//   * le lancer.
// -----------------------------------------------------------------------
using DrDxt.ReconcilerHost;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Tourne comme service Windows quand le SCM le lance ; sinon comme application console
// (pour un smoke test interactif sans installer le service).
builder.Services.AddWindowsService(options => options.ServiceName = "DrDxtReconciler");

builder.Services.AddDrDxtReconcilerHost();

using var host = builder.Build();
await host.RunAsync();
