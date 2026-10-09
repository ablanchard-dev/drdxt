using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 25/09/2026. Trois textes affichés du fichier français étaient écrits sans accents (« aucun reglage n'a
/// encore ete applique », « l'etat ou il etait »), dont l'infobulle du bouton principal « Optimiser ». Ce crible
/// refuse les mots courants qui n'existent pas sans accent en français.
/// </summary>
public sealed class TextesFrancaisAccentuesTests
{
    private static readonly Regex SansAccent = new(
        @"\b(reglages?|etats?|ete|etait|deja|ecrans?|problemes?|systeme|securite|apres|tres|fenetres?|frequence|modele|sequence|residu|differentes?|endommagee?s?|reparation|reinstallation|verifiee?s?|memoire|parametres?|resultats?|donnees|mesurees?|mesuree|detectee?s?|peripheriques?|energie|depasse|references?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    [Fact]
    public void Aucun_texte_affiche_n_ecrit_un_mot_courant_sans_ses_accents()
    {
        // Témoins : la forme fautive est vue, la forme juste ne l'est pas.
        SansAccent.IsMatch("aucun reglage n'a encore ete applique").Should().BeTrue();
        SansAccent.IsMatch("aucun réglage n'a encore été appliqué, l'état d'origine").Should().BeFalse();

        var fichier = Path.Combine(Racine(), "src", "DrDxt.Infrastructure", "Resources", "Strings.fr.resx");
        var valeurs = XDocument.Load(fichier).Descendants("data")
            .Select(d => (Cle: (string?)d.Attribute("name") ?? "", Texte: (string?)d.Element("value") ?? ""))
            .ToList();
        valeurs.Should().HaveCountGreaterThan(400, "le fichier de textes français en comptait 465 le 25/09/2026");

        valeurs.Where(v => SansAccent.IsMatch(v.Texte)).Select(v => v.Cle)
            .Should().BeEmpty("un texte affiché en français porte ses accents");
    }
}
