namespace DrDxt.App.Platform;

/// <summary>
/// Le rapport de « Vérifier DrDXT » part par un ticket GitHub (modèle .github/ISSUE_TEMPLATE/rapport-de-verification.yml).
/// Aucun serveur : DrDXT ouvre le navigateur sur le modèle vide, le joueur y glisse le fichier, le relit et soumet lui-même.
/// Rien du rapport ni du matériel ne va dans l'adresse : le navigateur l'envoie à GitHub dès l'ouverture (et la garde dans
/// son historique), avant toute relecture.
/// </summary>
public static class LienDeRapport
{
    /// <summary>Le dépôt qui reçoit les rapports. Il passe au dépôt public le jour de la sortie (PUB4).</summary>
    public const string Depot = "ablanchard-dev/drdxt";

    /// <summary>Le guide de ce qui se fait à la main, étape par étape, lu sur le dépôt.</summary>
    public const string Guide = $"https://github.com/{Depot}/blob/main/docs/A-FAIRE-A-LA-MAIN.md";

    /// <summary>L'étape 5.23 (sous-tension), par l'ancre posée dans le guide : celle que GitHub tire d'un titre accentué
    /// n'est pas stable.</summary>
    public const string GuideSousTension = Guide + "#sous-tension";

    /// <summary>Le modèle de ticket, vide.</summary>
    public const string Nouveau = $"https://github.com/{Depot}/issues/new?template=rapport-de-verification.yml";

    /// <summary>Le modèle vide, quel que soit le rapport : processeur, carte et texte restent sur la machine jusqu'à ce que
    /// le joueur les glisse lui-même dans le ticket. Les paramètres restent pour l'appelant de l'écran.</summary>
    public static string Construire(string processeur, string carte, string rapport) => Nouveau;
}
