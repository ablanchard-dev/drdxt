using System.Reflection;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// La décision du mainteneur : le SMU en lecture seule. Ce test rougit si une commande de réglage entre dans la liste.
public sealed class SmuAmdTests
{
    // Les numéros des commandes de RÉGLAGE de la boîte RSMU, relevés dans les mêmes sources (ZenStates-Core 869b7e82,
    // Zen2Settings.cs, hérité par Zen3Settings.cs ; Zen4Settings.cs, hérité par Zen5Settings.cs) : aucune lecture ne doit
    // porter l'un d'eux sur sa génération.
    private static readonly IReadOnlySet<uint> ReglagesZen2Et3 = new HashSet<uint>
        { 0x53, 0x54, 0x55, 0x56, 0x58, 0x5A, 0x5B, 0x5C, 0x5D, 0x61, 0x0A, 0x0B };
    private static readonly IReadOnlySet<uint> ReglagesZen4Et5 = new HashSet<uint>
        { 0x56, 0x57, 0x58, 0x59, 0x5B, 0x5D, 0x5E, 0x5F, 0x60, 0x61, 0x70, 0xCB, 0x06, 0x07, 0xA6, 0xA7 };

    // Les numéros de réglage de chaque puce, explicitement : une puce ajoutée à SmuAmd sans sa ligne ici fait échouer le test.
    private static readonly IReadOnlyDictionary<(int, int), IReadOnlySet<uint>> ReglagesParPuce = new Dictionary<(int, int), IReadOnlySet<uint>>
    {
        [(0x17, 0x71)] = ReglagesZen2Et3, [(0x19, 0x21)] = ReglagesZen2Et3,
        [(0x19, 0x61)] = ReglagesZen4Et5, [(0x1A, 0x44)] = ReglagesZen4Et5,
    };

    // Toutes les puces de SmuAmd, lues dans SmuAmd lui-même : aucune n'échappe à la garde.
    public static TheoryData<int, int> Puces()
    {
        var d = new TheoryData<int, int>();
        foreach (var (f, m) in SmuAmd.Puces) d.Add(f, m);
        return d;
    }

    [Fact]
    public void Chaque_puce_de_la_liste_a_ses_numeros_de_reglage_ici()
        => SmuAmd.Puces.Should().OnlyContain(p => ReglagesParPuce.ContainsKey(p)).And.NotBeEmpty();

    [Theory]
    [MemberData(nameof(Puces))]
    public void Chaque_commande_de_la_liste_lit_et_aucune_ne_porte_un_numero_de_reglage_de_sa_generation(int famille, int modele)
    {
        var lectures = SmuAmd.Lectures(famille, modele);
        ReglagesParPuce.TryGetValue((famille, modele), out var reglages).Should().BeTrue($"puce {famille:X}h {modele:X}h sans numéros de réglage");

        lectures.Should().NotBeEmpty();
        lectures.Should().OnlyContain(c => c.Nom.StartsWith("Get", StringComparison.Ordinal) || c.Nom == "TransferTableToDram");
        lectures.Select(c => c.Nom).Should().NotContain(n => n.Contains("Set", StringComparison.OrdinalIgnoreCase)
            || n.Contains("Enable", StringComparison.OrdinalIgnoreCase) || n.Contains("Disable", StringComparison.OrdinalIgnoreCase));
        lectures.Select(c => c.Numero).Should().NotIntersectWith(reglages);
        lectures.Select(c => c.Numero).Should().OnlyHaveUniqueItems();
        lectures.Should().OnlyContain(c => c.Source.Contains("869b7e82") || c.Source.Contains("d2983668"));
    }

    [Fact]
    public void Un_numero_qui_lit_sur_une_generation_regle_sur_une_autre_donc_la_liste_suit_la_puce_exacte()
    {
        // 0x06 lit l'adresse de la table PM sur Zen 2 et Zen 3, et règle la marge d'un cœur du Curve Optimizer sur Zen 4.
        SmuAmd.Lectures(0x17, 0x71).Should().Contain(c => c.Numero == 0x06 && c.Nom == "GetDramBaseAddress");
        ReglagesZen4Et5.Should().Contain(0x06u);
        SmuAmd.Lectures(0x19, 0x61).Should().NotContain(c => c.Numero == 0x06);
        // Une puce voisine, même de la même famille, n'a aucune commande.
        SmuAmd.Lectures(0x19, 0x11).Should().BeEmpty();
        SmuAmd.Lectures(0x19, 0x70).Should().BeEmpty();
    }

    [Fact]
    public void Chaque_puce_de_la_liste_est_reconnue_par_le_portail_CPUID()
    {
        foreach (var (famille, modele) in SmuAmd.Puces)
            GenerationAmd.Table.Should().Contain(l => l.Famille == famille && modele >= l.ModeleMin && modele <= l.ModeleMax);
    }

    [Fact]
    public void Une_commande_ne_se_fabrique_pas_hors_de_la_liste()
    {
        typeof(CommandeSmuLecture).GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty();
        typeof(CommandeSmuLecture).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Should().NotContain(m => m.ReturnType == typeof(CommandeSmuLecture));
    }
}
