using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Repair;

public sealed partial class RepairAuditService
{
    // 25/09/2026 : même élévation que les services, par les tâches planifiées. Une tâche lancée en SYSTEM,
    // ou avec les droits les plus élevés (elle passe alors l'UAC sans rien demander), depuis un fichier que
    // le compte peut modifier : un programme sans droits remplace le fichier, et il est élevé au prochain
    // déclenchement. Vue sur la machine de reference (une tâche « plus haut niveau » lançant un pythonw.exe de ses
    // Téléchargements). Seul l'exécutable est vérifié, pas un script passé en argument.
    private Result<IReadOnlyList<WmiRow>> TachesLues() => _wmi.QueryInNamespace(
        @"\\.\root\Microsoft\Windows\TaskScheduler", "SELECT TaskName, TaskPath, State, Principal, Actions FROM MSFT_ScheduledTask");

    internal static IEnumerable<RepairFinding> TachesRemplacables(Result<IReadOnlyList<WmiRow>> taches, Func<string, string?> verifier)
    {
        if (!taches.IsOk)
        {
            yield return new RepairFinding("TACHES", "tâches planifiées remplaçables", null, null, RepairStatus.Unknown,
                $"la liste des tâches planifiées n'a pas pu être lue ({taches.Error?.Message}).");
            yield break;
        }

        foreach (var t in taches.Value!)
        {
            var principal = t.Properties.GetValueOrDefault("Principal") as WmiRow;
            var systeme = principal?.GetString("UserId") is "SYSTEM" or "S-1-5-18" or @"NT AUTHORITY\SYSTEM";
            var plusHautNiveau = principal?.GetInt("RunLevel") == 1;
            if (!systeme && !plusHautNiveau) continue;

            var actions = t.Properties.GetValueOrDefault("Actions") as WmiRow[] ?? [];
            foreach (var a in actions)
            {
                var brut = a.GetString("Execute");
                if (string.IsNullOrWhiteSpace(brut)) continue;
                var exe = Environment.ExpandEnvironmentVariables(brut.Trim('"'));
                // Un nom nu (« cmd.exe ») se résout par le PATH : pas de fichier précis à juger.
                if (!Path.IsPathFullyQualified(exe) || !File.Exists(exe)) continue;
                var raison = verifier(exe);
                if (raison is null || raison.StartsWith("ACL illisible", StringComparison.Ordinal)) continue;

                var nom = t.GetString("TaskName");
                var chemin = (t.GetString("TaskPath") ?? "") + nom;
                var droits = systeme
                    ? "avec les droits du système (SYSTEM)"
                    : "avec les droits administrateur, sans passer par la fenêtre de confirmation";
                // 25/09/2026, relu sur la machine de reference : la tâche signalée était désactivée (État MSFT_ScheduledTask 1).
                var debut = t.GetInt("State") == 1
                    ? $"cette tâche est désactivée, elle ne se lance pas aujourd'hui ; réactivée, elle se lancerait {droits}"
                    : $"cette tâche se lance {droits}";
                // 29/09/2026 : « remplaçable » faisait peur sans rien dire ; le titre dit ce qui peut arriver.
                yield return new RepairFinding("TACHES", $"tâche planifiée {nom} : son fichier peut être remplacé par un autre programme", exe, null,
                    RepairStatus.Suspicious,
                    $"{debut}, mais {SansLeChemin(raison, exe)}. Un programme lancé sans droits administrateur "
                    + "pourrait remplacer ce fichier, et il serait élevé au prochain déclenchement de la tâche. "
                    + Remede(exe, "Déplacer le programme dans Program Files, ou supprimer la tâche si tu ne t'en sers plus, "
                        + "ferme ce chemin.")
                    + $" Dans le Planificateur de tâches : {chemin}. DrDXT ne touche pas à cette tâche.");
                break;
            }
        }
    }
}
