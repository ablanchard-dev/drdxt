using DrDxt.Core;
using DrDxt.Detection.Wmi;

namespace DrDxt.Detection.Repair;

/// <summary>Sécurité : les tâches planifiées dont le programme pourrait être remplacé par un autre.</summary>
public sealed partial class RepairAuditService
{
    // Même élévation que les services, par les tâches planifiées. Une tâche lancée en SYSTEM,
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
                Docteur.T("TA.NonLu", taches.Error?.Message));
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
                if (raison is null || CheminProtege.EstIllisible(raison)) continue;

                var nom = t.GetString("TaskName");
                var chemin = (t.GetString("TaskPath") ?? "") + nom;
                var droits = systeme
                    ? Docteur.T("TA.DroitsSysteme")
                    : Docteur.T("TA.DroitsAdmin");
                // Relu sur la machine de reference : la tâche signalée était désactivée (État MSFT_ScheduledTask 1).
                var debut = t.GetInt("State") == 1
                    ? Docteur.T("TA.Desactivee", droits)
                    : Docteur.T("TA.SeLance", droits);
                // « remplaçable » faisait peur sans rien dire ; le titre dit ce qui peut arriver.
                yield return new RepairFinding("TACHES", Docteur.Nom("TA.Nom", nom), exe, null,
                    RepairStatus.Suspicious,
                    Docteur.T("TA.Explication", debut, SansLeChemin(raison, exe)) + Docteur.T("TA.Planificateur", chemin),
                    Remede: Remede(exe, Docteur.T("TA.Deplacer")));
                break;
            }
        }
    }
}
