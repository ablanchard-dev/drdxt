namespace DrDxt.Core;

/// <summary>Ce qu'il faut faire du service de mesure avant de mesurer.</summary>
public enum PresentMonServiceAction
{
    /// <summary>Il n'existe pas : le creer.</summary>
    Install,

    /// <summary>Il existe et pointe deja sur notre binaire : rien a ecrire, juste le demarrer.</summary>
    StartOnly,

    /// <summary>Il existe mais ne pointe pas sur notre binaire : reecrire sa ligne de commande
    /// avant de demarrer. Sinon DrDXT mesure a travers un binaire qu'il ne connait pas.</summary>
    Repair,
}

/// <summary>
/// Le cycle de vie du service, en trois cas — le code precedent n'en connaissait que deux.
///
/// Il faisait : « le service existe ⇒ demarre-le ». Ca laisse passer le cas qui compte : un
/// service enregistre sous ce nom qui pointe **ailleurs** — une ancienne installation de DrDXT,
/// une app deplacee, ou un autre PresentMon ayant pris le nom. Le demarrer revient alors a mesurer
/// a travers un binaire dont on ne connait ni la version ni l'ABI, et l'API vendorée est
/// justement sensible a la version (mesure du 07/09/2026 : client 2.4.1 → service 2.5.1 =
/// `PM_STATUS_PIPE_ERROR`, alors que le meme client → service 2.4.1 reussit).
///
/// Et l'inverse compte autant : quand la configuration est deja la bonne, **on n'ecrit rien**.
/// Un produit qui recree son service a chaque mesure se bat contre le systeme au lieu de s'en
/// servir.
///
/// Pur : aucune I/O, aucun appel a `sc.exe`. L'appelant lit l'existence et le `binPath`, cette
/// fonction decide, et c'est elle qui est testable sans service.
/// </summary>
public static class PresentMonServicePlan
{
    /// <param name="currentBinPath">La ligne de commande enregistree dans le SCM, telle quelle.
    /// <c>null</c> = pas pu la lire — ce qui n'est pas « elle est correcte » : on repare.</param>
    public static PresentMonServiceAction Decide(bool exists, string? currentBinPath, string expectedBinary)
    {
        if (!exists)
            return PresentMonServiceAction.Install;

        if (string.IsNullOrWhiteSpace(currentBinPath))
            return PresentMonServiceAction.Repair;

        // Le chemin ne se compare pas a la casse : c'est Windows.
        return currentBinPath.Contains(expectedBinary, StringComparison.OrdinalIgnoreCase)
            ? PresentMonServiceAction.StartOnly
            : PresentMonServiceAction.Repair;
    }

    /// <summary>Le cas <see cref="PresentMonServiceAction.Repair"/> : arreter, puis reconfigurer.
    ///
    /// L'arret n'etait pas lu. S'il echouait, `sc config` passait quand meme, puis `Start`
    /// rendait Ok parce que le service tournait deja : la reparation annoncait un succes avec
    /// l'ANCIEN binaire en place, et la mesure suivante passait a travers lui — exactement ce que
    /// ce cas existe pour empecher. Un arret rate est rendu tel quel ; on ne reconfigure pas.</summary>
    public static Result Reparer(Func<Result> arreter, Func<Result> reconfigurer)
    {
        var arret = arreter();
        return arret.IsOk ? reconfigurer() : arret;
    }

    /// <summary>La valeur de <c>binPath=</c> telle que le SCM la conserve, et telle que
    /// <see cref="Decide"/> la relit dans <c>ImagePath</c>. Une seule construction, partagee par
    /// l'installeur et par le helper elevé.</summary>
    public static string BinPath(string binary) =>
        PresentMonNaming.ServiceArguments.Length == 0
            ? $"\"{binary}\""
            : $"\"{binary}\" {PresentMonNaming.ServiceArguments}";

    /// <summary>L'argument <c>binPath= ...</c> a poser sur la ligne de commande de <c>sc.exe</c>.
    ///
    /// Les antislash devant les guillemets internes ne sont pas decoratifs des que le binPath
    /// porte des arguments. Mesure du 07/09/2026 sur deux services jetables : sans eux,
    /// <c>sc.exe</c> rend **1639** (ligne de commande invalide) et **ne cree rien**.
    /// `CommandLineToArgvW` doit rendre a sc.exe un seul argument qui contient des guillemets ;
    /// `""` en tete est lu comme une chaine vide et disloque la ligne. Le chemin d'installation
    /// par defaut contient une espace, donc c'est le cas nominal qui casse.
    ///
    /// L'echappement est conserve alors que <see cref="PresentMonNaming.ServiceArguments"/> est
    /// vide aujourd'hui : il est correct dans les deux cas, et c'est la forme qui survit au jour
    /// ou un argument revient.</summary>
    public static string BinPathArgument(string binary)
        => $"binPath= \"\\\"{binary}\\\"{Suffixe()}\"";

    private static string Suffixe() =>
        PresentMonNaming.ServiceArguments.Length == 0 ? string.Empty : " " + PresentMonNaming.ServiceArguments;
}
