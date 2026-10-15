using DrDxt.Application.Parties;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Un réglage basculé entre deux scans devient un changement borné par les deux relevés.
public sealed class SuiviDesReglagesTests : IDisposable
{
    private readonly string _chemin = Path.Combine(Path.GetTempPath(), $"suivi-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_chemin); } catch { } }

    private static string Libelle(string nom, string avant, string apres) => $"{nom}: {avant}>{apres}";

    [Fact]
    public void Une_bascule_entre_deux_releves_devient_un_changement_borne()
    {
        var s = new SuiviDesReglages(_chemin);
        var t1 = new DateTimeOffset(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);
        var t2 = t1.AddHours(5);

        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé" }, t1, Libelle).Should().BeEmpty("premier relevé : aucun avant");
        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé" }, t1.AddHours(1), Libelle).Should().BeEmpty("rien n'a bougé");

        var c = s.Relever(new Dictionary<string, string> { ["IR"] = "éteint" }, t2, Libelle).Should().ContainSingle().Subject;
        c.Libelle.Should().Be("IR: allumé>éteint");
        c.Quand.Should().Be(t2);
        c.Depuis.Should().Be(t1.AddHours(1), "la bascule a eu lieu entre les deux derniers relevés");
    }

    [Fact]
    public void Un_reglage_non_relu_n_est_pas_un_changement_et_garde_son_etat()
    {
        var s = new SuiviDesReglages(_chemin);
        var t = DateTimeOffset.Now;
        s.Relever(new Dictionary<string, string> { ["IR"] = "allumé", ["HAGS"] = "1" }, t, Libelle);
        s.Relever(new Dictionary<string, string> { ["HAGS"] = "1" }, t.AddMinutes(1), Libelle).Should().BeEmpty();
        var c = s.Relever(new Dictionary<string, string> { ["IR"] = "éteint" }, t.AddMinutes(2), Libelle)
            .Should().ContainSingle(c => c.Libelle == "IR: allumé>éteint", "l'état d'avant a survécu au relevé qui ne l'a pas lu").Subject;
        c.Depuis.Should().Be(t, "borné par le dernier relevé qui a LU ce réglage, pas par le dernier relevé tout court");
    }
}
