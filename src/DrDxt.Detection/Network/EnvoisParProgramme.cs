using DrDxt.Core;

namespace DrDxt.Detection.Network;

/// <summary>
/// Qui occupe la montée : les programmes qui envoient le plus pendant une courte fenêtre du scan, en TCP et en UDP, IPv4 et
/// IPv6, en kbit/s. Une synchronisation de fichiers, un appel vocal ou un lanceur qui télécharge prennent la montée que le
/// jeu attend. Lecture seule, session ETW en temps réel dans le processus (aucun fichier, aucun outil externe). Dit, sans
/// verdict : la capacité de montée de la connexion n'est pas mesurée, donc aucune part n'est chiffrée.
/// </summary>
public static class EnvoisParProgramme
{
    public static readonly TimeSpan Fenetre = TimeSpan.FromSeconds(3);
    public const int Montres = 3;

    public static RepairFinding Juger(IReadOnlyDictionary<int, long>? octets, TimeSpan fenetre, Func<int, string> nom)
    {
        const string titre = "programmes qui envoient";
        if (octets is null)
            return new RepairFinding("RESEAU", titre, null, null, RepairStatus.Unknown, Docteur.T("ENV.NonLu"));
        var gros = octets.Where(kv => kv.Key > 0)
            .Select(kv => (Nom: nom(kv.Key), Kbps: kv.Value * 8 / 1000.0 / fenetre.TotalSeconds))
            .GroupBy(x => x.Nom, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Nom: g.Key, Kbps: g.Sum(x => x.Kbps)))
            .Where(x => x.Kbps > 0)
            .OrderByDescending(x => x.Kbps).Take(Montres).ToList();
        if (gros.Count == 0)
            return new RepairFinding("RESEAU", titre, Docteur.T("ENV.ValeurRien"), null, RepairStatus.ExpectedDefault,
                Docteur.T("ENV.Rien", fenetre.TotalSeconds));
        var liste = string.Join(", ", gros.Select(g => $"{g.Nom} {g.Kbps.ToString("0.#", Docteur.Langue)} kbit/s"));
        return new RepairFinding("RESEAU", titre, liste, null, RepairStatus.Observed, Docteur.T("ENV.Liste", fenetre.TotalSeconds, liste));
    }

    /// <summary>Le nom d'un processus encore vivant ; sinon « pid N ».</summary>
    public static string NomDuProcessus(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return p.ProcessName; }
        catch (Exception) { return $"pid {pid}"; }
    }
}
