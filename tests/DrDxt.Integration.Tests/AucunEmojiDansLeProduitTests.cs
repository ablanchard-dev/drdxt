using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// 27/09/2026 : un « ⚠️ » restait dans la description d'un levier (FR et EN), lu par le joueur. Les
// pictogrammes decoratifs sont la marque la plus reconnaissable d'un texte ecrit par un assistant ; le
// produit parle en phrases. Le crible couvre le code, les vues et les textes.
public sealed class AucunEmojiDansLeProduitTests
{
    private static readonly Regex Emoji = new(@"[☀-➿⭐]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDEFF]|\uD83E[\uDD00-\uDEFF]");

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    [Fact]
    public void Le_crible_reconnait_un_pictogramme()
    {
        Emoji.IsMatch("Sans effet ⚠️ ici").Should().BeTrue();
        Emoji.IsMatch("Réussi ✅").Should().BeTrue();
        Emoji.IsMatch("un 🔴 rouge").Should().BeTrue();
        Emoji.IsMatch("Phrase française : « guillemets », 1 % → 2 %, cœur, 3×4").Should().BeFalse();
    }

    [Fact]
    public void Ni_le_code_ni_les_vues_ni_les_textes_ne_portent_de_pictogramme()
    {
        var src = Path.Combine(Racine().FullName, "src");
        var fichiers = new[] { "*.cs", "*.xaml", "*.resx" }
            .SelectMany(m => FichiersDuDepot.Sous(src, m))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        fichiers.Should().HaveCountGreaterThan(100, "temoin : le crible lit le produit");

        var fautes = fichiers
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => Emoji.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        fautes.Should().BeEmpty();
    }

    // 28/09/2026 : le crible ne lisait que `src`. L'installeur (script et Inno Setup) et le serveur de mises à
    // jour en portaient encore, dont un commentaire au ton de journal ; ils partent avec le produit.
    [Fact]
    public void Ni_l_installeur_ni_le_serveur_ne_portent_de_pictogramme()
    {
        var racine = Racine().FullName;
        var fichiers = new[] { ("installer", "*.iss"), ("installer", "*.ps1"), ("server", "*.cs") }
            .SelectMany(c => FichiersDuDepot.Sous(Path.Combine(racine, c.Item1), c.Item2))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .ToList();
        fichiers.Should().HaveCountGreaterThan(5, "temoin : le crible lit l'installeur et le serveur");

        var fautes = fichiers
            .SelectMany(f => File.ReadAllLines(f).Select((l, i) => (f, i, l)))
            .Where(x => Emoji.IsMatch(x.l))
            .Select(x => $"{Path.GetFileName(x.f)}:{x.i + 1}")
            .ToList();
        fautes.Should().BeEmpty();
    }
}
