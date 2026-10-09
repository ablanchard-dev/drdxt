using DrDxt.Server.Manifest;

var builder = WebApplication.CreateBuilder(args);

// 24/09/2026 : sous Windows, CreateBuilder écrit aussi dans le journal Application de la machine.
// Chaque passage des tests (environnement Development) y posait l'avertissement « graine BLANK »
// sous l'ID 1000, celui des plantages d'applications : un faux signal sur le PC de celui qui
// teste. Hors production, rien dans le journal de la machine.
if (!builder.Environment.IsProduction() && OperatingSystem.IsWindows())
    builder.Logging.AddFilter<Microsoft.Extensions.Logging.EventLog.EventLogLoggerProvider>(_ => false);

// --- Configuration (secret via ENV only) ----------------------------------------------------
// 23/09/2026 — DrDXT est gratuit : ce serveur ne sert plus que le CANAL DE MISE A JOUR. Le webhook
// de paiement, le client Keygen, la base des commandes et la file de revocation sont retires.
// Le seul secret restant est la graine de signature des manifestes (ManifestSigning__Ed25519PrivateSeedBase64),
// jamais commitee : appsettings.json ne porte que des emplacements vides.
var manifestSigningOptions = builder.Configuration.GetSection(ManifestSigningOptions.SectionName).Get<ManifestSigningOptions>()
                             ?? new ManifestSigningOptions();

builder.Services.AddSingleton(manifestSigningOptions);
builder.Services.AddSingleton<IManifestSigningService, Ed25519ManifestSigningService>();

var app = builder.Build();

var signatureConfiguree = !string.IsNullOrWhiteSpace(manifestSigningOptions.Ed25519PrivateSeedBase64);
if (!signatureConfiguree)
{
    app.Logger.LogCritical(
        "ManifestSigning:Ed25519PrivateSeedBase64 is BLANK — every update manifest request answers 503. Set the ManifestSigning__Ed25519PrivateSeedBase64 environment variable.");
}

// LU — le canal de mise a jour. Il ne sert jamais un manifeste non signe (503 plutot), et repond 204
// sur un canal sans version publiee — que le client lit comme « rien a offrir », pas comme une panne.
app.MapGet("/updates/{channel}/manifest",
    (string channel, IConfiguration config, IManifestSigningService signer)
        => UpdateManifestEndpoint.Handle(channel, config, signer));

// /health DIT CE QU'IL VOIT : sans graine de signature, le canal ne peut rien servir, donc « degraded ».
// Deduit, jamais ecrit en dur : un statut constant ne peut pas dire non.
app.MapGet("/health", () => Results.Ok(new { status = signatureConfiguree ? "ok" : "degraded" }));

await app.RunAsync();

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
