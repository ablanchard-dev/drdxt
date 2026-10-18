using System.Diagnostics.Eventing.Reader;
using System.Runtime.Versioning;
using System.Text.Json;
using DrDxt.Detection.Repair;
using DrDxt.Reconciler.Manifest;
using Microsoft.Extensions.Hosting;

namespace DrDxt.ReconcilerHost;

/// <summary>
/// Le témoin d'incident porté par le service : au démarrage, un témoin pour chaque arrêt brutal ou incident noté depuis
/// le dernier ; ensuite, un témoin dès qu'un déclencheur arrive dans le journal Système. Il lit les journaux et écrit un
/// petit fichier sous %ProgramData%\DrDxt\temoins, dossier et fichiers possédés par Administrateurs, lisibles par
/// l'App sans droits, jamais inscriptibles par le compte. Aucune tâche planifiée, aucun script : du code compilé du
/// service. Rien d'autre n'est écrit.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TemoinDIncidentWorker : BackgroundService
{
    private readonly string _dossier;
    private readonly Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<TemoinDIncident.Ligne>> _lireFenetre;
    private readonly object _verrou = new();
    private DateTimeOffset? _dernier;

    public TemoinDIncidentWorker() : this(DossierParDefaut()) { }

    internal TemoinDIncidentWorker(string dossier, Func<DateTimeOffset, DateTimeOffset, IReadOnlyList<TemoinDIncident.Ligne>>? lireFenetre = null)
        => (_dossier, _lireFenetre) = (dossier, lireFenetre ?? TemoinDIncident.LireFenetre);

    public static string DossierParDefaut() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DrDxt", TemoinDIncident.Dossier);

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // %ProgramData% laisse tout compte y créer un dossier : le parent (DrDxt) est durci d'abord, point de reparse
        // refusé, pour que les témoins et leurs suppressions ne puissent viser que ce dossier-ci.
        if (!TrustedManifestRoot.EnsureCreatedHardened(Path.GetDirectoryName(_dossier)!).IsOk
            || !TrustedManifestRoot.EnsureCreatedHardened(_dossier).IsOk) return Task.CompletedTask;
        var deja = TemoinDIncident.LireTous(_dossier) ?? [];
        _dernier = deja.FirstOrDefault()?.Incident;
        // Ce qui s'est passé pendant que le service ne tournait pas : au plus une semaine en arrière.
        var depuis = _dernier ?? DateTimeOffset.Now.AddDays(-7);
        try
        {
            using var lecteur = new EventLogReader(new EventLogQuery("System", PathType.LogName, TemoinDIncident.Requete(depuis)));
            var lus = new List<(DateTimeOffset, string)>();
            for (var e = lecteur.ReadEvent(); e is not null; e = lecteur.ReadEvent())
                using (e)
                    if (e.TimeCreated is { } t && TemoinDIncident.Genre(e.ProviderName, e.Id) is { } g) lus.Add((new DateTimeOffset(t), g));
            foreach (var (quand, genre) in lus.Where(l => l.Item1 > depuis).OrderBy(l => l.Item1)) Temoigner(quand, genre);
        }
        catch (EventLogException) { }

        // Puis en direct : l'abonnement vit jusqu'à l'arrêt du service.
        var veille = new EventLogWatcher(new EventLogQuery("System", PathType.LogName, TemoinDIncident.Requete()));
        veille.EventRecordWritten += (_, a) =>
        {
            using var e = a.EventRecord;
            if (e?.TimeCreated is { } t && TemoinDIncident.Genre(e.ProviderName, e.Id) is { } g) Temoigner(new DateTimeOffset(t), g);
        };
        try { veille.Enabled = true; }
        catch (EventLogException) { veille.Dispose(); return Task.CompletedTask; }
        stoppingToken.Register(veille.Dispose);
        return Task.CompletedTask;
    }

    /// <summary>Un témoin. Appelé aussi depuis le rappel du journal, sur un fil du pool : rien n'en sort, une exception y
    /// arrêterait tout le service. Un témoin raté est un témoin absent.</summary>
    internal void Temoigner(DateTimeOffset incident, string genre)
    {
        try { TemoignerSansGarde(incident, genre); }
        catch (Exception) { /* un témoin manqué ne doit jamais arrêter le service */ }
    }

    private void TemoignerSansGarde(DateTimeOffset incident, string genre)
    {
        lock (_verrou)
        {
            if (!TemoinDIncident.AEcrire(incident, _dernier)) return;
            var (debut, fin) = TemoinDIncident.Fenetre(genre, incident);
            var temoin = new TemoinDIncident.Temoin(incident, genre, debut, fin, _lireFenetre(debut, fin));
            var fichier = Path.Combine(_dossier, $"temoin-{incident.UtcDateTime:yyyyMMdd-HHmmss}.json");
            if (!TrustedManifestRoot.WriteTrustedFile(fichier, JsonSerializer.SerializeToUtf8Bytes(temoin)).IsOk) return;
            _dernier = incident;
            // Les plus anciens partent : le dossier ne grossit pas sans fin.
            foreach (var vieux in Directory.EnumerateFiles(_dossier, "temoin-*.json").OrderByDescending(f => f, StringComparer.Ordinal)
                         .Skip(TemoinDIncident.Gardes))
                try { File.Delete(vieux); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
