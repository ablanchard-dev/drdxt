using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Measurement.Memory;

/// <summary>
/// La mesure de reference d'une machine : ce a quoi un apres se comparera.
/// <paramref name="ConfiguredMts"/> voyage avec elle parce qu'un A/B dont la frequence a change
/// ne compare pas deux fois la meme configuration -- et c'est justement le changement qu'on teste.
/// <paramref name="JeuFermeVerifie"/> (26/09/2026) : vrai pour une mesure prise par une version qui refuse de
/// mesurer jeu lancé ; faux ou absent pour une référence d'avant ce garde.
/// </summary>
public sealed record MemoryBaseline(
    string MachineId,
    DateTimeOffset TakenUtc,
    int ConfiguredMts,
    MemoryMeasurement Measurement,
    bool JeuFermeVerifie = false);

/// <summary>Le fichier sous le magasin. Meme couture que le marqueur BIOS : les tests
/// injectent un faux et n'ecrivent jamais sur le disque.</summary>
public interface IMemoryBaselineFile
{
    Result WriteAtomic(string json);
    Result<string?> Read();
}

/// <summary>
/// Le magasin de la mesure de reference.
///
/// Il porte une garantie qui n'est pas cosmetique : une reference prise sur une autre machine est
/// ignoree comme si elle n'existait pas. Un disque voyage -- image systeme, restauration, second
/// PC -- et comparer la memoire d'une machine a la reference d'une autre rendrait un delta invente
/// de toutes pieces, avec l'air d'un resultat.
///
/// Lecture FAIL-CLEAN : fichier absent = <c>Ok(null)</c> (rien de mesure encore), contenu illisible
/// = echec nomme. Il ne leve jamais.
/// </summary>
public sealed class MemoryBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IMemoryBaselineFile _file;

    public MemoryBaselineStore(IMemoryBaselineFile file)
        => _file = file ?? throw new ArgumentNullException(nameof(file));

    public Result Save(MemoryBaseline baseline)
    {
        // Defensif, comme le magasin BIOS : on n'ecrit jamais une reference qui se relirait
        // comme corrompue.
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
    /// La reference de cette machine, ou <c>null</c> quand il n'y en a pas -- y compris quand le
    /// fichier decrit une autre machine.
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

        // Le refus vit ici, la ou tous les appelants passent. `JsonSerializer` remplit un record
        // sans se plaindre d'un champ absent : un fichier a moitie ecrit se relit en objet valide
        // dont la mesure vaut null, et c'est le premier appelant qui la dereference qui tombe --
        // en plein scan. Une reference amputee est CORROMPUE, pas « presque bonne ».
        var invalide = Invalide(baseline);
        if (invalide is not null)
            return Result<MemoryBaseline?>.Fail(ErrorCodes.MemoryBaselineCorrupt, invalide);

        return Result<MemoryBaseline?>.Ok(
            string.Equals(baseline!.MachineId, machineId, StringComparison.OrdinalIgnoreCase)
                ? baseline
                : null);
    }

    /// <summary>Ce qui manque, ou <c>null</c> quand la reference est complete. Sert des deux
    /// cotes : on n'ecrit jamais une reference qui se relirait comme corrompue.</summary>
    private static string? Invalide(MemoryBaseline? b)
    {
        if (b is null) return "référence vide";
        if (string.IsNullOrWhiteSpace(b.MachineId)) return "identifiant de machine absent";
        if (b.Measurement is null) return "mesure absente";
        if (b.Measurement.Staircase is null) return "escalier absent";
        if (!double.IsFinite(b.Measurement.BandwidthGBs) || b.Measurement.BandwidthGBs <= 0)
            return "debit absent ou absurde";
        if (!double.IsFinite(b.Measurement.LatencyNs) || b.Measurement.LatencyNs <= 0)
            return "latence absente ou absurde";
        // Une dispersion absente se relit 0, et MemoryVerdict en tire un seuil de 3 sigma nul :
        // tout ecart de bruit deviendrait « significatif ». Dix passes ne rendent jamais 0.
        if (!double.IsFinite(b.Measurement.BandwidthSigmaPct) || b.Measurement.BandwidthSigmaPct <= 0
            || !double.IsFinite(b.Measurement.LatencySigmaPct) || b.Measurement.LatencySigmaPct <= 0)
            return "dispersion absente ou absurde";
        return null;
    }
}
