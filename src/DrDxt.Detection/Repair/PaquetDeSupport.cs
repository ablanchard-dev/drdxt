using System.IO.Compression;
using DrDxt.Core;

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

    /// <summary>La note qui nomme ce qui n'a pas pu entrer : le joueur la voit dans l'aperçu, et l'aide la lit dans le zip.</summary>
    public const string NomDeLaNote = "non-inclus.txt";

    /// <summary>Le contenu, anonymisé : le rapport puis chaque fichier de la liste blanche présent et lisible.</summary>
    public static IReadOnlyList<(string Nom, string Texte)> Contenu(string dossier, string rapport, IEnumerable<string> valeursDeCePc)
    {
        var valeurs = valeursDeCePc.ToList();
        var contenu = new List<(string, string)>();
        // Ce qui n'entre pas ne part jamais brut (l'anonymisation a un délai par règle) ; il ne disparaît pas non plus en
        // silence : la note le nomme.
        var dehors = new List<string>();
        try { contenu.Add((NomDuRapport, MachineExportee.AnonymiserTexte(rapport, valeurs))); }
        catch (Exception) { dehors.Add(NomDuRapport); }
        foreach (var nom in ListeBlanche)
        {
            var chemin = Path.Combine(dossier, nom);
            try
            {
                if (File.Exists(chemin))
                {
                    var texte = File.ReadAllText(chemin);
                    contenu.Add((nom, nom.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? MachineExportee.AnonymiserJson(texte, valeurs) : MachineExportee.AnonymiserTexte(texte, valeurs)));
                }
            }
            catch (Exception) { dehors.Add(nom); }
        }
        if (dehors.Count > 0) contenu.Add((NomDeLaNote, Docteur.T("PQ.NonInclus", string.Join(", ", dehors))));
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
