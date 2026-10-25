using System.IO;

namespace DrDxt.App.Platform;

/// <summary>
/// Pourquoi la réparation de Microsoft n'a pas abouti, lu dans <c>CBS.log</c>. Vu sur la machine de référence : DISM
/// /RestoreHealth rend 0x800f0915 (CBS_E_REPAIR_CONTENT_MISSING), 1 512 fichiers abîmés, tous dans des composants de
/// l'installation (10.0.26100.1591, juin 2025) que des versions plus récentes (8972, 9278) remplacent ; sfc ne trouve
/// rien. Windows Update ne distribue plus ces vieilles versions : relancer la même réparation tournait en rond.
/// Le geste est alors la réinstallation de Microsoft (Paramètres >
/// Système > Récupération > « Réinstaller maintenant »), qui garde fichiers, applications et réglages
/// (support.microsoft.com, « Fix issues by reinstalling the current version of Windows »).
/// </summary>
public static class CorruptionDuMagasin
{
    /// <param name="Complete">Vrai seulement si chaque ligne « Corrupt » de la session a été lue
    /// et que leur nombre retombe sur le total que CBS écrit lui-même. Une liste incomplète ne propose jamais un geste
    /// irréversible.</param>
    public sealed record Lecture(bool SourceIntrouvable, IReadOnlyList<string> Composants, bool Complete = true);

    /// <summary>Le geste que la carte propose. Réparer : rien ne dit que la réparation échoue. Réinstaller : elle échoue
    /// faute de source (sur la machine de référence : 1 512 fichiers, tous dans des versions d'installation).</summary>
    public enum Geste { Reparer, Reinstaller }

    public static Geste Choisir(Lecture lu)
        => !lu.SourceIntrouvable || lu.Composants.Count == 0 || !lu.Complete ? Geste.Reparer : Geste.Reinstaller;

    /// <summary>La dernière session de réparation du journal : les composants abîmés qu'elle a nommés, et si la
    /// source de réparation manquait. Rien de lisible : aucun composant, jamais une supposition.</summary>
    public static Lecture Lire(IReadOnlyList<string> lignes)
    {
        var fin = -1;
        for (var i = lignes.Count - 1; i >= 0; i--)
            if (lignes[i].Contains("Total Detected Corruption", StringComparison.Ordinal)) { fin = i; break; }
        if (fin < 0) return new(false, []);

        var debut = 0;
        for (var i = fin - 1; i >= 0; i--)
            if (lignes[i].Contains("Total Detected Corruption", StringComparison.Ordinal)) { debut = i + 1; break; }

        var composants = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int lues = 0, illisibles = 0;
        for (var i = debut; i < fin; i++)
        {
            if (!lignes[i].Contains("Corrupt\t", StringComparison.Ordinal) || !lignes[i].Contains("CSI ", StringComparison.Ordinal)) continue;
            var chemin = lignes[i].Split('\t', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
            var composant = chemin.Split('\\')[0];
            if (Decouper(composant) is null) { illisibles++; continue; }
            composants.Add(composant);
            lues++;
        }
        var total = System.Text.RegularExpressions.Regex.Match(lignes[fin], @"Total Detected Corruption:\s*(\d+)");
        var complete = illisibles == 0 && total.Success && int.Parse(total.Groups[1].Value) == lues;

        var source = false;
        for (var i = fin; i < lignes.Count && i < fin + 200; i++)
            if (lignes[i].Contains("CBS_E_REPAIR_CONTENT_MISSING", StringComparison.Ordinal)) { source = true; break; }
        return new(source, composants.ToList(), complete);
    }

    /// <summary>La lecture réelle : la fin de CBS.log (le fichier fait des dizaines de Mo) et les dossiers de WinSxS.
    /// Pas de journal : la réparation reste proposée ; une lecture qui lève est rattrapée par l'appelant.</summary>
    public static Geste SurCetteMachine()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var journal = Path.Combine(windows, "Logs", "CBS", "CBS.log");
        if (!File.Exists(journal)) return Geste.Reparer;
        using var flux = new FileStream(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // 8 Mo de fin suffisent à une session de réparation (1 512 lignes sur la machine de référence). Une session plus
        // longue se lit incomplète (Complete = faux) : la réparation reste proposée, jamais la réinstallation.
        flux.Seek(Math.Max(0, flux.Length - 8 * 1024 * 1024), SeekOrigin.Begin);
        using var lecteur = new StreamReader(flux);
        var lignes = lecteur.ReadToEnd().Split('\n');
        var lu = Lire(lignes);
        return Choisir(lu);
    }

    // arch_nom_signataire_version_langue_empreinte ; le nom peut contenir des « _ », on lit donc depuis la fin.
    private static (string Cle, Version Version)? Decouper(string composant)
    {
        var p = composant.Split('_');
        if (p.Length < 6 || !Version.TryParse(p[^3], out var version)) return null;
        return (string.Join('_', p[..^3]) + "_" + p[^2], version);
    }
}
