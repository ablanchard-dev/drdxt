using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// Les phrases du docteur passent, tranche par tranche, dans Docteur.resx / Docteur.fr.resx (DrDxt.Core). Ce cliquet compte
// les lignes de code qui portent encore une chaîne accentuée dans Detection et Measurement : il ne peut que baisser. Une
// chaîne qui LIT une sortie de Windows en français (« Activé ») reste, et reste comptée.
public sealed class LeDocteurNEcritPlusEnFrancaisDansLeCodeTests
{
    // Baisser ce nombre à chaque tranche convertie ; jamais le remonter.
    private const int Plafond = 36;

    private static readonly Regex ChaineAccentuee = new("\"[^\"\n]*[éèàùçêâîôûÉÀ][^\"\n]*\"");

    [Fact]
    public void Le_nombre_de_phrases_en_dur_du_docteur_ne_remonte_pas()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var lignes = new[] { "DrDxt.Detection", "DrDxt.Measurement" }
            .SelectMany(p => FichiersDuDepot.Sous(Path.Combine(d!.FullName, "src", p), "*.cs"))
            .SelectMany(File.ReadLines)
            .Count(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && ChaineAccentuee.IsMatch(l));

        lignes.Should().BeLessThanOrEqualTo(Plafond, "une phrase du docteur va dans Docteur.resx et Docteur.fr.resx");
    }

    // Le premier compte ne voyait que les chaînes accentuées, et seulement dans Detection et Measurement : « elle regroupe les
    // interruptions » ou les constats de Core et de Bios lui échappaient. Celui-ci compte une chaîne qui porte deux mots outils
    // du français, partout où le docteur écrit.
    private const int PlafondPhrases = 14;

    private static readonly Regex PhraseFrancaise = new(
        @"""[^""\n]*\b(le|la|les|des|du|une|est|pas|ne|sur|dans|qui|tes|ton|ta)\b[^""\n]*"
        + @"\b(le|la|les|des|du|une|est|pas|ne|sur|dans|qui|tes|ton|ta|de|en|au)\b[^""\n]*""");

    [Fact]
    public void Les_phrases_francaises_du_docteur_ne_remontent_pas_ou_qu_elles_soient()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        var lignes = new[] { "DrDxt.Detection", "DrDxt.Measurement", "DrDxt.Bios", "DrDxt.Core/Detection", "DrDxt.Core/Measurement",
                "DrDxt.Core/Elevation", "DrDxt.Application/Parties" }
            .SelectMany(p => FichiersDuDepot.Sous(Path.Combine(d!.FullName, "src", p), "*.cs"))
            .SelectMany(File.ReadLines)
            .Count(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal) && PhraseFrancaise.IsMatch(l));

        lignes.Should().BeLessThanOrEqualTo(PlafondPhrases, "une phrase du docteur va dans Docteur.resx et Docteur.fr.resx");
    }
}
