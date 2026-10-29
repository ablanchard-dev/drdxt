namespace DrDxt.Core;

/// <summary>Ce qu'il faut faire du service de mesure avant de mesurer.</summary>
public enum PresentMonServiceAction
{
    /// <summary>Il n'existe pas : le creer.</summary>
    Install,

    /// <summary>Il existe et pointe deja sur notre binaire : rien a ecrire, juste le demarrer.</summary>
    StartOnly,

    /// <summary>Il pointe sur un ancien emplacement de DrDXT : reecrire sa ligne de commande avant de demarrer, pour ne
    /// pas mesurer a travers un binaire inconnu.</summary>
    Repair,

    /// <summary>Il appartient a un autre produit (le nom est aussi celui du PresentMon d'Intel), ou sa ligne
    /// ne se lit pas : on n'y touche pas, la mesure passe par le repli DXGI.</summary>
    PasANous,
}

/// <summary>
/// Le cycle de vie du service, en quatre cas : creer, demarrer, reparer, ou laisser a son proprietaire. Un service de
/// ce nom peut pointer ailleurs (ancienne installation, app deplacee, autre PresentMon) : l'API vendorée est sensible a
/// la version (07/09/2026 : client 2.4.1 → service 2.5.1 = `PM_STATUS_PIPE_ERROR`, → service 2.4.1 reussit). Une
/// configuration deja bonne n'est jamais reecrite. Pur : l'appelant lit l'existence et le `binPath`, ceci decide.
/// </summary>
public static class PresentMonServicePlan
{
    /// <param name="currentBinPath">La ligne de commande enregistree dans le SCM, telle quelle.
    /// <c>null</c> = pas pu la lire, ce qui n'est ni « elle est correcte » ni « elle est a nous » : on n'y touche pas.</param>
    public static PresentMonServiceAction Decide(bool exists, string? currentBinPath, string expectedBinary)
    {
        if (!exists)
            return PresentMonServiceAction.Install;

        if (string.IsNullOrWhiteSpace(currentBinPath))
            return PresentMonServiceAction.PasANous;

        // Exactement notre ligne (la casse ne compte pas : c'est Windows). Une enveloppe qui la contient n'est pas elle.
        if (string.Equals(currentBinPath.Trim(), BinPath(expectedBinary), StringComparison.OrdinalIgnoreCase))
            return PresentMonServiceAction.StartOnly;

        // Un ancien emplacement de DrDXT (reinstallation ailleurs, dossier supprime) se repare ; tout autre chemin est
        // celui d'un autre produit, jamais repointe ni supprime.
        return AncienEmplacementDeDrDxt(currentBinPath) ? PresentMonServiceAction.Repair : PresentMonServiceAction.PasANous;
    }

    /// <summary>La ligne commence par un PresentMonService.exe entre guillemets sous un dossier DrDXT, arguments
    /// eventuels ensuite ; une enveloppe qui le cite plus loin n'est pas lui.</summary>
    internal static bool AncienEmplacementDeDrDxt(string binPath)
    {
        var t = binPath.Trim();
        var fin = t.Length > 1 && t[0] == '"' ? t.IndexOf('"', 1) : -1;
        if (fin < 0) return false;
        var chemin = t[1..fin];
        return string.Equals(Path.GetFileName(chemin), "PresentMonService.exe", StringComparison.OrdinalIgnoreCase)
            && chemin.Split('\\').SkipLast(1).Any(d => string.Equals(d, "DrDXT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Le cas <see cref="PresentMonServiceAction.Repair"/> : arreter, puis reconfigurer. Un arret rate est rendu
    /// tel quel, sans reconfigurer : sinon `Start` rendrait Ok sur le service deja lance, avec l'ancien binaire.</summary>
    public static Result Reparer(Func<Result> arreter, Func<Result> reconfigurer)
    {
        var arret = arreter();
        return arret.IsOk ? reconfigurer() : arret;
    }

    /// <summary>La valeur de <c>binPath=</c> telle que le SCM la conserve et que <see cref="Decide"/> la relit dans
    /// <c>ImagePath</c> ; partagee par l'installeur et le helper elevé.</summary>
    public static string BinPath(string binary) =>
        PresentMonNaming.ServiceArguments.Length == 0
            ? $"\"{binary}\""
            : $"\"{binary}\" {PresentMonNaming.ServiceArguments}";

    /// <summary>L'argument <c>binPath= ...</c> de la ligne de commande de <c>sc.exe</c>. Les antislash devant les
    /// guillemets internes comptent des que le binPath porte des arguments (mesuré le 07/09/2026 sur deux services jetables : sans eux,
    /// <c>sc.exe</c> rend 1639 et ne cree rien) : `CommandLineToArgvW` doit rendre un seul argument, et `""` en tete
    /// disloque la ligne ; le chemin par defaut contient une espace. Correct aussi quand
    /// <see cref="PresentMonNaming.ServiceArguments"/> est vide.</summary>
    public static string BinPathArgument(string binary)
        => $"binPath= \"\\\"{binary}\\\"{Suffixe()}\"";

    private static string Suffixe() =>
        PresentMonNaming.ServiceArguments.Length == 0 ? string.Empty : " " + PresentMonNaming.ServiceArguments;
}
