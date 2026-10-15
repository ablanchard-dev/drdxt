using System.Text.RegularExpressions;
using System.Xml.Linq;
using FluentAssertions;

namespace DrDxt.Integration.Tests;

// -----------------------------------------------------------------------
// Une traduction manquante ne leve rien — elle S'AFFICHE.
//
// Contrat de `ResxLocalizer`, ecrit dans son propre resume : « Missing keys return the
// key itself — never null, never an exception. » C'est un bon contrat pour un ecran
// (rien ne plante), et c'est exactement ce qui rend le defaut invisible : la seule
// personne qui voit la clef brute, c'est le client.
//
// Exemple : si les verdicts que `AbComparer` produit (`Scan.Ab.MeasurableGain`, `Scan.Ab.NoMeasurableGain`,
// `Scan.Ab.Regression`) manquaient aux deux resx, la ligne qui dit au joueur ce que la mesure a trouve
// afficherait son propre code.
//
// Ce crible ne regarde pas une liste : il lit toutes les chaines litterales du livre qui
// ressemblent a une clef de ressource et dont le premier segment est un prefixe deja
// present dans le resx (`Scan.`, `License.`, `UI.`...). Une clef ecrite dans le code et
// jamais traduite ressort ici, dans les deux langues que le produit embarque.
//
// Bruyance mesuree avant de l'ecrire : 397 candidats, 1 seule fausse accusation -- une
// reference de documentation `<see cref="Finding.EstimatedGainPct"/>`, ou le point separe
// un type de son membre et pas un prefixe d'une clef. Les `cref` sont donc retires. Zero
// accusation restante sur le depot corrige.
// -----------------------------------------------------------------------
public sealed class AucuneClefAfficheeNeSortEnBrutTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    private static string Resx(string nom) =>
        Path.Combine(Racine().FullName, "src", "DrDxt.Infrastructure", "Resources", nom);

    private static HashSet<string> ClefsDe(string nom)
        => XDocument.Load(Resx(nom)).Root!.Elements("data")
            .Select(d => (string?)d.Attribute("name") ?? "")
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Une chaine litterale en deux segments ou plus : `Scan.Ab.Regression`.</summary>
    private static readonly Regex Litteral = new("\"([A-Za-z][A-Za-z0-9_]*(?:\\.[A-Za-z0-9_]+)+)\"");

    /// <summary>Une valeur donnee a une propriete `…Key` est une clef, quel que soit son
    /// prefixe. Le filtre par prefixe connu laissait passer `measure.network.icmp_caveat` : aucune clef
    /// du resx ne commencait par `measure.`, et l'avertissement n'avait de texte nulle part.</summary>
    private static readonly Regex ProprieteClef = new("Key\\s*=>\\s*\"([^\"]+)\"");

    /// <summary>`cref="Type.Membre"` : de la documentation, pas une clef affichee.</summary>
    private static readonly Regex Cref = new("cref=\"[^\"]*\"");

    private static List<(string Clef, string Fichier)> ClefsEcritesDansLeCode(HashSet<string> connues)
    {
        var prefixes = connues.Select(c => c.Split('.')[0]).ToHashSet(StringComparer.Ordinal);
        var trouvees = new List<(string, string)>();

        foreach (var chemin in FichiersDuDepot.Sous(Path.Combine(Racine().FullName, "src"), "*.cs"))
        {
            var texte = Cref.Replace(File.ReadAllText(chemin), " ");
            foreach (Match m in Litteral.Matches(texte))
            {
                var clef = m.Groups[1].Value;
                if (prefixes.Contains(clef.Split('.')[0]))
                    trouvees.Add((clef, Path.GetFileName(chemin)));
            }
            foreach (Match m in ProprieteClef.Matches(texte))
                trouvees.Add((m.Groups[1].Value, Path.GetFileName(chemin)));
        }
        return trouvees;
    }

    [Fact]
    public void Le_crible_lit_bien_les_deux_resx_et_le_livre()
    {
        // Temoin de contenu : sans lui, un chemin change rendrait tout vert sur du vide.
        var neutre = ClefsDe("Strings.resx");
        var francais = ClefsDe("Strings.fr.resx");

        neutre.Should().HaveCountGreaterThan(400);
        francais.Should().HaveCountGreaterThan(400);
        ClefsEcritesDansLeCode(neutre).Should().HaveCountGreaterThan(100);
    }

    [Fact]
    public void Les_deux_langues_annoncees_portent_exactement_les_memes_clefs()
    {
        // `SatelliteResourceLanguages` = en;fr. Une clef presente d'un seul cote, c'est la
        // meme panne, reservee a la moitie des clients.
        var neutre = ClefsDe("Strings.resx");
        var francais = ClefsDe("Strings.fr.resx");

        neutre.Except(francais).Should().BeEmpty("ces clefs n'ont pas de traduction francaise");
        francais.Except(neutre).Should().BeEmpty("ces clefs n'existent que cote francais");
    }

    [Fact]
    public void Aucune_clef_ecrite_dans_le_code_ne_manque_au_resx()
    {
        var neutre = ClefsDe("Strings.resx");
        // Une phrase du docteur (Docteur.T) vit dans le resx de Core ; elle est traduite aussi.
        var docteur = XDocument.Load(Path.Combine(Racine().FullName, "src", "DrDxt.Core", "Resources", "Docteur.resx")).Root!
            .Elements("data").Select(d => (string?)d.Attribute("name") ?? "").ToHashSet(StringComparer.Ordinal);

        var manquantes = ClefsEcritesDansLeCode(neutre)
            .Where(x => !neutre.Contains(x.Clef) && !docteur.Contains(x.Clef))
            .Select(x => $"{x.Clef} ({x.Fichier})")
            .Distinct()
            .ToList();

        manquantes.Should().BeEmpty(
            "une clef sans entree resx ne leve rien : le localiseur rend la clef elle-meme, " +
            "et c'est le client qui lit le code a l'ecran");
    }

    [Fact]
    public void Le_crible_VOIT_une_clef_neuve_non_traduite()
    {
        // Un crible qu'on ne peut pas faire tomber sur commande ne prouve rien.
        var neutre = ClefsDe("Strings.resx");
        var prefixe = neutre.First().Split('.')[0];
        var inventee = prefixe + ".Clef.Jamais.Traduite";

        neutre.Contains(inventee).Should().BeFalse();
        Litteral.IsMatch($"var k = \"{inventee}\";").Should().BeTrue(
            "le motif doit reconnaitre une clef ecrite dans le code");
    }

    [Fact]
    public void Le_crible_VOIT_une_propriete_Key_a_prefixe_inconnu()
    {
        ProprieteClef.Match("public string CaveatKey => \"measure.network.icmp_caveat\";").Groups[1].Value
            .Should().Be("measure.network.icmp_caveat");
    }

    [Fact]
    public void Une_reference_de_documentation_n_est_pas_accusee()
    {
        // Le contrepoids, sur la seule fausse accusation mesuree avant d'ecrire ce crible.
        var doc = "/// <see cref=\"Finding.EstimatedGainPct\"/> est une estimation.";

        Cref.Replace(doc, " ").Should().NotContain("Finding.EstimatedGainPct");
        Litteral.IsMatch(Cref.Replace(doc, " ")).Should().BeFalse();
    }
}
