using DrDxt.Detection.Latency;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Latency;

/// <summary>
/// Attribuer une adresse de routine a un pilote — la seule logique du tracer DPC qui puisse etre
/// fausse en silence.
///
/// Se tromper ici ne leve rien et ne se voit pas : le tableau reste plein, les chiffres restent
/// plausibles, et c'est le NOM qui est faux. Accuser `nvlddmkm.sys` d'une saccade causee par un
/// pilote reseau enverrait le joueur desinstaller le mauvais pilote.
///
/// La taille de chaque module vient de l'en-tete PE de son fichier ; une adresse hors de [base, base + taille[
/// rend `?`. Taille non lue (0) : `?` aussi, jamais le module d'a cote.
/// </summary>
public class DpcAttributionTests
{
    private static readonly ulong[] Bases = { 0x1000, 0x5000, 0x9000 };
    private static readonly uint[] Tailles = { 0x4000, 0x4000, 0x4000 };
    private static readonly string[] Noms = { "premier.sys", "milieu.sys", "dernier.sys" };

    [Theory]
    [InlineData(0x1000UL, "premier.sys")]   // pile sur le base
    [InlineData(0x4FFFUL, "premier.sys")]   // juste avant le suivant
    [InlineData(0x5000UL, "milieu.sys")]    // pile sur le suivant
    [InlineData(0x9500UL, "dernier.sys")]   // dans le dernier module
    public void Une_adresse_tombe_dans_le_module_qui_la_contient(ulong routine, string attendu) =>
        DpcLatencyTracer.Attribuer(routine, Bases, Tailles, Noms).Should().Be(attendu);

    [Fact]
    public void Une_adresse_sous_le_premier_module_n_est_attribuee_a_personne() =>
        DpcLatencyTracer.Attribuer(0x500, Bases, Tailles, Noms).Should().Be("?");

    // Notre règle : une taille non lue ne donne jamais l'adresse au plus grand base ≤ adresse, pas même tout près.
    [Theory]
    [InlineData(0x5004UL)]
    [InlineData(0x9000UL + 65UL * 1024 * 1024)]
    public void Sans_taille_lue_l_adresse_n_est_a_personne(ulong routine) =>
        DpcLatencyTracer.Attribuer(routine, Bases, new uint[] { 0x4000, 0, 0 }, Noms).Should().Be("?");

    [Fact]
    public void Sans_carte_des_pilotes_rien_n_est_invente() =>
        DpcLatencyTracer.Attribuer(0x1234, Array.Empty<ulong>(), Array.Empty<uint>(), Array.Empty<string>()).Should().Be("?");

    // Entre la fin d'un module et le base du suivant, l'adresse n'est a personne : le plus grand base l'aurait
    // donnee au precedent.
    [Theory]
    [InlineData(0x1FFFUL, "premier.sys")]
    [InlineData(0x2000UL, "?")]
    [InlineData(0x4FFFUL, "?")]
    [InlineData(0x9FFFUL, "dernier.sys")]
    [InlineData(0xA000UL, "?")]
    public void Une_adresse_hors_de_la_taille_du_module_n_est_attribuee_a_personne(ulong routine, string attendu) =>
        DpcLatencyTracer.Attribuer(routine, Bases, new uint[] { 0x1000, 0x1000, 0x1000 }, Noms).Should().Be(attendu);

    [Fact]
    public void La_taille_se_lit_dans_l_en_tete_pe_d_un_chemin_noyau() =>
        DpcLatencyTracer.TailleImage(@"\SystemRoot\System32\ntoskrnl.exe").Should().BeGreaterThan(0x100000);

    // La session est créée en temps système (ClientContext = 2) : horodatages et InitialTime sont en unités de 100 ns. Diviser
    // par la fréquence du compteur de performance (PerfFreq de l'en-tête) n'était juste que là où elle vaut 10 MHz ; ailleurs
    // (matériel plus ancien, machines virtuelles), toutes les durées sortaient fausses.
    [Theory]
    [InlineData(10_000L, 1.0)]
    [InlineData(25_000L, 2.5)]
    [InlineData(1L, 0.0001)]
    public void Une_duree_se_lit_en_unites_de_100_ns(long ticks, double ms)
        => DpcLatencyTracer.DureeMs(ticks).Should().BeApproximately(ms, 1e-9);
}
