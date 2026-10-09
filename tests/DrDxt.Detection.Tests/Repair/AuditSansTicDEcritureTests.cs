using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Les explications de l'audit sont affichees telles quelles au joueur. Retour du 16/09/2026 : les
/// pictogrammes d'alerte et les mots en capitales pour insister font lire le texte comme genere.
/// Ce gate lit les litteraux de chaine des fichiers qui ecrivent ces explications ; les sigles et
/// les cles de domaine du journal sont declares.
/// </summary>
public sealed class AuditSansTicDEcritureTests
{
    private static readonly string[] Fichiers =
    [
        "src/DrDxt.Detection/Repair/RepairAuditService.cs",
        "src/DrDxt.Detection/Settings/CrashDumpPolicy.cs",
        "src/DrDxt.Detection/Settings/BcdInterpretation.cs",
        "src/DrDxt.Core/Repair/RepairHistory.cs",
        "src/DrDxt.Core/Repair/RepairTriage.cs",
        "src/DrDxt.Detection/Latency/LatenceDpc.cs",
        "src/DrDxt.Measurement/Network/PertesInterface.cs",
        // motifs d'une session d'optimisation, affiches dans son compte rendu
        "src/DrDxt.Core/Autopilot/KeepPolicy.cs",
        "src/DrDxt.Core/Autopilot/GameMetricSet.cs",
        "src/DrDxt.Core/Autopilot/AutopilotEngine.cs",
        "src/DrDxt.Core/Autopilot/CandidateSelector.cs",
        "src/DrDxt.Application/Autopilot/GameOptimizationSession.cs",
        "src/DrDxt.Application/Autopilot/RevertEnAttente.cs",
        "src/DrDxt.Core/Autopilot/SceneContract.cs",
        "src/DrDxt.Application/Support/DiagnosticReport.cs",
        // 24/09/2026 : la nature des pannes de service s'affiche dans la carte « windows » (vue sans accents).
        "src/DrDxt.Detection/Hardware/ServiceFailureLog.cs",
    ];

    // 17/09/2026 : trois tours de suite ont trouve des phrases affichees sans accents, une par une.
    // Les cles du journal (« test memoire Windows », « frequence effective ») restent sans accents
    // exprès : aucun des mots ci-dessous n'en fait partie.
    private static readonly string[] MotsSansAccent =
    [
        "etait", "etaient", "ete", "deja", "meme", "precedente", "examinee", "verifies", "etats",
        "depend", "reparer", "repondre", "cesse", "entree", "mesuree", "systeme", "probleme", "tres",
        "a change", "l'a change", "etre", "defaut", "memoire vive",
        "demarrer", "demarrage", "facon", "recuperation",
    ];

    private static readonly HashSet<string> Sigles = new(StringComparer.Ordinal)
    {
        "ACPI", "ADLX", "IGCL", "BIOS", "NVIDIA", "NVML", "RAPL", "MMCSS", "SELECT", "FROM", "WHERE","SOFTWARE", "SYSTEM",
        "STABILITE", "POWER", "SERVICES", "STOCKAGE", "DISPLAY", "DRIVER", "PAGEFILE", "THERMIQUE",
        "LATENCE", "RESEAU", "KMDF", "PERIPHERIQUES", "TACHES", "WINDOWS", "UPDATE", "DEMARRAGE",
        // 04/10/2026 (plan 2.5) : la clé de domaine de l'intégrité de la mémoire, comme STABILITE.
        "SECURITE",
        // 24/09/2026 : l'API Microsoft par laquelle le compteur de repli lit les images. Un nom propre
        // technique, comme NVIDIA ou MMCSS, pas un mot crie.
        "DXGI",
    };

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucune_explication_ne_porte_de_pictogramme_ni_de_mot_en_capitales()
    {
        var fautes = new List<string>();
        var lignesLues = 0;
        foreach (var rel in Fichiers)
        {
            var lignes = File.ReadAllLines(Path.Combine(Racine(), rel));
            lignesLues += lignes.Length;
            for (var i = 0; i < lignes.Length; i++)
            {
                if (lignes[i].TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match lit in Regex.Matches(lignes[i], "\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var texte = lit.Groups[1].Value;
                    if (texte.Contains((char)0x26A0)) fautes.Add($"{rel}:{i + 1} pictogramme");
                    if (texte.Contains('\u2014')) fautes.Add($"{rel}:{i + 1} tiret cadratin");
                    foreach (Match mot in Regex.Matches(texte, @"\b[A-ZÉÈÀÊ][A-ZÉÈÀÊ]{3,}\b"))
                        if (!Sigles.Contains(mot.Value)) fautes.Add($"{rel}:{i + 1} {mot.Value}");
                    if (Regex.IsMatch(texte, @"\bPAS\b")) fautes.Add($"{rel}:{i + 1} PAS");
                    var prose = Regex.Replace(texte, @"\{[^}]*\}", " "); // trous d'interpolation : du code, pas du texte
                    foreach (var m in MotsSansAccent)
                        if (Regex.IsMatch(prose,$@"(?<![\p{{L}}']){Regex.Escape(m)}(?!\p{{L}})"))
                            fautes.Add($"{rel}:{i + 1} « {m} » sans accent");
                }
            }
        }

        // Temoin de lecture : un fichier deplace rendrait ce crible muet et vert.
        lignesLues.Should().BeGreaterThan(100, "le crible doit avoir lu de vrais fichiers du produit");
        fautes.Should().BeEmpty();
    }
}
