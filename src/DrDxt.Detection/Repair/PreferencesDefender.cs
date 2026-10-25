using DrDxt.Core;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Deux réglages de Microsoft Defender qui touchent le jeu, lus dans MSFT_MpPreference et jamais écrits : l'accès contrôlé
/// aux dossiers (un jeu qui n'y est pas autorisé ne peut plus écrire ses réglages ni ses sauvegardes sous Documents), et les
/// exclusions d'analyse comparées aux dossiers des jeux. DrDXT n'ajoute jamais d'exclusion : il dit si le dossier est exclu,
/// couvert par un dossier parent ou non exclu, et signale une exclusion de processus par simple nom, qu'un autre programme
/// portant ce nom traverserait sans analyse.
/// Sources : Microsoft Learn, « Controlled folder access » (EnableControlledFolderAccess : 0 désactivé, 1 activé,
/// 2 audit, 3 et 4 secteurs du disque seulement, selon l'énumération ControlledFolderAccessType du module Defender ;
/// ControlledFolderAccessAllowedApplications) et « Configure custom exclusions » (ExclusionPath, ExclusionProcess ;
/// un compte non administrateur lit « N/A: Must be an administrator to view exclusions » à la place de la liste).
/// </summary>
public static class PreferencesDefender
{
    public sealed record Lues(int? AccesControle, IReadOnlyList<string> Autorisees, IReadOnlyList<string> ExclusionsChemin,
        IReadOnlyList<string> ExclusionsProcessus);

    /// <summary>Le vrai, en lecture seule ; <c>null</c> = Defender n'a pas répondu (désactivé, remplacé, ou WMI refusé).</summary>
    public static Lues? Lire() => LectureWmiBornee.Lire<Lues?>(() =>
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher(@"root\Microsoft\Windows\Defender",
                "SELECT EnableControlledFolderAccess, ControlledFolderAccessAllowedApplications, ExclusionPath, ExclusionProcess FROM MSFT_MpPreference");
            using var rangees = s.Get();
            foreach (var o in rangees.Cast<System.Management.ManagementObject>())
                using (o)
                {
                    static IReadOnlyList<string> Liste(object? v) => v is string[] a ? a : [];
                    return new Lues(o["EnableControlledFolderAccess"] is { } c ? Convert.ToInt32(c, System.Globalization.CultureInfo.InvariantCulture) : null,
                        Liste(o["ControlledFolderAccessAllowedApplications"]), Liste(o["ExclusionPath"]), Liste(o["ExclusionProcess"]));
                }
            return null;
        }
        catch (Exception) { return null; }
    }, () => null);

    // Le texte que Defender rend à la place d'une liste qu'il cache.
    private static bool Cachee(IReadOnlyList<string> l) => l.Any(e => e.StartsWith("N/A", StringComparison.OrdinalIgnoreCase));

    private static bool Sous(string chemin, string dossier)
    {
        // TrimEnd plutôt que TrimEndingDirectorySeparator : ce dernier garde « D:\ », et une exclusion du disque entier
        // ne couvrirait plus rien.
        var d = dossier.TrimEnd('\\', '/');
        return chemin.StartsWith(d + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
               || string.Equals(Path.TrimEndingDirectorySeparator(chemin), d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Pur. L'accès contrôlé aux dossiers, et si un jeu (programme sous un dossier de jeux) y est autorisé.</summary>
    public static RepairFinding JugerAccesControle(Lues? lu, IReadOnlyList<string>? dossiersDeJeux)
    {
        const string Nom = "acces controle aux dossiers";
        if (lu?.AccesControle is not { } etat)
            return new RepairFinding("SECURITE", Nom, null, null, RepairStatus.Unknown, Docteur.T("DF.NonLu"));
        if (etat == 0)
            return new RepairFinding("SECURITE", Nom, Docteur.T("DF.Desactive"), null, RepairStatus.ExpectedDefault, Docteur.T("DF.AccesDesactive"));
        // 3 BlockDiskModificationOnly, 4 AuditDiskModificationOnly : les secteurs du disque, jamais les dossiers.
        if (etat is 3 or 4)
            return new RepairFinding("SECURITE", Nom, Docteur.T("DF.DisqueSeul"), null, RepairStatus.Observed, Docteur.T("DF.AccesDisqueSeul"));
        if (etat != 1)
            return new RepairFinding("SECURITE", Nom, Docteur.T("DF.Audit"), null, RepairStatus.Observed, Docteur.T("DF.AccesAudit"));
        if (Cachee(lu.Autorisees) || dossiersDeJeux is null)
            return new RepairFinding("SECURITE", Nom, Docteur.T("DF.Active"), null, RepairStatus.Unknown, Docteur.T("DF.AccesAutorisesNonLus"));
        var jeux = lu.Autorisees.Count(a => dossiersDeJeux.Any(d => Sous(a, d)));
        return jeux > 0
            ? new RepairFinding("SECURITE", Nom, Docteur.T("DF.Active"), null, RepairStatus.Observed, Docteur.T("DF.AccesJeuxAutorises", jeux))
            // Un choix de sécurité du joueur ; aucun blocage d'un jeu n'a été lu : à regarder.
            : new RepairFinding("SECURITE", Nom, Docteur.T("DF.Active"), null, RepairStatus.NonDefault, Docteur.T("DF.AccesAucunJeu"),
                RepairAction.Recommandation, Remede: Docteur.T("DF.AccesRemede"));
    }

    /// <summary>Pur. Chaque dossier de jeux : exclu, couvert par un parent ou non exclu ; et les exclusions de processus par nom nu.
    /// Jamais de conseil d'exclure : une exclusion est un trou dans la protection, et la mesure ne l'a jamais justifiée.</summary>
    public static IEnumerable<RepairFinding> JugerExclusions(Lues? lu, IReadOnlyList<string>? dossiersDeJeux)
    {
        const string Nom = "exclusions Defender";
        if (lu is null)
        {
            yield return new RepairFinding("SECURITE", Nom, null, null, RepairStatus.Unknown, Docteur.T("DF.NonLu"));
            yield break;
        }
        if (Cachee(lu.ExclusionsChemin) || Cachee(lu.ExclusionsProcessus))
        {
            yield return new RepairFinding("SECURITE", Nom, Docteur.T("DF.Invisible"), null, RepairStatus.Unknown, Docteur.T("DF.ExclusionsCachees"));
            yield break;
        }
        var nus = lu.ExclusionsProcessus.Where(p => p.IndexOfAny(['\\', '/']) < 0 && !p.Contains('*')).ToList();
        if (nus.Count > 0)
            yield return new RepairFinding("SECURITE", "exclusion de processus par nom", string.Join(", ", nus), null, RepairStatus.Suspicious,
                Docteur.T("DF.ProcessusNu", string.Join(", ", nus)), RepairAction.Recommandation, Remede: Docteur.T("DF.ProcessusNuRemede"));
        if (dossiersDeJeux is null || dossiersDeJeux.Count == 0) yield break;
        var exclus = dossiersDeJeux.Count(d => lu.ExclusionsChemin.Any(e => string.Equals(Path.TrimEndingDirectorySeparator(e),
            Path.TrimEndingDirectorySeparator(d), StringComparison.OrdinalIgnoreCase)));
        var couverts = dossiersDeJeux.Count(d => lu.ExclusionsChemin.Any(e => Sous(d, e))) - exclus;
        yield return new RepairFinding("SECURITE", Nom, Docteur.T("DF.Compte", exclus, couverts, dossiersDeJeux.Count - exclus - couverts),
            null, RepairStatus.Observed, Docteur.T("DF.ExclusionsExplication"));
    }
}
