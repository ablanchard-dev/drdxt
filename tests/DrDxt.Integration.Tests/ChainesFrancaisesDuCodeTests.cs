using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

/// <summary>
/// 25/09/2026. Les deux gardes d'écriture lisaient le fichier de textes français ; les chaînes écrites dans le
/// code leur échappaient. Relues à la main, elles portaient 3 « on » du produit et 16 phrases sans accents
/// (message de démarrage, raisons de verdict, rapport de désinstallation). Ce crible lit les chaînes françaises
/// de `src`, trous d'interpolation retirés (« {probleme} » est un nom de variable, pas un mot affiché).
/// </summary>
public sealed class ChainesFrancaisesDuCodeTests
{
    private static readonly Regex Chaine = new("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant);
    private static readonly Regex Trou = new(@"\{[^{}]*\}", RegexOptions.CultureInvariant);
    private static readonly Regex Francais = new(@"[éèàùçêô]|\b(le|la|les|des|une|pas|est|rien|aucune?|du|au|aux|ton|ta|tes|sur|dans|avec|sans|que|alors|pu)\b|\b(qu|[nlqd])'[a-z]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Fautif = new(
        @"\b(reglages?|etats?|ete|etait|etre|derniere|premiere|deja|ecrans?|problemes?|systeme|securite|apres|tres|fenetres?|frequence|modele|sequence|residu|differentes?|endommagee?s?|reparation|reinstallation|verifiee?s?|memoire|parametres?|resultats?|donnees|mesurees?|mesuree|detectee?s?|peripheriques?|energie|depasse|references?|echecs?|echoue|ecrite?s?|defaut)\b"
        + @"|\b(nous|notre|nos)\b|\bon (te|t'|ne te|ne le|n'a|corrige|mesure|annule|ne touche)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Racine()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DrDXT.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DrDXT.sln introuvable");
    }

    private static string? Faute(string texte)
    {
        var sansTrous = Trou.Replace(texte, " ");
        return Francais.IsMatch(sansTrous) && Fautif.IsMatch(sansTrous) ? sansTrous : null;
    }

    [Fact]
    public void Aucune_chaine_francaise_du_code_ne_perd_ses_accents_ni_sa_voix()
    {
        // Témoins : les formes d'avant sont vues ; la forme juste et un nom de variable ne le sont pas.
        Faute("le GPU est deja sature : liberer du CPU ne rend rien").Should().NotBeNull();
        Faute("pour ne pas écraser un journal qu'on n'a pas su lire : ").Should().NotBeNull();
        // 25/09/2026 : sans accent ni article entouré d'espaces, cette chaîne n'était pas vue comme française.
        Faute("la sequence ne contient qu'un seul etat").Should().NotBeNull();
        // 26/09/2026 : deux phrases affichées sans accents passaient, faute de marqueur reconnu.
        Faute("{a} : {b} alors que {c} etait ").Should().NotBeNull();
        Faute("L'installeur n'a pas pu etre lance.").Should().NotBeNull();
        Faute("le GPU est déjà saturé : libérer du CPU ne rend rien").Should().BeNull();
        Faute("son pilote ne fonctionne pas ({probleme}) : le périphérique est inutilisable").Should().BeNull();

        var racine = Racine();
        var sep = Path.DirectorySeparatorChar;
        var fichiers = FichiersDuDepot.Sous(Path.Combine(racine, "src"))
            .Where(f => f.EndsWith(".cs", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"))
            .ToList();
        var chaines = fichiers
            .SelectMany(f => File.ReadLines(f).Select((l, i) => (f, i, l)))
            .Where(x => !x.l.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .SelectMany(x => Chaine.Matches(x.l).Select(m => (x.f, x.i, Texte: m.Groups[1].Value)))
            .Where(x => Francais.IsMatch(Trou.Replace(x.Texte, " ")))
            .ToList();
        chaines.Should().HaveCountGreaterThan(500, "src portait 695 chaînes françaises le 25/09/2026");

        chaines.Where(x => Faute(x.Texte) is not null)
            .Select(x => $"{Path.GetRelativePath(racine, x.f)}:{x.i + 1}")
            .Should().BeEmpty("une chaîne affichée garde ses accents et parle en « DrDXT »");
    }
}
