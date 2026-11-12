using System.Text.RegularExpressions;
using DrDxt.Core;
using DrDxt.Detection.Repair;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Une valeur absente veut dire « non lu » partout : l'écran écrit « non lisible » à sa place, le tri la compte parmi les
// lectures manquées, et « Détails techniques » l'écarte. Un constat lu qui ne porte pas de valeur se dit donc non lu à côté
// de sa propre explication.
public sealed partial class UnConstatLuPorteSaValeurTests
{
    [GeneratedRegex("""new RepairFinding\(\s*(?:"[^"]*"|[^,"()]+),\s*(?:"[^"]*"|[^,"()]+),\s*null\s*,\s*(?:null|"[^"]*"|[^,"()]+),\s*RepairStatus\.(\w+)""")]
    private static partial Regex SansValeur();

    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!.FullName;
    }

    [Fact]
    public void Aucun_constat_lu_n_est_construit_sans_valeur()
    {
        var fautifs = new List<string>();
        var construits = 0;
        foreach (var fichier in Directory.EnumerateFiles(Path.Combine(Racine(), "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (fichier.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
            var texte = File.ReadAllText(fichier);
            foreach (Match m in SansValeur().Matches(texte))
            {
                construits++;
                if (m.Groups[1].Value != nameof(RepairStatus.Unknown))
                    fautifs.Add($"{Path.GetFileName(fichier)}:{texte.AsSpan(0, m.Index).Count('\n') + 1}");
            }
        }

        construits.Should().BeGreaterThan(50, "le motif doit voir les constats non lus du produit, sinon il ne lit plus rien");
        fautifs.Should().BeEmpty("un constat lu porte une valeur ; sans elle, il s'affiche « non lisible » et se compte non lu");
    }

    [Fact]
    public void Le_motif_voit_un_constat_lu_sans_valeur_et_laisse_un_non_lu()
    {
        SansValeur().Match("""new RepairFinding("CPU", "x", null, null, RepairStatus.Observed, "y")""").Groups[1].Value.Should().Be("Observed");
        SansValeur().Match("new RepairFinding(Domaine, Nom,\n    null, \"0\", RepairStatus.Suspicious").Groups[1].Value.Should().Be("Suspicious");
        SansValeur().Match("""new RepairFinding("CPU", "x", null, null, RepairStatus.Unknown, "y")""").Groups[1].Value.Should().Be("Unknown");
        SansValeur().IsMatch("""new RepairFinding("CPU", "x", "12", null, RepairStatus.Observed, "y")""").Should().BeFalse();
    }

    [Fact]
    public void Les_attenuations_lues_se_comptent_lues()
    {
        var constat = AttenuationsProcesseur.Juger(new(0x01, 0x20));

        constat.ValeurCourante.Should().NotBeNull();
        Constat.Depuis(constat).Etat.Should().Be(EtatConstat.Lu);
    }
}
