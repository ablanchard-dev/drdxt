using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// La mesure de reference d'une machine, a laquelle un apres se compare. <paramref name="ConfiguredMts"/> voyage avec
/// elle : une frequence changee n'est plus la meme configuration. <paramref name="JeuFermeVerifie"/> : vrai pour une
/// version qui refuse de mesurer jeu lancé ; faux ou absent avant ce garde.
/// </summary>
public sealed record MemoryBaseline(
    string MachineId,
    DateTimeOffset TakenUtc,
    int ConfiguredMts,
    MemoryMeasurement Measurement,
    bool JeuFermeVerifie = false,
    // Les barrettes de la mesure (nombre et emplacements) : une barrette ajoutée ou déplacée à la même fréquence change le
    // nombre de canaux que le débit prouve. Absente d'une référence plus ancienne : sa configuration est inconnue.
    string? Barrettes = null);

/// <summary>Le fichier sous le magasin, couture comme le marqueur BIOS : les tests n'ecrivent pas sur le disque.</summary>
public interface IMemoryBaselineFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>
/// Le magasin de la mesure de reference. Une reference d'une autre machine est ignoree : un disque voyage (image,
/// restauration, second PC), et la comparer inventerait un delta. Absent = <c>Ok(null)</c>, illisible = echec nomme ;
/// ne leve jamais.
/// </summary>
public sealed class MemoryBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IMemoryBaselineFile _file;

    public MemoryBaselineStore(IMemoryBaselineFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    public Result Save(MemoryBaseline baseline)
    {
        // Comme le magasin BIOS : jamais une reference qui se relirait corrompue.
        var invalide = Invalide(baseline);
        if (invalide is not null)
            return Result.Fail(ErrorCodes.MemoryBaselineCorrupt, invalide);

        try
        {
            return _file.WriteAtomic(JsonSerializer.Serialize(baseline, JsonOptions));
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.MemoryBaselineIo, $"Serialisation impossible : {ex.Message}");
        }
    }

    /// <summary>
    /// La reference de cette machine, ou <c>null</c> (aucune, ou celle d'une autre machine).
    /// </summary>
    public Result<MemoryBaseline?> Read(string machineId)
    {
        var read = _file.Read();
        if (!read.IsOk) return Result<MemoryBaseline?>.Fail(read.Error!.Value.Code, read.Error!.Value.Message);
        if (read.Value is null) return Result<MemoryBaseline?>.Ok(null);

        MemoryBaseline? baseline;
        try
        {
            baseline = JsonSerializer.Deserialize<MemoryBaseline>(read.Value, JsonOptions);
        }
        catch (JsonException ex)
        {
            return Result<MemoryBaseline?>.Fail(ErrorCodes.MemoryBaselineCorrupt, ex.Message);
        }

        // Le refus vit ici, ou tous passent : `JsonSerializer` relit un fichier a moitie ecrit en objet « valide » a la
        // mesure null, qui tomberait en plein scan. Une reference amputee est corrompue.
        var invalide = Invalide(baseline);
        if (invalide is not null)
            return Result<MemoryBaseline?>.Fail(ErrorCodes.MemoryBaselineCorrupt, invalide);

        return Result<MemoryBaseline?>.Ok(
            string.Equals(baseline!.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
                ? baseline
                : null);
    }

    /// <summary>Ce qui manque, ou <c>null</c> quand la reference est complete ; a l'ecriture comme a la lecture.</summary>
    private static string? Invalide(MemoryBaseline? b)
    {
        if (b is null) return "empty reference";
        if (string.IsNullOrWhiteSpace(b.MachineId)) return "machine id missing";
        if (b.Measurement is null) return "measurement missing";
        if (b.Measurement.Staircase is null || b.Measurement.Staircase.Any(p => p is null)) return "staircase missing";
        if (!double.IsFinite(b.Measurement.BandwidthGBs) || b.Measurement.BandwidthGBs <= 0)
            return "bandwidth missing or absurd";
        if (!double.IsFinite(b.Measurement.LatencyNs) || b.Measurement.LatencyNs <= 0)
            return "latency missing or absurd";
        // Une dispersion absente se relit 0, un seuil de 3 sigma nul pour MemoryVerdict ; dix passes ne rendent jamais 0.
        if (!double.IsFinite(b.Measurement.BandwidthSigmaPct) || b.Measurement.BandwidthSigmaPct <= 0
            || !double.IsFinite(b.Measurement.LatencySigmaPct) || b.Measurement.LatencySigmaPct <= 0)
            return "dispersion absente ou absurde";
        return null;
    }
}
