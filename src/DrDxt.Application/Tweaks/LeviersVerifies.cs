using System.Text.Json;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks;

/// <summary>
/// Les leviers que « Vérifier DrDXT sur mon PC » a posés, relus, rendus et relus sur cette machine.
/// La liste « ce que DrDXT fait ici » les dit « vérifié ici ».
/// Réécrite à chaque vérification (un levier tenté et pas validé n'est plus « vérifié »), et liée à
/// l'empreinte du matériel (un autre processeur, une autre carte ou un autre pilote : plus rien n'est « vérifié »).
/// Illisible ou d'une autre machine : vide (rien n'est dit vérifié sans preuve).
/// </summary>
public static class LeviersVerifies
{
    private sealed record Contenu(string Empreinte, string[] Ids);

    /// <summary>Le processeur, chaque carte et son pilote : ce qui, s'il change, rend une vérification caduque.</summary>
    public static string Empreinte(HardwareInventory? hw)
        => hw is null ? "" : string.Join(" | ", new[] { hw.Cpu.Model }.Concat(hw.Gpus.Select(g => $"{g.Model} {g.DriverVersion}")));

    public static IReadOnlySet<string> Lire(string chemin, string empreinte)
    {
        try
        {
            return empreinte.Length > 0 && File.Exists(chemin) && JsonSerializer.Deserialize<Contenu>(File.ReadAllText(chemin)) is { } c
                   && string.Equals(c.Empreinte, empreinte, StringComparison.Ordinal)
                ? c.Ids.ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
        }
        catch (Exception) { return new HashSet<string>(StringComparer.Ordinal); }
    }

    /// <param name="essayes">Les leviers que la vérification a tentés (validés ou non) : ils perdent leur ancien état.</param>
    public static void Enregistrer(string chemin, string empreinte, IEnumerable<string> essayes, IEnumerable<string> valides)
    {
        if (empreinte.Length == 0) return;   // matériel non lu : rien ne s'enregistre comme vérifié
        var tentes = essayes.ToHashSet(StringComparer.Ordinal);
        var tous = Lire(chemin, empreinte).Where(id => !tentes.Contains(id)).Concat(valides)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        // Fichier du compte : jamais à travers un nom posé d'avance en lien (l'App peut tourner élevée).
        var ecrit = new DrDxt.Core.FichierJsonAtomique(chemin, DrDxt.Core.ErrorCodes.IoFailure)
            .WriteAtomic(JsonSerializer.Serialize(new Contenu(empreinte, tous)));
        if (!ecrit.IsOk) throw new IOException(ecrit.Error!.Value.Message);
    }
}
