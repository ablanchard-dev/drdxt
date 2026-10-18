using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Le dossier de données (%LOCALAPPDATA%\DrDXT) appartient au joueur, et l'App peut y écrire élevée. Après le contrôle du
// démarrage, un nom posé d'avance en lien y ferait écrire l'App élevée dans un autre fichier : toute écriture EN PLACE
// (ajout, création qui écrase, réécriture) le suivrait. Seules deux écritures y sont admises : FichierJsonAtomique.WriteAtomic
// (le renommage remplace le nom, il ne suit pas le lien) et FichierJsonAtomique.AjouterSansLien (n'écrit que dans un
// fichier à un seul nom, ouvert sans suivre de point de reparse).
//
// Mesuré par blanc-40 sur ce Windows (build 26200) : CreateHardLink vers un fichier sans droit d'écriture y est REFUSÉ. Le
// risque réel reste les autres builds, les liens symboliques (mode développeur), et les cibles que le joueur peut écrire
// mais que l'élévation rend dangereuses. Les tests de chaque magasin posent donc le lien vers un fichier témoin qu'ils
// possèdent, et vérifient qu'il n'est pas modifié.
//
// Ce crible voit une source qui nomme le dossier de données (DossierDonnees, CheminLocal) et écrit en place. Il ne voit pas
// un magasin qui reçoit son dossier en paramètre : ceux-là sont tenus par leurs propres tests.
public sealed class AucuneEcritureEnPlaceDansLesDonneesTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    private static readonly Regex NommeLesDonnees = new(@"DossierDonnees\.|CheminLocal\(");
    private static readonly Regex EcritEnPlace = new(
        @"File\.(AppendAllText|AppendAllLines|AppendText|WriteAllText|WriteAllBytes|WriteAllLines)\s*\(|FileMode\.(Create\b(?!New)|Append|OpenOrCreate)");
    private static readonly Regex BlocDeDebug = new(@"#if DEBUG.*?#endif", RegexOptions.Singleline);

    /// <summary>Les sources admises, chacune avec sa raison.</summary>
    private static readonly Dictionary<string, string> Admises = new()
    {
        ["FichierJsonAtomique.cs"] = "les deux écritures sûres elles-mêmes",
        ["DossierDeLAppEleve.cs"] = "le témoin, ouvert en lecture seule et jamais écrit",
        ["VerrouDuReleve.cs"] = "un verrou ouvert sans suivre de lien, supprimé à la fermeture, où rien n'est écrit",
    };

    private static bool Fautif(string source)
    {
        var sansDebug = BlocDeDebug.Replace(source, "");
        return NommeLesDonnees.IsMatch(sansDebug) && EcritEnPlace.IsMatch(sansDebug);
    }

    [Fact]
    public void Le_crible_reconnait_une_ecriture_en_place_dans_les_donnees()
    {
        Fautif("var p = DossierDonnees.Chemin(\"x.log\"); File.AppendAllText(p, l);").Should().BeTrue();
        Fautif("var p = FichierJsonAtomique.CheminLocal(\"x.json\"); new FileStream(p, FileMode.Create);").Should().BeTrue();
        Fautif("var p = DossierDonnees.Chemin(\"x.json\"); new FileStream(p, FileMode.CreateNew);").Should().BeFalse();
        Fautif("var p = DossierDonnees.Chemin(\"x\");\n#if DEBUG\nFile.WriteAllText(Path.GetTempPath(), s);\n#endif").Should().BeFalse();
    }

    [Fact]
    public void Aucune_source_n_ecrit_en_place_dans_le_dossier_de_donnees()
    {
        var sources = FichiersDuDepot.Sous(Path.Combine(Racine(), "src"), "*.cs").ToList();
        var nomment = sources.Where(f => NommeLesDonnees.IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();

        // Témoin réel : le crible lit les sources qui nomment le dossier de données.
        nomment.Should().Contain("FichierJsonAtomique.cs");
        sources.Where(f => !Admises.ContainsKey(Path.GetFileName(f)) && Fautif(File.ReadAllText(f)))
            .Select(Path.GetFileName).Should().BeEmpty();
    }
}
