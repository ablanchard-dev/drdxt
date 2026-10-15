using DrDxt.App.Platform;
using FluentAssertions;

namespace DrDxt.App.Tests;

// Le lanceur du jeu et ce qu'il fait tourner ne sont pas des voisins à calmer. Une table de
// processus écrite à la main ; aucun vrai processus n'est lu.
public sealed class FamilleDuJeuTests
{
    private const int Explorer = 10, Steam = 20, Cod = 30, CrashHandler = 31, Shader = 32, WebHelper = 21, Overlay = 22,
        WebHelperEnfant = 23, Obs = 40, Chrome = 41, Services = 5, Svchost = 6;

    private static readonly Dictionary<int, (int Parent, string Nom)> Table = new()
    {
        [Explorer] = (1, "explorer"),
        [Steam] = (Explorer, "steam"),
        [WebHelper] = (Steam, "steamwebhelper"),
        [WebHelperEnfant] = (WebHelper, "steamwebhelper"),
        [Overlay] = (Steam, "GameOverlayUI"),
        [Cod] = (Steam, "cod"),
        [CrashHandler] = (Cod, "crashpad_handler"),
        [Shader] = (CrashHandler, "shadercompile"),
        [Obs] = (Explorer, "obs64"),
        [Chrome] = (Explorer, "chrome"),
        [Services] = (1, "services"),
        [Svchost] = (Services, "svchost"),
    };

    [Fact]
    public void Le_jeu_ses_enfants_son_lanceur_et_ce_que_le_lanceur_fait_tourner_sont_ecartes()
    {
        FamilleDuJeu.De(Cod, Table).Should().BeEquivalentTo(
            [Cod, CrashHandler, Shader, Steam, Explorer, WebHelper, WebHelperEnfant, Overlay]);
    }

    [Fact]
    public void Un_jeu_lance_par_l_explorateur_n_ecarte_pas_les_autres_programmes_de_l_explorateur()
    {
        var table = new Dictionary<int, (int Parent, string Nom)>(Table) { [Cod] = (Explorer, "cod") };
        var famille = FamilleDuJeu.De(Cod, table);
        famille.Should().Contain([Cod, CrashHandler, Shader, Explorer]);
        famille.Should().NotContain([Obs, Chrome, Steam, WebHelper]);
    }

    [Fact]
    public void Un_lanceur_de_windows_ou_une_boucle_de_parents_ne_font_rien_deborder()
    {
        var table = new Dictionary<int, (int Parent, string Nom)>(Table) { [Cod] = (Svchost, "cod"), [Services] = (Svchost, "services") };
        FamilleDuJeu.De(Cod, table).Should().BeEquivalentTo([Cod, CrashHandler, Shader, Svchost, Services]);
        FamilleDuJeu.De(99, Table).Should().BeEquivalentTo([99], "un jeu absent de la table n'a que lui-même");
    }
}
