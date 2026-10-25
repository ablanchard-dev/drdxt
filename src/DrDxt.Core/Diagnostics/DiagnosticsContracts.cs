namespace DrDxt.Core;

// --- Diagnostic / journal d'audit ---

public interface IDiagnosticsLog
{
    void Change(string tweakId, string from, string to, string actor /*"DrDXT"|"Windows"*/);
    /// <summary>Copie le journal tel quel (valeurs de registre, chemins du compte) et rend le chemin de la copie. Tout texte
    /// exporté hors de la machine passe d'abord par MachineExportee.AnonymiserTexte, l'anonymiseur unique (garde :
    /// LeJournalExporteNeSortQueParLAnonymiseurTests).</summary>
    Task<Result<string>> ExportAsync();
}
