namespace DrDxt.Core;

// --- Diagnostic / journal d'audit ---

public interface IDiagnosticsLog
{
    void Change(string tweakId, string from, string to, string actor /*"DrDXT"|"Windows"*/);
    Task<Result<string>> ExportAsync(); // bouton du support
}
