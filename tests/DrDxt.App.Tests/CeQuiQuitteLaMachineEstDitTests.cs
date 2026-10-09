using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// Ce qui quitte la machine.
//
// Jusqu'au 23/09/2026, DrDXT envoyait deux choses a un tiers : la cle de licence et une
// empreinte materielle, vers api.keygen.sh. DrDXT est devenu gratuit et Keygen est parti avec
// la licence. Ce qui sort encore (README, « Ce qui quitte la machine », tenu par le test du bas) :
//   * la lecture des dernieres versions de pilotes (pages publiques d'AMD et de NVIDIA, rien sur la machine) ;
//   * des pings sans contenu (box, 1.1.1.1, serveur du jeu pendant une partie) ;
//   * le flux de mise a jour, et seulement si une adresse HTTPS est configuree.
// Le journal de plantage (crash.log) reste en local, rien ne l'envoie.
//
// Ce fichier tient cette phrase : aucun hote n'est contacte EN DUR par le code produit. Un
// hote qui reapparait (telemetrie, licence, analytics) rougit ici avant d'atteindre quelqu'un.
// -----------------------------------------------------------------------
public sealed class CeQuiQuitteLaMachineEstDitTests
{
    private static readonly Regex HoteEnDur = new(
        @"(BaseAddress\s*=\s*new Uri\(|new Uri\(|GetAsync\(|PostAsync\()\s*""https?://([^/""]+)",
        RegexOptions.Compiled);

    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    /// <summary>Les hotes que le code produit contacte, en dur.</summary>
    private static SortedSet<string> HotesContactes()
    {
        var trouves = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var src = new DirectoryInfo(Path.Combine(Racine().FullName, "src"));
        foreach (var fichier in src.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (fichier.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            foreach (Match m in HoteEnDur.Matches(File.ReadAllText(fichier.FullName)))
                trouves.Add(m.Groups[2].Value);
        }
        return trouves;
    }

    [Fact]
    public void Le_crible_verrait_un_hote_ecrit_en_dur()
    {
        // Temoin : sans lui, un motif casse rendrait le test suivant vert sur le vide.
        HoteEnDur.IsMatch("http.BaseAddress = new Uri(\"https://api.keygen.sh/v1/\");").Should().BeTrue();
        HoteEnDur.IsMatch("await client.PostAsync(\"https://telemetrie.example/x\", body);").Should().BeTrue();
    }

    [Fact]
    public void Le_crible_lit_bien_les_sources_du_produit()
    {
        // Temoin de lecture : une liste d'hotes vide parce qu'on n'a lu aucun fichier
        // ressemblerait trait pour trait a une liste vide parce que rien ne sort.
        new DirectoryInfo(Path.Combine(Racine().FullName, "src"))
            .GetFiles("*.cs", SearchOption.AllDirectories).Length
            .Should().BeGreaterThan(200);
    }

    [Fact]
    public void Aucun_hote_n_est_contacte_en_dur_par_le_produit()
    {
        HotesContactes().Should().BeEmpty(
            "les adresses que DrDXT contacte vivent dans des constantes nommees, jamais en ligne dans l'appel");
    }

    // 08/10/2026 (chantier V1) : le crible ci-dessus ne voyait pas `GetStringAsync(Url)` avec l'adresse dans une
    // constante, et le README disait « aucune donnee » alors que chaque scan lit les pages de pilotes de NVIDIA et d'AMD.
    // Tout fichier du produit qui fait une requete HTTP : chaque hote qu'il ecrit doit etre nomme dans la section
    // « Ce qui quitte la machine » du README. Les adresses IP pinguees en dur aussi.
    private static readonly Regex AppelHttp = new(@"HttpClient|GetStringAsync|GetAsync|PostAsync|SendAsync", RegexOptions.Compiled);
    private static readonly Regex Hote = new(@"""https?://([^/""?]+)", RegexOptions.Compiled);
    private static readonly Regex IpPinguee = new(@"(?:IPAddress\.Parse\(|ReferenceInternetParDefaut\s*=\s*)""(\d{1,3}(?:\.\d{1,3}){3})""", RegexOptions.Compiled);

    private static string SectionDuReadme()
    {
        var readme = File.ReadAllText(Path.Combine(Racine().FullName, "README.md"));
        var debut = readme.IndexOf("## Ce qui quitte la machine", StringComparison.Ordinal);
        debut.Should().BeGreaterThan(-1, "le README garde sa section « Ce qui quitte la machine »");
        var fin = readme.IndexOf("\n## ", debut + 5, StringComparison.Ordinal);
        return fin < 0 ? readme[debut..] : readme[debut..fin];
    }

    [Fact]
    public void Chaque_hote_et_chaque_adresse_pinguee_du_produit_est_dit_dans_le_README()
    {
        var src = new DirectoryInfo(Path.Combine(Racine().FullName, "src"));
        var contactes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var fichier in src.GetFiles("*.cs", SearchOption.AllDirectories))
        {
            if (fichier.FullName.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")) continue;
            var texte = File.ReadAllText(fichier.FullName);
            foreach (Match m in IpPinguee.Matches(texte)) contactes.Add(m.Groups[1].Value);
            if (!AppelHttp.IsMatch(texte)) continue;
            foreach (Match m in Hote.Matches(texte)) contactes.Add(m.Groups[1].Value);
        }
        contactes.Should().Contain("www.amd.com", "temoin : le crible voit bien la page de pilotes d'AMD");

        var section = SectionDuReadme();
        contactes.Where(h => !section.Contains(h, StringComparison.OrdinalIgnoreCase)).Should().BeEmpty(
            "tout ce que DrDXT contacte est dit au joueur, dans le README");
    }
}
