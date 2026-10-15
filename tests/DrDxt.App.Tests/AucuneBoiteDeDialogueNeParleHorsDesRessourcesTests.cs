using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Les deux seules boites de dialogue du produit parlaient hors des ressources.
//
// La regle est ecrite, et depuis longtemps. `AppReconcilerSeams.cs` la cite en tete de
// fichier : « human-readable text resolves from a resx KEY via ILocalizer, never hardcoded
// ». Tout l'affichage la suit : les ViewModels, les convertisseurs, les vues XAML —
// balayees, une seule chaine en dur, la marque « DXT ».
//
// Deux surfaces ne la suivaient pas, et ce sont les deux moments ou le produit PARLE :
//
//   1. `MessageBoxConsentPrompt` — la seule question que DrDXT pose avant de toucher la
//      machine : « System Restore is disabled… Enable System Restore now? ». En ANGLAIS,
//      en dur. Sur la machine francaise a laquelle ce produit s'adresse, la seule demande
//      d'autorisation du logiciel s'affiche dans une langue que l'utilisateur n'a pas
//      choisie — et il doit repondre Oui ou Non.
//
//   2. Le message de plantage de `App.xaml.cs` — en FRANCAIS, en dur. L'exact miroir.
//
// Autrement dit : quelle que soit la langue du poste, l'un des deux dialogues est dans la
// mauvaise. Et les gardes de ressources de ce depot — pas de tiret cadratin, pas de
// capitales d'insistance, tutoiement — ne voient ni l'un ni l'autre, puisqu'elles lisent
// les `.resx`. Le titre anglais portait d'ailleurs « DrDXT — System Restore », un tiret
// cadratin que `ResxSansTiretCadratinTests` aurait refuse dans une ressource.
//
// Deux surfaces, une promesse, une seule l'appliquait. La garde ci-dessous ferme la porte.
// -----------------------------------------------------------------------
public sealed class AucuneBoiteDeDialogueNeParleHorsDesRessourcesTests
{
    private static string RacineApp()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "src", "DrDxt.App");
    }

    /// <summary>Tout appel a `MessageBox.Show(`, avec ce qui suit la parenthese.</summary>
    private static IEnumerable<(string Fichier, string Argument)> AppelsDeDialogue()
    {
        foreach (var fichier in Directory.EnumerateFiles(RacineApp(), "*.cs", SearchOption.AllDirectories))
        {
            if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                || fichier.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                continue;

            var texte = File.ReadAllText(fichier);
            foreach (Match m in Regex.Matches(texte, @"MessageBox\.Show\s*\(\s*([^;]{0,120})", RegexOptions.Singleline))
                yield return (Path.GetFileName(fichier), m.Groups[1].Value);
        }
    }

    [Fact]
    public void Le_crible_trouve_bien_les_boites_de_dialogue()
    {
        // Temoin : si ce motif cessait de matcher, le test suivant serait vert sur du vide
        // -- et c'est precisement ce genre de vert qui a laisse passer le defaut du jour.
        AppelsDeDialogue().Should().NotBeEmpty(
            "ce produit affiche au moins un dialogue : le message de plantage");
    }

    [Fact]
    public void Aucun_dialogue_n_affiche_une_phrase_ecrite_en_dur()
    {
        // Premiere VERSION : « tout litteral de 12 caracteres ou plus ». Elle accusait le
        // correctif lui-meme — `"Dialog.Crash.Message"` est un litteral, lui aussi. Ce qui
        // distingue une PHRASE d'une CLEF est l'espace : une clef resx n'en porte pas.
        var fautifs = AppelsDeDialogue()
            .Where(a => Regex.IsMatch(a.Argument, "\"[^\"]*[^\"\\s][ ][^\"]*\""))
            .Select(a => $"{a.Fichier} : {a.Argument.Split('\n')[0].Trim()}")
            .ToList();

        fautifs.Should().BeEmpty(
            "un dialogue ecrit en dur ne suit ni la langue du poste ni les gardes de "
            + "ressources de ce depot. Passez par une clef resx et ILocalizer");
    }

    [Fact]
    public void Les_clefs_des_deux_dialogues_existent_dans_LES_DEUX_langues()
    {
        // Contrepoids : deplacer une phrase vers une clef ne sert a rien si la clef
        // n'existe pas -- `ResxLocalizer` rendrait alors la CLEF a l'utilisateur, et le
        // dialogue afficherait « Dialog.Crash.Message ».
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln"))) dir = dir.Parent;
        var ressources = Path.Combine(dir!.FullName, "src", "DrDxt.Infrastructure", "Resources");

        string[] clefs =
        [
            "Dialog.Crash.Title", "Dialog.Crash.Message",
            "Dialog.Restore.Title", "Dialog.Restore.Question",
        ];

        foreach (var fichier in new[] { "Strings.resx", "Strings.fr.resx" })
        {
            var texte = File.ReadAllText(Path.Combine(ressources, fichier));
            foreach (var clef in clefs)
                texte.Should().Contain($"name=\"{clef}\"", $"{fichier} doit porter {clef}");
        }
    }
}
