using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// The filesystem boundary for the pending-BIOS-change marker, isolated so the store's
/// logic is fully testable without touching the real disk. Every method is fail-clean
/// (returns a <see cref="Result"/>, never throws): the I/O frontier is wrapped here so
/// the store above stays pure.
/// </summary>
public interface IBiosMarkerFile
{
    /// <summary>
    /// Writes <paramref name="json"/> ATOMICALLY (temp file + move-with-overwrite onto
    /// the target — a reader never observes a torn file). Fails clean on an I/O fault.
    /// </summary>
    Result WriteAtomic(string json);

    /// <summary>
    /// Reads the marker. <c>Ok(null)</c> when the file does not exist (nothing pending);
    /// <c>Ok(content)</c> when present; <c>Fail</c> on an I/O fault. Never throws.
    /// </summary>
    Result<string?> Read();

    /// <summary>Deletes the marker if present (idempotent). Fails clean on an I/O fault.</summary>
    Result Delete();
}

/// <summary>
/// Real-filesystem <see cref="IBiosMarkerFile"/>. Writes are atomic (temp + move); the
/// parent directory is created on demand. All boundaries are wrapped — no exception
/// escapes. The temp file sits next to the target (same volume) so the move is a true
/// atomic rename, not a cross-volume copy.
/// </summary>
public sealed class FileSystemBiosMarkerFile : IBiosMarkerFile
{
    private readonly string _path;

    public FileSystemBiosMarkerFile(string path)
        => _path = string.IsNullOrWhiteSpace(path)
            ? throw new ArgumentException("Marker path is required.", nameof(path))
            : path;

    // The atomic write has ONE implementation (Core); it used to be copied here.
    private FichierJsonAtomique File_ => new(_path, ErrorCodes.BiosPendingChangeIo);

    /// <inheritdoc />
    public Result WriteAtomic(string json) => File_.WriteAtomic(json);

    /// <inheritdoc />
    public Result<string?> Read() => File_.Read();

    /// <inheritdoc />
    public Result Delete()
    {
        try
        {
            if (File.Exists(_path))
                File.Delete(_path);
            return Result.Ok();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.BiosPendingChangeIo, $"Marker delete failed: {ex.Message}");
        }
    }
}
