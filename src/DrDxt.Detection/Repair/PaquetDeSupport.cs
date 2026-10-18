using System.IO.Compression;

namespace DrDxt.Detection.Repair;

/// <summary>
/// Le paquet d'aide en un clic : un zip local que le joueur envoie lui-même (Discord, ticket), fait d'une liste blanche
/// fermée. Le rapport du docteur, les parties relevées, le journal des changements et le journal de plantage de DrDXT, tous
/// passés par l'anonymiseur unique (<see cref="MachineExportee.AnonymiserTexte"/> : profil, nom du PC, adresses MAC et IP,
/// numéros de série, clé de produit, identifiants). Un fichier qui n'est pas nommé ici n'entre jamais, quel que soit son
/// nom : aucune variable de firmware, aucune table brute, aucun inconnu. Rien ne part sur le réseau.
/// </summary>
public static class PaquetDeSupport
{
    /// <summary>Les seuls fichiers du dossier des données qui peuvent entrer, en plus du rapport.</summary>
    public static readonly string[] ListeBlanche = ["parties.json", "parties-interrompues.json", "changements.json", "crash.log"];

    public const string NomDuRapport = "rapport-du-docteur.txt";

    /// <summary>Le contenu, anonymisé : le rapport puis chaque fichier de la liste blanche présent et lisible.</summary>
    public static IReadOnlyList<(string Nom, string Texte)> Contenu(string dossier, string rapport, IEnumerable<string> valeursDeCePc)
    {
        var valeurs = valeursDeCePc.ToList();
        var contenu = new List<(string, string)> { (NomDuRapport, MachineExportee.AnonymiserTexte(rapport, valeurs)) };
        foreach (var nom in ListeBlanche)
        {
            var chemin = Path.Combine(dossier, nom);
            try { if (File.Exists(chemin)) contenu.Add((nom, MachineExportee.AnonymiserTexte(File.ReadAllText(chemin), valeurs))); }
            catch (Exception) { /* un fichier verrouillé ou illisible reste dehors */ }
        }
        return contenu;
    }

    /// <summary>Pur : la ligne qui montre un fichier et sa taille en kilo-octets, arrondie au-dessus ; l'unité vient de la langue.</summary>
    public static string Ligne(string nom, string texte, string uniteKo)
        => $"{nom} ({(System.Text.Encoding.UTF8.GetByteCount(texte) + 1023) / 1024} {uniteKo})";

    /// <summary>Écrit le zip là où le joueur l'a choisi.</summary>
    public static void Ecrire(string chemin, IReadOnlyList<(string Nom, string Texte)> contenu)
    {
        using var flux = new FileStream(chemin, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(flux, ZipArchiveMode.Create);
        foreach (var (nom, texte) in contenu)
        {
            using var entree = new StreamWriter(zip.CreateEntry(nom, CompressionLevel.Optimal).Open());
            entree.Write(texte);
        }
    }
}
