using System.Text.Json;
using System.Text.Json.Serialization;
using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// <see cref="IPendingBiosChangeStore"/> sur un fichier JSON (<c>%LOCALAPPDATA%\DrDXT\pending-bios-change.json</c>),
/// par la couture <see cref="IBiosMarkerFile"/> (les tests simulent atomicité, corruption et pannes).
///   • écriture atomique : un temporaire déplacé sur la cible, jamais un fichier tronqué ;
///   • absent → <c>Ok(null)</c> ; corrompu (JSON invalide, cibles vides, attente Unknown, identifiant machine vide,
///     horodatage non positif) → <see cref="ErrorCodes.BiosPendingChangeCorrupt"/> ; panne →
///     <see cref="ErrorCodes.BiosPendingChangeIo"/>. Ne lève jamais ;
///   • n'écrit jamais le BIOS, seulement ce fichier.
/// </summary>
public sealed class PendingBiosChangeStore : IPendingBiosChangeStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // Énumérations en chaînes : une valeur inconnue rend « marqueur corrompu », jamais le membre zéro en silence.
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IBiosMarkerFile _file;

    /// <summary>Le chemin du marqueur par défaut, sous les données locales de l'utilisateur.</summary>
    public static string DefaultPath()
        => DrDxt.Core.DossierDonnees.Chemin("pending-bios-change.json");

    /// <summary>Le magasin de production, qui écrit sous <c>%LOCALAPPDATA%\DrDXT\</c>.</summary>
    public PendingBiosChangeStore()
        : this(new FileSystemBiosMarkerFile(DefaultPath())) { }

    /// <summary>Construit le magasin sur une couture de fichier explicite (les tests injectent un faux).</summary>
    public PendingBiosChangeStore(IBiosMarkerFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    /// <inheritdoc />
    public Result Save(PendingBiosChange change)
    {
        // Défensif : ne jamais écrire un marqueur qui se relirait corrompu.
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
            // Aucune exception ne sort du magasin, même d'une sérialisation qui ne devrait pas lever.
            return Result.Fail(ErrorCodes.BiosPendingChangeIo, $"Marker serialize failed: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public Result<PendingBiosChange?> Load()
    {
        var read = _file.Read();
        if (!read.IsOk)
            return Result<PendingBiosChange?>.Fail(read.Error!.Value.Code, read.Error.Value.Message);

        // Marqueur absent : proprement « rien en attente ».
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

        // Revalider le changement reconstruit (défense en profondeur contre un fichier édité à la main).
        var invalid = Validate(change);
        if (invalid is not null)
            return Result<PendingBiosChange?>.Fail(ErrorCodes.BiosPendingChangeCorrupt, invalid);

        return Result<PendingBiosChange?>.Ok(change);
    }

    /// <inheritdoc />
    public Result Clear() => _file.Delete();

    /// <summary>Rend la raison de la corruption, ou null quand le changement est un marqueur valide.</summary>
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
            // Une attente doit être concrète : Unknown n'est pas une cible vérifiable.
            if (t.ExpectedState == TriState.Unknown)
                return $"Pending change target '{t.Setting}' has an Unknown expectation.";
            if (!Enum.IsDefined(typeof(TriState), t.ExpectedState))
                return $"Pending change target '{t.Setting}' has an invalid expectation.";
        }

        return null;
    }

    // ── DTO sur disque, pas le domaine : un fichier malformé ou hostile se désérialise dans une forme validée ensuite.
    //    Énumérations en chaînes (voir JsonOptions). ──
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

    // ObservedBefore, optionnel : un marqueur plus ancien se relit null, et le juge retombe sur la comparaison TriState,
    // jamais un verdict fabrique. Il doit survivre a la serialisation : BiosLoopVerifier ne voit les cibles que par Load(),
    // et sans lui le juge par delta ne se declencherait jamais apres un redemarrage. Un test d'aller-retour le garde (un
    // FakeStore ne passe pas par la serialisation).
    private sealed record TargetDto(FirmwareSettingKind Setting, TriState ExpectedState, long? ObservedBefore = null);
}
