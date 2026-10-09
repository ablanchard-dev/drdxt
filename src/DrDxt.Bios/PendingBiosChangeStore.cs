using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// File-backed <see cref="IPendingBiosChangeStore"/>. The marker lives as a single JSON
/// file (default: <c>%LOCALAPPDATA%\DrDXT\pending-bios-change.json</c>). All filesystem
/// access goes through an injectable <see cref="IBiosMarkerFile"/> seam so tests fake
/// I/O (atomicity, corruption, faults) without touching the real disk.
///
/// Guarantees:
///   • write is ATOMIC — serialize, write a temp file, then move-with-overwrite onto
///     the target (the move is the atomic publish; a reader never sees a torn file).
///   • read is FAIL-CLEAN — absent file → <c>Ok(null)</c> "nothing pending"; corrupt
///     content (bad JSON, empty targets, an Unknown expectation, a blank machine id, a
///     non-positive timestamp) → <see cref="ErrorCodes.BiosPendingChangeCorrupt"/>;
///     an I/O fault → <see cref="ErrorCodes.BiosPendingChangeIo"/>. Never throws.
///   • This never writes the BIOS — it persists/reads an OS file only.
/// </summary>
public sealed class PendingBiosChangeStore : IPendingBiosChangeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Enums as strings so an unknown future enum value fails the read cleanly
        // (a corrupt marker) instead of silently mapping to the zero member.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IBiosMarkerFile _file;

    /// <summary>The default marker path under the per-user local app data root.</summary>
    public static string DefaultPath()
        => DrDxt.Core.DossierDonnees.Chemin("pending-bios-change.json");

    /// <summary>Production store writing under <c>%LOCALAPPDATA%\DrDXT\</c>.</summary>
    public PendingBiosChangeStore()
        : this(new FileSystemBiosMarkerFile(DefaultPath())) { }

    /// <summary>Constructs the store over an explicit file seam (tests inject a fake).</summary>
    public PendingBiosChangeStore(IBiosMarkerFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <inheritdoc />
    public Result Save(PendingBiosChange change)
    {
        // Defensive: never persist a marker that would read back as corrupt.
        var invalid = Validate(change);
        if (invalid is not null)
            return Result.Fail(ErrorCodes.BiosPendingChangeCorrupt, invalid);

        try
        {
            var json = JsonSerializer.Serialize(MarkerDto.From(change), JsonOptions);
            return _file.WriteAtomic(json);
        }
        catch (Exception ex)
        {
            // Serialization itself should not throw for a validated change, but we wrap
            // the boundary regardless — no exception ever escapes the store.
            return Result.Fail(ErrorCodes.BiosPendingChangeIo, $"Marker serialize failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Result<PendingBiosChange?> Load()
    {
        var read = _file.Read();
        if (!read.IsOk)
            return Result<PendingBiosChange?>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        // Absent marker → clean "nothing pending".
        if (read.Value is null)
            return Result<PendingBiosChange?>.Ok(null);

        MarkerDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<MarkerDto>(read.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<PendingBiosChange?>.Fail(
                ErrorCodes.BiosPendingChangeCorrupt, $"Marker JSON is malformed: {ex.Message}");
        }

        if (dto is null)
            return Result<PendingBiosChange?>.Fail(
                ErrorCodes.BiosPendingChangeCorrupt, "Marker JSON deserialized to null.");

        var change = dto.ToDomainOrNull(out var reason);
        if (change is null)
            return Result<PendingBiosChange?>.Fail(ErrorCodes.BiosPendingChangeCorrupt, reason!);

        // Re-validate the materialized change (defence in depth against a hand-edited file).
        var invalid = Validate(change);
        if (invalid is not null)
            return Result<PendingBiosChange?>.Fail(ErrorCodes.BiosPendingChangeCorrupt, invalid);

        return Result<PendingBiosChange?>.Ok(change);
    }

    /// <inheritdoc />
    public Result Clear() => _file.Delete();

    /// <summary>Returns a corruption reason, or null when the change is a valid marker.</summary>
    private static string? Validate(PendingBiosChange change)
    {
        if (change is null) return "Pending change is null.";
        if (change.Targets is null || change.Targets.Count == 0)
            return "Pending change has no targets.";
        if (string.IsNullOrWhiteSpace(change.MachineId))
            return "Pending change has an empty machine id.";
        if (change.CreatedUtc <= DateTimeOffset.UnixEpoch)
            return "Pending change has a non-positive timestamp.";

        foreach (var t in change.Targets)
        {
            if (t is null) return "Pending change has a null target.";
            if (!Enum.IsDefined(typeof(FirmwareSettingKind), t.Setting))
                return $"Pending change has an unknown setting '{(int)t.Setting}'.";
            // An expectation must be concrete — Unknown is not a verifiable target.
            if (t.ExpectedState == TriState.Unknown)
                return $"Pending change target '{t.Setting}' has an Unknown expectation.";
            if (!Enum.IsDefined(typeof(TriState), t.ExpectedState))
                return $"Pending change target '{t.Setting}' has an invalid expectation.";
        }

        return null;
    }

    // ── On-disk DTO: an explicit shape we control (not the domain record), so a
    //    malformed/hostile file deserializes into something we then validate, rather
    //    than binding straight onto the domain. Enums are strings (see JsonOptions). ──
    private sealed record MarkerDto(
        List<TargetDto> Targets,
        DateTimeOffset CreatedUtc,
        string MachineId)
    {
        public static MarkerDto From(PendingBiosChange c) => new(
            c.Targets.Select(t => new TargetDto(t.Setting, t.ExpectedState, t.ObservedBefore)).ToList(),
            c.CreatedUtc,
            c.MachineId);

        public PendingBiosChange? ToDomainOrNull(out string? reason)
        {
            if (Targets is null || Targets.Count == 0)
            {
                reason = "Marker has no targets.";
                return null;
            }

            if (string.IsNullOrWhiteSpace(MachineId))
            {
                reason = "Marker has an empty machine id.";
                return null;
            }

            var targets = new List<PendingSettingTarget>(Targets.Count);
            foreach (var t in Targets)
            {
                if (t is null)
                {
                    reason = "Marker has a null target.";
                    return null;
                }

                targets.Add(new PendingSettingTarget(t.Setting, t.ExpectedState, t.ObservedBefore));
            }

            reason = null;
            return new PendingBiosChange(targets, CreatedUtc, MachineId);
        }
    }

    // ObservedBefore est OPTIONNEL/en queue : un marqueur ecrit par une version anterieure
    // se relit avec null, et le juge retombe alors sur la comparaison TriState -- le
    // comportement d'avant, jamais un verdict fabrique.
    //
    // Corrige le 26/08. Ce DTO n'avait pas ce champ : la grandeur mesuree etait jetee a
    // l'ecriture et reconstruite a null a la relecture. Comme BiosLoopVerifier ne voit les
    // cibles que par Load(), le juge par delta ne se declenchait jamais apres un redemarrage
    // -- c'est-a-dire dans le seul moment ou la boucle tourne. Le lot etait inerte en
    // production, et les tests le rataient parce qu'ils injectent la cible par FakeStore et ne
    // passent jamais par la serialisation. D'ou le test d'aller-retour ajoute avec ce correctif.
    private sealed record TargetDto(FirmwareSettingKind Setting, TriState ExpectedState, long? ObservedBefore = null);
}
