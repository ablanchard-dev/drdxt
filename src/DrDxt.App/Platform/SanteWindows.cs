using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;

namespace DrDxt.App.Platform;

/// <summary>
/// 25/09/2026 — le constat « magasin de composants » du docteur (DISM /CheckHealth, lecture). Meme
/// chemin que <see cref="VariablesFirmware"/> : jamais d'UAC pour un scan. Le helper deja lance, sinon
/// lecture directe si l'appli tourne en administrateur, sinon « non lu ».
/// </summary>
public static class SanteWindows
{
    /// <param name="installation">07/10/2026 (D8) : la dernière installation de Windows ; après « Réinstaller
    /// maintenant », c'est elle qui date la preuve (avant : abîmé ; depuis la réinstallation du X : sain).</param>
    public static RepairFinding Constat(Func<Result<byte[]>>? lire = null, Func<DateTimeOffset?>? installation = null)
    {
        var constat = Lu(lire);
        DateTimeOffset? quand = null;
        try { quand = (installation ?? DrDxt.Detection.Repair.InstallationDeWindows.Lire)(); }
        catch (Exception) { }
        return quand is { } q && constat.ValeurCourante is not null
            ? constat with { Explication = constat.Explication + " " + string.Format(System.Globalization.CultureInfo.GetCultureInfo("fr-FR"),
                "Dernière installation ou mise à niveau de Windows (une réinstallation en place compte) : le {0:dd/MM/yyyy}.", q.LocalDateTime) }
            : constat;
    }

    private static RepairFinding Lu(Func<Result<byte[]>>? lire)
    {
        var lu = (lire ?? LireSansUac)();
        if (!lu.IsOk)
            return new RepairFinding("STABILITE", "magasin de composants", null, null, RepairStatus.Unknown,
                "non lu : la vérification de Windows (DISM) demande les droits administrateur. « Non lu » n'est pas « sain ».");

        return System.Text.Encoding.UTF8.GetString(lu.Value!) switch
        {
            SanteDesComposants.Sain => new RepairFinding("STABILITE", "magasin de composants", "sain", "sain",
                RepairStatus.ExpectedDefault, "DISM /CheckHealth ne signale aucune corruption des composants de Windows."),
            SanteDesComposants.Reparable => new RepairFinding("STABILITE", "magasin de composants", "abîmé, réparable", "sain",
                RepairStatus.Suspicious,
                "des fichiers qui servent à mettre à jour et à réparer Windows sont abîmés, mais réparables. "
                + "Vérifié par DISM /CheckHealth, l'outil de Microsoft. Réparation Microsoft, dans une console administrateur : "
                + "« DISM /Online /Cleanup-Image /RestoreHealth » puis « sfc /scannow ». Si elle échoue faute de source "
                // DOC-WIN (09/10/2026) : la réparation que Microsoft documente pour 0x800f081f, sans nettoyage irréversible.
                + "(0x800f081f), Microsoft propose : Paramètres > Système > Récupération > « Réinstaller maintenant », qui "
                + "réinstalle la même version de Windows en gardant fichiers, applications et réglages.",
                RepairAction.Recommandation),
            SanteDesComposants.NonReparable => new RepairFinding("STABILITE", "magasin de composants", "abîmé, non réparable", "sain",
                RepairStatus.Suspicious,
                "des fichiers qui servent à mettre à jour et à réparer Windows sont abîmés, et l'outil de Microsoft ne peut "
                + "pas les réparer (vérifié par DISM /CheckHealth). Microsoft conseille "
                + "alors de repartir d'une image saine : une réinstallation de Windows en conservant ses fichiers.",
                RepairAction.Recommandation),
            var autre => new RepairFinding("STABILITE", "magasin de composants", autre, null, RepairStatus.Unknown,
                "réponse de DISM non reconnue : DrDXT ne l'interprète pas."),
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
