using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// Le rapport de diagnostic et l'export de la machine : ce que le joueur envoie quand il demande de l'aide.
public sealed partial class ScanViewModel
{
    // ── Le rapport de diagnostic ─────────────────────────────────────────────
    //
    // « Jamais de debogage a l'aveugle par mail. » Sans ce bouton, chaque joueur en difficulte
    // coute un echange par question, avec quelqu'un qui ne sait pas ou regarder.

    private ScanResult? _lastScan;

    /// <summary>« Ce que ta memoire rend », relu dans le magasin (jamais remesure ici) ; null sans
    /// reference, ou si elle date d'une autre configuration. La date voyage avec les chiffres : une
    /// mesure enregistree se lit autrement qu'une mesure du jour.</summary>
    private string? LigneMemoire(IReadOnlyList<MemoryModule> modules)
    {
        if (_memoryBaseline is null || _machineId is null) return null;
        var reference = _memoryBaseline.MesureCourante(_machineId(), modules);
        if (reference is null) return null;
        var ligne = string.Format(CultureInfo.CurrentCulture, _localize("Scan.Memory.Measured"),
            reference.TakenUtc.ToLocalTime().DateTime,
            reference.Measurement.BandwidthGBs, reference.Measurement.BandwidthSigmaPct,
            reference.Measurement.LatencyNs, reference.Measurement.LatencySigmaPct);

        // Une référence prise avant le garde « jeu lancé » peut décrire le partage avec un jeu
        // (mesurée le 16/09 sur la machine de référence : 12,7 Go/s au lieu de 33,4, une capture de Warzone 1 min 40 plus tard).
        var mention = MentionPriseEnJeu("Scan.Memory.PriseEnJeu", reference.TakenUtc, reference.JeuFermeVerifie);
        _memoireSuspecte = mention.Length > 0;
        return ligne + mention;
    }

    /// <summary>Une référence d'avant le garde « jeu lancé » qu'une capture de jeu du même scan accompagne dans le
    /// journal : la phrase qui le dit, sinon rien. Une référence prise sous le garde ne se soupçonne pas : un jeu
    /// lancé juste après la mesure laisserait une capture proche sans rien avoir faussé.</summary>
    private string MentionPriseEnJeu(string cle, DateTimeOffset prise, bool jeuFermeVerifie)
    {
        if (jeuFermeVerifie || _machineId is null) return "";
        IReadOnlyList<DrDxt.Core.PerformanceSample> journal;
        try { journal = _performanceJournal?.Lire() ?? []; }
        catch { journal = []; }
        return CaptureDuMemeScan(journal, prise, _machineId()) is { } capture
            ? string.Format(CultureInfo.CurrentCulture, _localize(cle),
                capture.GameId ?? "?", capture.TakenUtc.ToLocalTime().DateTime)
            : "";
    }

    /// <summary>Une capture de jeu à moins de cette durée de la référence appartient au même scan. Non chiffré
    /// au-delà des deux cas de la machine de référence : 1 min 40 (mémoire, 16/09) et 5 min (processeur, 11/09).
    /// Le soupçon ne vise que les références d'avant le garde : le prix d'un faux positif est une remesure.</summary>
    internal static readonly TimeSpan FenetreDuMemeScan = TimeSpan.FromMinutes(10);

    internal static DrDxt.Core.PerformanceSample? CaptureDuMemeScan(
        IReadOnlyList<DrDxt.Core.PerformanceSample> journal, DateTimeOffset reference, string machineId)
        => journal
            .Where(s => s.MachineId == machineId && s.AvgFps > 0 && (s.TakenUtc - reference).Duration() <= FenetreDuMemeScan)
            .OrderBy(s => (s.TakenUtc - reference).Duration())
            .FirstOrDefault();

    /// <summary>Le chemin du dernier rapport ecrit, montre a l'utilisateur pour qu'il le
    /// retrouve. Vide tant qu'aucun export n'a eu lieu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportedReport))]
    private string _lastReportPath = string.Empty;

    /// <summary>Vrai quand un rapport vient d'etre ecrit.</summary>
    public bool HasExportedReport => LastReportPath.Length > 0;

    /// <summary>Vrai quand l'export est possible ; sinon le bouton disparait au lieu de ne
    /// rien faire.</summary>
    public bool CanExportReport => _writeReport is not null;

    public string LabelReportWritten => _localize("Support.ReportWritten");
    public string LabelReportFailed => _localize("Support.ReportFailed");

    /// <summary>Le rapport du docteur tel que l'écran le montre, avant anonymisation.</summary>
    // Après un scan raté (ou pendant un scan), le scan d'avant ne se donne pas pour le dernier : le rapport dit « aucun scan ».
    private string RapportBrut(DateTimeOffset now) => DrDxt.Application.Support.DiagnosticReport.Build(
        State is ScanState.Failed or ScanState.Scanning ? null : _lastScan, _appVersion, now,
        State is ScanState.Failed or ScanState.Scanning ? null : _derniereSante, _localize,
        processeur: _dernierDeficitCpu,
        reseau: NetworkPathLabel is { } ligne ? new DrDxt.Measurement.Network.LigneReseau(ligne) : null,
        memoire: MemoryMeasuredLabel is { } memoire ? new DrDxt.Measurement.Memory.LigneMemoireMesuree(memoire) : null,
        derive: PerformanceDriftLabel is { } derive ? new DrDxt.Measurement.LigneDerive(derive) : null);

    /// <summary>
    /// Ecrit le rapport et dit ou. Ce fichier est fait pour etre colle dans un salon public : rien de ce qui
    /// designe ce PC ou son joueur n'y reste.
    /// </summary>
    [RelayCommand]
    private async Task ExportReportAsync()
    {
        if (_writeReport is null) return;
        MessageDExport = string.Empty;

        try
        {
            var now = DateTimeOffset.Now;
            // Ce fichier est fait pour être envoyé : il passe par l'anonymiseur de la machine exportée, celui de
            // toutes les sorties publiques (profil, nom du PC, adresses, numéros de série, courriels).
            // Le rapport se compose ici (il lit l'écran) ; la lecture WMI des valeurs de ce PC et l'anonymisation partent hors
            // du fil de l'interface : une dizaine de requêtes WMI figeaient la fenêtre au clic.
            var brut = RapportBrut(now);
            var text = await Task.Run(() => DrDxt.Detection.Repair.MachineExportee.AnonymiserTexte(brut,
                DrDxt.Detection.Repair.MachineExportee.ValeursDeCePc()));

            LastReportPath = _writeReport(
                DrDxt.Application.Support.DiagnosticReport.FileNameFor(now), text);
        }
        catch
        {
            // Disque plein, droits refuses : on le dit. Un bouton d'export qui ne fait rien
            // visiblement est pire que pas de bouton : le client croit avoir un fichier.
            LastReportPath = string.Empty;
            MessageDExport = _localize("Support.ReportFailed");
        }
    }

    /// <summary>L'échec du dernier export (rapport ou machine), dit près des boutons d'export ; effacé par un export.</summary>
    [ObservableProperty] private string _messageDExport = string.Empty;

    /// <summary>Le chemin de la dernière machine exportée. Vide tant qu'aucun export n'a eu lieu.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExportedMachine))]
    private string _lastMachineExportPath = string.Empty;

    public bool HasExportedMachine => LastMachineExportPath.Length > 0;

    /// <summary>Vrai quand l'export de la machine est branché ; sinon le bouton disparaît.</summary>
    public bool CanExportMachine => _exporterMachine is not null;

    public string LabelExportMachine => _localize("Machine.ExportButton");
    public string LabelMachineExportWritten => _localize("Machine.ExportWritten");

    /// <summary>
    /// Écrit ce que le scan a lu, anonymisé, dans un fichier que le joueur envoie lui-même.
    /// Rien ne part sur le réseau. Sans scan, ou si l'écriture échoue, on le dit : pas de chemin vers un fichier absent.
    /// </summary>
    [RelayCommand]
    private async Task ExporterMaMachineAsync()
    {
        if (_exporterMachine is null) return;
        LastMachineExportPath = string.Empty;
        MessageDExport = string.Empty;
        // Un scan raté ou en cours a déjà remplacé le matériel ou les réglages lus, pas les constats : l'export mêlerait
        // deux scans. Il ne part que d'un scan fini.
        if (State is ScanState.Failed or ScanState.Scanning)
        {
            MessageDExport = _localize("Machine.ExportScanRate");
            return;
        }
        try
        {
            if (_materielLu is not { } hw || _reglagesLus is not { } st) throw new InvalidOperationException("no scan read");
            var sante = _constatsLus ?? [];
            var exporter = _exporterMachine;
            // Hors du thread UI : l'export relit les numéros de série par WMI pour les retirer.
            LastMachineExportPath = await Task.Run(() => exporter(hw, st, sante));
        }
        catch
        {
            MessageDExport = _localize("Machine.ExportFailed");
        }
    }

    /// <summary>
    /// « la partie BIOS est optionnelle ».
    ///
    /// Sans cette phrase, un joueur qui n'ose pas entrer dans son BIOS croit qu'il rate la moitie
    /// du produit, et un joueur qui ose mais s'y perd ouvre un ticket. Ce qui distingue DrDXT n'est
    /// pas le guide, c'est la preuve qu'un changement a pris.
    /// </summary>
    public string LabelBiosOptional => _localize("Bios.Optional.Note");
}
