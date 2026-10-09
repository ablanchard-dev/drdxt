using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

// ─────────────────────────────────────────────────────────────────────────────
// Le pont entre « l'ecran que Windows pilote » et « ses octets d'EDID ».
//
// Mesure du 18/09/2026 sur la machine de reference : quatre entrees du registre portent un EDID
// pour deux ecrans reellement branches — les autres sont des branchements memorises. Prendre
// « le premier trouve » aurait lu un ecran debranche et rendu une capacite VRR qui ne concerne
// personne. Le pont doit donc partir de l'ecran actif, pas du registre.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class EdidDuMoniteurTests
{
    private static string? Chemin(string? nom) =>
        typeof(DrDxt.Detection.Display.Win32DisplayInfo).Assembly
            .GetType("DrDxt.Detection.Display.EdidDuMoniteur")!
            .GetMethod("CheminRegistre", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, [nom]) as string;

    [Fact]
    public void Le_nom_d_interface_reel_donne_le_chemin_de_registre_reel()
    {
        // Le nom exact rendu par EnumDisplayDevices pour l'ecran de jeu de cette machine, et la
        // cle ou son EDID a ete lu pour de vrai.
        Chemin(@"\\?\DISPLAY#AUS2413#5&3ac6cfcf&0&UID4354#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}")
            .Should().Be(@"SYSTEM\CurrentControlSet\Enum\DISPLAY\AUS2413\5&3ac6cfcf&0&UID4354\Device Parameters");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\?\USB#VID_046D#abc#{guid}")]          // pas un ecran
    [InlineData(@"\\?\DISPLAY#AUS2413")]                   // tronque
    [InlineData(@"\\?\DISPLAY##5&3ac#{guid}")]             // pnp vide
    [InlineData(@"\\?\DISPLAY#AUS2413##{guid}")]           // instance vide
    public void Un_nom_qui_n_a_pas_la_forme_attendue_ne_donne_AUCUN_chemin(string? nom)
    {
        Chemin(nom).Should().BeNull();
    }

    [Fact]
    public void Un_champ_qui_contient_un_separateur_de_cle_est_refuse()
    {
        // Sans cette garde, un nom malforme ferait sortir du sous-arbre vise et lire une cle
        // qui n'a rien a voir. Le nom vient de Windows, pas d'un utilisateur — mais une lecture
        // qui depend de la bonne foi de son entree n'est pas une lecture sure.
        Chemin(@"\\?\DISPLAY#AUS2413\..\..\Services#5&3ac#{guid}").Should().BeNull();
    }
}
