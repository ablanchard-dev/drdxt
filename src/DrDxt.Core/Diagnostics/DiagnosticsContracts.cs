namespace DrDxt.Core;

// --- Diagnostics / audit log ---

public interface IDiagnosticsLog
{
    void Change(string tweakId, string from, string to, string actor /*"DrDXT"|"Windows"*/);
    Task<Result<string>> ExportAsync(); // support button
}
