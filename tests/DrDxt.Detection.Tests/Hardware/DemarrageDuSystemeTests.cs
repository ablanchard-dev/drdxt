using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

// 07/10/2026 (F-32) : le dernier démarrage, rapide ou complet.
public sealed class DemarrageDuSystemeTests
{
    private static readonly DateTimeOffset Compteur = new(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Un_demarrage_rapide_plus_recent_que_le_compteur_fait_foi()
        => DemarrageDuSysteme.Choisir(Compteur.AddDays(5), Compteur).Should().Be(Compteur.AddDays(5),
            "après un arrêt et un démarrage rapide, le compteur peut ne pas être reparti de zéro");

    [Fact]
    public void Sans_evenement_ou_avec_un_evenement_plus_ancien_le_compteur_fait_foi()
    {
        DemarrageDuSysteme.Choisir(null, Compteur).Should().Be(Compteur);
        DemarrageDuSysteme.Choisir(Compteur.AddDays(-3), Compteur).Should().Be(Compteur);
    }

    [Fact, Trait("Category", "Machine")] // lit le vrai journal Système de cette machine
    public void Le_dernier_demarrage_se_lit_dans_le_journal_de_la_machine()
    {
        var e = DemarrageDuSysteme.DernierEvenement();
        Console.WriteLine($"dernier démarrage noté : {e:dd/MM/yyyy HH:mm:ss}, retenu : {DemarrageDuSysteme.Valeur:dd/MM/yyyy HH:mm:ss}");
        e.Should().NotBeNull().And.BeBefore(DateTimeOffset.Now);
        DemarrageDuSysteme.Valeur.Should().BeOnOrAfter(e!.Value);
    }
}
