// -----------------------------------------------------------------------
// DrDXT reconciler host — entry point (THIN SHELL).
//
// All logic lives in the tested classes (ReconcileTickService / SingleFlightReconcile
// / FeatureUpdateTrigger / BootSessionMarker / Worker). This file only:
//   * builds the generic host,
//   * registers it to run as a Windows Service (UseWindowsService),
//   * wires the full production DI graph (AddDrDxtReconcilerHost),
//   * runs.
// -----------------------------------------------------------------------
using DrDxt.ReconcilerHost;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

// Run as a Windows Service when launched by the SCM; runs as a console app otherwise
// (so it can be smoke-tested interactively without installing the service).
builder.Services.AddWindowsService(options => options.ServiceName = "DrDxtReconciler");

builder.Services.AddDrDxtReconcilerHost();

using var host = builder.Build();
await host.RunAsync();
