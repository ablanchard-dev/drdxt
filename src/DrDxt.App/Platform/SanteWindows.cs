using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;

namespace DrDxt.App.Platform;

/// <summary>
/// Le constat « magasin de composants » du docteur (DISM /CheckHealth, lecture). Meme
/// chemin que <see cref="VariablesFirmware"/> : jamais d'UAC pour un scan. Le helper deja lance, sinon
/// lecture directe si l'appli tourne en administrateur, sinon « non lu ».
/// </summary>
public static class SanteWindows
{
    /// <param name="installation">La dernière installation de Windows ; après « Réinstaller
    /// maintenant », c'est elle qui date la preuve (avant : abîmé ; depuis la réinstallation du X : sain).</param>
    public static RepairFinding Constat(Func<Result<byte[]>>? lire = null, Func<DateTimeOffset?>? installation = null)
    {
        var constat = Lu(lire);
        DateTimeOffset? quand = null;
        try { quand = (installation ?? DrDxt.Detection.Repair.InstallationDeWindows.Lire)(); }
        catch (Exception) { /* sans date d'installation, le constat reste, sans « depuis la réinstallation » */ }
        return quand is { } q && constat.ValeurCourante is not null
            ? constat with { Explication = constat.Explication + " " + Docteur.T("WinSante.DerniereInstallation", q.LocalDateTime) }
            : constat;
    }

    private static RepairFinding Lu(Func<Result<byte[]>>? lire)
    {
        var lu = (lire ?? LireSansUac)();
        if (!lu.IsOk)
            return new RepairFinding("STABILITE", "magasin de composants", null, null, RepairStatus.Unknown,
                Docteur.T("WinSante.NonLu"));

        return System.Text.Encoding.UTF8.GetString(lu.Value!) switch
        {
            SanteDesComposants.Sain => new RepairFinding("STABILITE", "magasin de composants", Docteur.T("WinSante.Sain"), Docteur.T("WinSante.Sain"),
                RepairStatus.ExpectedDefault, Docteur.T("WinSante.Sain.Phrase")),
            // La réparation que Microsoft documente pour 0x800f081f, sans nettoyage irréversible.
            SanteDesComposants.Reparable => new RepairFinding("STABILITE", "magasin de composants", Docteur.T("WinSante.Reparable"), Docteur.T("WinSante.Sain"),
                RepairStatus.Suspicious, Docteur.T("WinSante.Reparable.Phrase"), RepairAction.Recommandation,
                Remede: Docteur.T("WinSante.Reparable.Remede")),
            SanteDesComposants.NonReparable => new RepairFinding("STABILITE", "magasin de composants", Docteur.T("WinSante.NonReparable"), Docteur.T("WinSante.Sain"),
                RepairStatus.Suspicious, Docteur.T("WinSante.NonReparable.Phrase"), RepairAction.Recommandation,
                Remede: Docteur.T("WinSante.NonReparable.Remede")),
            var autre => new RepairFinding("STABILITE", "magasin de composants", autre, null, RepairStatus.Unknown,
                Docteur.T("WinSante.Inconnu")),
        };
    }

    private static Result<byte[]> LireSansUac()
    {
        var parHelper = ElevatedClientFactory.CreateSansLancer()
            .ReadAsync(new ReadComponentStoreHealthOp(), CancellationToken.None).GetAwaiter().GetResult();
        if (parHelper.IsOk) return parHelper;

        var eleve = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        return eleve ? SanteDesComposants.Lire() : parHelper;
    }
}
