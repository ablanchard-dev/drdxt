using DrDxt.Core;
using DrDxt.Detection.Gpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

// Le pilote graphique 32 bits de chaque vraie carte : déclaré, puis présent sur le disque.
public sealed class PiloteGraphique32BitsTests
{
    private const string Umd64 = @"C:\WINDOWS\System32\DriverStore\FileRepository\x.inf\umd64.dll";
    private const string Umd32 = @"C:\WINDOWS\System32\DriverStore\FileRepository\x.inf\umd32.dll";

    [Fact]
    public void Declare_et_present_rien_a_redire()
        => PiloteGraphique32Bits.Juger([new("RTX", [Umd64], [Umd32])], _ => true).Single().Statut.Should().Be(RepairStatus.ExpectedDefault);

    [Fact]
    public void Non_declare_ou_fichier_manquant_appelle_la_reinstallation()
    {
        PiloteGraphique32Bits.Juger([new("RTX", [Umd64], null)], _ => true).Single().Statut.Should().Be(RepairStatus.Suspicious);
        var c = PiloteGraphique32Bits.Juger([new("RTX", [Umd64], [Umd32])], f => f != Umd32).Single();
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Explication.Should().Contain("umd32.dll");
    }

    // Un simple nom de fichier (la forme de la page Microsoft) se cherche dans SysWOW64, pas dans le dossier courant.
    [Fact]
    public void Un_nom_sans_dossier_se_cherche_dans_le_dossier_systeme_32_bits()
    {
        var syswow64 = Environment.GetFolderPath(Environment.SpecialFolder.SystemX86);
        var vus = new List<string>();
        PiloteGraphique32Bits.Defaut(new("RTX", [Umd64], ["umd32.dll", Umd32]), f => { vus.Add(f); return f.StartsWith(syswow64, StringComparison.OrdinalIgnoreCase) || f == Umd32; })
            .Should().BeNull();
        vus.Should().Equal(Path.Combine(syswow64, "umd32.dll"), Umd32);
    }

    // Notre règle : seules les vraies cartes comptent. Une carte sans pilote mode utilisateur lu (pilote de base, affichage
    // virtuel) ne fait pas un constat vide.
    [Fact]
    public void Une_carte_sans_pilote_mode_utilisateur_n_est_pas_jugee()
        => PiloteGraphique32Bits.Defaut(new("Microsoft Basic Display Adapter", null, null), _ => false).Should().BeNull();
}
