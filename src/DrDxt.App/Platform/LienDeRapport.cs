namespace DrDxt.App.Platform;

/// <summary>
/// Le rapport de « Vérifier DrDXT » part par un ticket GitHub
/// pré-rempli (modèle .github/ISSUE_TEMPLATE/rapport-de-verification.yml, champ « rapport »). Aucun serveur : DrDXT ouvre
/// le navigateur, le joueur relit et soumet lui-même. Une adresse trop longue est refusée par les navigateurs et par
/// GitHub : au-delà de <see cref="LongueurMax"/>, le rapport est coupé et le dit (le fichier complet se joint au ticket).
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

    public const int LongueurMax = 7000;

    private static string Coupure => "\n\n" + DrDxt.Core.Docteur.T("App.RapportCoupe");

    public static string Construire(string processeur, string carte, string rapport)
    {
        string Lien(string texte) => $"https://github.com/{Depot}/issues/new?template=rapport-de-verification.yml"
            + $"&title={Uri.EscapeDataString($"Vérification : {processeur} + {carte}")}"
            + $"&rapport={Uri.EscapeDataString(texte)}";

        var lien = Lien(rapport);
        if (lien.Length <= LongueurMax) return lien;
        // Coupe par moitiés : l'encodage change la longueur d'un caractère à l'autre.
        int bas = 0, haut = rapport.Length;
        while (bas < haut)
        {
            var milieu = (bas + haut + 1) / 2;
            if (Lien(rapport[..milieu] + Coupure).Length <= LongueurMax) bas = milieu; else haut = milieu - 1;
        }
        return Lien(rapport[..bas] + Coupure);
    }
}
