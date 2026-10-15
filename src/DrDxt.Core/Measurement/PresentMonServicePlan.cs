namespace DrDxt.Core;

/// <summary>Ce qu'il faut faire du service de mesure avant de mesurer.</summary>
public enum PresentMonServiceAction
{
    /// <summary>Il n'existe pas : le creer.</summary>
    Install,

    /// <summary>Il existe et pointe deja sur notre binaire : rien a ecrire, juste le demarrer.</summary>
    StartOnly,

    /// <summary>Il pointe sur un ancien emplacement de DrDXT : reecrire sa ligne de commande
    /// avant de demarrer. Sinon DrDXT mesure a travers un binaire qu'il ne connait pas.</summary>
    Repair,

    /// <summary>Il appartient a un autre produit (le nom est aussi celui du PresentMon d'Intel), ou sa ligne
    /// ne se lit pas : on n'y touche pas, la mesure passe par le repli DXGI.</summary>
    PasANous,
}

/// <summary>
/// Le cycle de vie du service, en quatre cas : creer, demarrer, reparer, ou laisser a son proprietaire.
///
/// « Le service existe ⇒ demarre-le » laisserait passer le cas qui compte : un
/// service enregistre sous ce nom qui pointe **ailleurs** : une ancienne installation de DrDXT,
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

    /// <summary>La ligne commence par un executable entre guillemets, nomme PresentMonService.exe, sous un dossier
    /// nomme DrDXT ; des arguments peuvent suivre (une installation plus ancienne en posait). Une enveloppe qui le cite
    /// plus loin n'est pas lui.</summary>
    internal static bool AncienEmplacementDeDrDxt(string binPath)
    {
        var t = binPath.Trim();
        var fin = t.Length > 1 && t[0] == '"' ? t.IndexOf('"', 1) : -1;
        if (fin < 0) return false;
        var chemin = t[1..fin];
        return string.Equals(Path.GetFileName(chemin), "PresentMonService.exe", StringComparison.OrdinalIgnoreCase)
            && chemin.Split('\\').SkipLast(1).Any(d => string.Equals(d, "DrDXT", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Le cas <see cref="PresentMonServiceAction.Repair"/> : arreter, puis reconfigurer.
    ///
    /// L'arret n'etait pas lu. S'il echouait, `sc config` passait quand meme, puis `Start`
    /// rendait Ok parce que le service tournait deja : la reparation annoncait un succes avec
    /// l'ancien binaire en place, et la mesure suivante passait a travers lui : exactement ce que
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
