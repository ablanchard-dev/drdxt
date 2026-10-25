using FluentAssertions;

namespace DrDxt.ReconcilerHost.Tests;

/// <summary>
/// La protection du ping s'allume quand le jeu retenu tourne. Le service voit les processus de toutes les sessions par leur
/// nom : un « cod.exe » lancé par un autre compte (une autre session) ou en session 0 allumerait sans ce tri le plafond de toute la machine.
/// Seul le jeu de la session du joueur compte : la seule session active (la console, ou le Bureau à distance), sinon la
/// console.
/// </summary>
public sealed class JeuDeLaSessionDuJoueurTests
{
    [Fact]
    public void Seul_le_jeu_de_la_session_de_la_console_compte()
    {
        HostServiceCollectionExtensions.JeuEnCours("cod.exe", [("cod", 1)], sessionDuJoueur: 1).Should().BeTrue();
        HostServiceCollectionExtensions.JeuEnCours("cod.exe", [("cod", 2)], sessionDuJoueur: 1).Should().BeFalse("un autre compte");
        HostServiceCollectionExtensions.JeuEnCours("cod.exe", [("cod", 0)], sessionDuJoueur: 1).Should().BeFalse("la session des services");
        HostServiceCollectionExtensions.JeuEnCours("cod.exe", [("autre", 1)], sessionDuJoueur: 1).Should().BeFalse();
    }

    // Le joueur en Bureau à distance : sa session est la seule active, la console attend à l'écran de connexion.
    [Fact]
    public void La_session_active_du_bureau_a_distance_est_celle_du_joueur()
        => HostServiceCollectionExtensions.SessionDuJoueur([(1, false), (3, true)], console: 1).Should().Be(3);

    [Fact]
    public void Sans_session_active_unique_la_console_fait_foi()
    {
        HostServiceCollectionExtensions.SessionDuJoueur([(1, false), (2, false)], console: 1).Should().Be(1);
        HostServiceCollectionExtensions.SessionDuJoueur([(2, true), (3, true)], console: 2).Should().Be(2);
        HostServiceCollectionExtensions.SessionDuJoueur([(0, true)], console: uint.MaxValue).Should().BeNull("la session 0 n'est jamais celle du joueur");
    }

    [Fact]
    public void Sans_session_a_la_console_aucun_jeu_ne_compte()
        => HostServiceCollectionExtensions.JeuEnCours("cod.exe", [("cod", 1)], sessionDuJoueur: null).Should().BeFalse();
}
