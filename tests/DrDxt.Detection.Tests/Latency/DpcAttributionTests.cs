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
/// `EnumDeviceDrivers` ne donne pas la taille des modules : l'attribution prend donc le plus grand
/// base ≤ adresse, avec un garde-fou a 64 Mo au-dela duquel on rend `?` plutot qu'un nom faux.
/// </summary>
public class DpcAttributionTests
{
    private static readonly ulong[] Bases = { 0x1000, 0x5000, 0x9000 };
    private static readonly string[] Noms = { "premier.sys", "milieu.sys", "dernier.sys" };

    [Theory]
    [InlineData(0x1000UL, "premier.sys")]   // pile sur le base
    [InlineData(0x4FFFUL, "premier.sys")]   // juste avant le suivant
    [InlineData(0x5000UL, "milieu.sys")]    // pile sur le suivant
    [InlineData(0x9500UL, "dernier.sys")]   // dans le dernier module
    public void Une_adresse_tombe_dans_le_module_qui_la_contient(ulong routine, string attendu) =>
        DpcLatencyTracer.Attribuer(routine, Bases, Noms).Should().Be(attendu);

    [Fact]
    public void Une_adresse_sous_le_premier_module_n_est_attribuee_a_personne() =>
        DpcLatencyTracer.Attribuer(0x500, Bases, Noms).Should().Be("?");

    [Fact]
    public void Au_dela_de_64_Mo_du_dernier_base_on_rend_inconnu_plutot_qu_un_nom_faux() =>
        DpcLatencyTracer.Attribuer(0x9000 + 65UL * 1024 * 1024, Bases, Noms).Should().Be("?");

    [Fact]
    public void Sans_carte_des_pilotes_rien_n_est_invente() =>
        DpcLatencyTracer.Attribuer(0x1234, Array.Empty<ulong>(), Array.Empty<string>()).Should().Be("?");
}
