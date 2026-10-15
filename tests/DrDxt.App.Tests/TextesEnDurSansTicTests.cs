using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Les ressources et l'audit sont gardes, mais deux phrases affichees vivaient en dur dans
/// les ViewModels (« est INCONNU, pas vide », « categorie — accroche »). Cette garde lit les litteraux
/// des ViewModels : ni pictogramme, ni tiret cadratin, ni mot en capitales hors sigles.
/// </summary>
public sealed class TextesEnDurSansTicTests
{
    private static readonly HashSet<string> Sigles = new(StringComparer.Ordinal)
    {
        // Le nom du réglage « DRAM Frequency » tel que le menu du BIOS l'écrit.
        "DRAM",
        "BIOS", "NVIDIA", "JEDEC", "ASUS", "EXPO", "DWORD", "SELECT", "FROM", "SOFTWARE", "MMCSS", "NVML", "SMART", "UEFI",
        // cles internes du journal, traduites a l'affichage par RepairFindingViewModel.LibelleDomaine
        "STABILITE", "POWER", "SERVICES", "STOCKAGE", "DISPLAY", "DRIVER", "PAGEFILE", "THERMIQUE", "LATENCE", "RESEAU",
        // L'ordre des familles sur la carte (ScanViewModel.OrdreDesFamilles), memes cles.
        "WINDOWS", "UPDATE", "PERIPHERIQUES", "USB", "DEFENDER", "TACHES", "DEMARRAGE", "BCD",
    };

    // Meme liste que AuditSansTicDEcritureTests (Detection) : les cles du journal n'en font pas partie.
    private static readonly string[] MotsSansAccent =
    [
        "etait", "etaient", "ete", "deja", "meme", "precedente", "examinee", "verifies", "etats",
        "depend", "reparer", "repondre", "cesse", "entree", "mesuree", "systeme", "probleme", "tres",
        "a change", "l'a change", "etre", "defaut", "memoire vive",
    ];

    [Fact]
    public void Les_phrases_en_dur_des_ViewModels_n_ont_ni_pictogramme_ni_tiret_ni_capitales()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var racine = Path.Combine(dir!.FullName, "src", "DrDxt.App", "ViewModels");

        var fautes = new List<string>();
        var lus = 0;
        // Cette ligne ne lisait que le premier niveau de `ViewModels`.
        // Experience : un fichier `ViewModels/Sous/Fautif.cs` portant « categorie — accroche »
        // est passe VERT. La garde ne dormait pas par erreur de motif, mais par erreur de
        // PORTEE : elle couvrait tout tant que le dossier restait plat, et se serait tue
        // le jour ou quelqu'un aurait range les ViewModels en sous-dossiers.
        foreach (var f in Directory.EnumerateFiles(racine, "*.cs", SearchOption.AllDirectories))
        {
            lus++;
            var lignes = File.ReadAllLines(f);
            for (var i = 0; i < lignes.Length; i++)
            {
                if (lignes[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match lit in Regex.Matches(lignes[i], "\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var t = lit.Groups[1].Value;
                    if (t == "—") continue; // cellule vide, meme exception que les ressources
                    if (t.Contains((char)0x26A0) || t.Contains('—')) fautes.Add($"{Path.GetFileName(f)}:{i + 1}");
                    foreach (Match m in Regex.Matches(t, @"\b[A-ZÉÈÀ]{4,}\b"))
                        if (!Sigles.Contains(m.Value)) fautes.Add($"{Path.GetFileName(f)}:{i + 1} {m.Value}");
                    foreach (var mot in MotsSansAccent)
                        if (Regex.IsMatch(t, $@"(?<![\p{{L}}']){Regex.Escape(mot)}(?!\p{{L}})"))
                            fautes.Add($"{Path.GetFileName(f)}:{i + 1} « {mot} » sans accent");
                }
            }
        }

        // Le temoin manquait : ce test concluait « aucune faute » sans jamais
        // affirmer avoir lu un fichier. Un chemin qui change, et il restait vert sur zero.
        lus.Should().BeGreaterThan(5, "le crible doit avoir lu de vrais ViewModels");
        fautes.Should().BeEmpty();
    }
}
