using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Troisieme mesure d'affilee sur le payload construit, apres les
// `.pdb` et le service livre deux fois. Celle-ci est la plus lourde :
//
//   installer\publish\  ->  13 dossiers de langue, **8,88 Mo**
//   cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant
//
// Or DrDXT parle exactement deux langues : le neutre (`Strings.resx`, declare
// `en`) et le francais (`Strings.fr.resx`). Les douze autres dossiers ne
// contiennent aucune ressource de DrDXT -- uniquement les satellites du
// framework WPF (PresentationCore, UIAutomation*, Ribbon...), c'est-a-dire les
// messages internes de WPF traduits en tcheque, en coreen ou en turc, pour une
// application dont toute l'interface est en francais et dont le repli est
// l'anglais.
//
// Sur une machine dont l'interface Windows est en tcheque, les chaines de DrDXT
// tombent deja sur le neutre anglais ; ce que ces 8 Mo achetent, c'est que le
// message d'exception interne de WPF soit, lui, en tcheque. Le client, lui,
// telecharge et pose tout.
//
// Le correctif ne choisit pas les langues a la main : `SatelliteResourceLanguages`
// doit dire exactement ce que le depot traduit. Le jour ou quelqu'un ajoute
// `Strings.de.resx`, ce test rougit tant que l'allemand n'est pas embarque --
// c'est l'inverse d'une liste figee, qui aurait silencieusement coupe la
// nouvelle traduction.
// -----------------------------------------------------------------------
public sealed class LeProduitNEmbarqueQueLesLanguesQuIlParleTests
{
    private static string RacineDuDepot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    /// <summary>Les cultures que le depot traduit vraiment, lues dans les fichiers de ressources.</summary>
    private static SortedSet<string> CulturesTraduites()
    {
        var cultures = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fichier in FichiersDuDepot.Sous(Path.Combine(RacineDuDepot(), "src"), "*.resx"))
        {
            if (fichier.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            // Strings.fr.resx -> fr ; Strings.resx -> neutre, pas de satellite.
            var m = Regex.Match(Path.GetFileName(fichier), @"^[^.]+\.([A-Za-z]{2}(?:-[A-Za-z0-9]+)?)\.resx$");
            if (m.Success) cultures.Add(m.Groups[1].Value);
        }
        return cultures;
    }

    private static string Csproj() =>
        File.ReadAllText(Path.Combine(RacineDuDepot(), "src", "DrDxt.App", "DrDxt.App.csproj"));

    private static SortedSet<string> LanguesEmbarquees()
    {
        var m = Regex.Match(Csproj(), @"<SatelliteResourceLanguages>([^<]*)</SatelliteResourceLanguages>");
        var declarees = m.Success ? m.Groups[1].Value : string.Empty;
        return new SortedSet<string>(
            declarees.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Le_crible_trouve_bien_les_traductions_du_depot()
    {
        // Temoin : si les .resx disparaissaient, le test suivant serait vert sur du vide.
        CulturesTraduites().Should().Contain("fr", "le depot porte Strings.fr.resx");
        File.Exists(Path.Combine(RacineDuDepot(), "src", "DrDxt.Infrastructure", "Resources", "Strings.resx"))
            .Should().BeTrue("le neutre doit exister, c'est le repli");
    }

    [Fact]
    public void Le_payload_ne_porte_que_les_langues_du_depot()
    {
        var traduites = CulturesTraduites();
        var embarquees = LanguesEmbarquees();

        embarquees.Should().NotBeEmpty(
            "sans <SatelliteResourceLanguages>, le publish emporte les satellites de TOUTES les langues " +
            "du framework WPF -- mesure du 18/09/2026 : 13 dossiers, 8,88 Mo, pour une application qui " +
            "ne parle que le francais et l'anglais");

        // `en` est le neutre : il n'a pas de satellite, mais il doit rester declare, sinon le
        // SDK peut ecarter les ressources de repli.
        embarquees.Should().Contain("en", "le neutre est le repli de toutes les cultures non traduites");
        embarquees.Except(new[] { "en" }, StringComparer.OrdinalIgnoreCase)
            .Should().BeEquivalentTo(traduites,
                "la liste embarquee doit DECOULER de ce que le depot traduit : une langue traduite mais " +
                "non embarquee ne partirait pas, une langue embarquee mais non traduite est du poids mort");
    }

    [Fact]
    public void Une_traduction_ajoutee_ne_peut_pas_rester_a_quai()
    {
        // L'autre sens, et c'est lui qui justifie de deriver la liste au lieu de la figer :
        // le crible doit pouvoir voir une culture qu'il ne connait pas d'avance.
        const string motif = @"^[^.]+\.([A-Za-z]{2}(?:-[A-Za-z0-9]+)?)\.resx$";

        Regex.IsMatch("Strings.de.resx", motif).Should().BeTrue();
        Regex.IsMatch("Strings.pt-BR.resx", motif).Should().BeTrue("une culture regionale est une culture");
        Regex.IsMatch("Strings.resx", motif).Should().BeFalse("le neutre n'est pas une culture satellite");
    }
}
