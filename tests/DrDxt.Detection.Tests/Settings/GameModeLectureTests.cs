using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

/// <summary>
/// Le Game Mode de Windows a deux noms dans le registre, et le produit n'en lisait qu'un.
///
/// Sur la machine de reference, `HKCU\Software\Microsoft\GameBar` ne porte que
/// `GamepadDoublePressIntervalMs`. Ni `AllowAutoGameMode` ni `AutoGameModeEnabled` : le reglage n'a jamais ete touche
/// ici, et le mode Jeu est dans l'etat par defaut de Windows, actif.
///
/// Mais l'asymetrie, elle, est un defaut. `GameModeOnTweak` ecrit les deux valeurs, et son
/// propre commentaire designe `AllowAutoGameMode` comme « the one detection reads ». Sur
/// Windows 11, l'interrupteur des Parametres ecrit `AutoGameModeEnabled` ; `AllowAutoGameMode`
/// est l'ancien nom. Un joueur qui active le Game Mode lui-meme pose donc le nom moderne — et
/// la detection, qui ne lisait que l'ancien, rendait `Unknown`. Le produit ne voyait pas un
/// reglage deja mis, et proposait un levier pour quelque chose de deja fait.
///
/// Lire les deux est juste quelle que soit la reponse a « lequel fait foi » : si l'une des deux
/// valeurs est presente, on sait. C'est pour ca que cette correction ne depend pas de la
/// qualite de la source.
/// </summary>
public class GameModeLectureTests
{
    private static TriState Lire(object? moderne, object? ancien)
        => GameModeLecture.Interpreter(moderne, ancien);

    [Fact]
    public void Le_nom_MODERNE_seul_est_lu_c_est_le_defaut_du_18_09()
        // Le cas reel : l'utilisateur bascule l'interrupteur de Windows 11, qui n'ecrit que lui.
        => Lire(moderne: 1, ancien: null).Should().Be(TriState.On);

    [Fact]
    public void Le_nom_MODERNE_a_zero_rend_Off_pas_Unknown()
        => Lire(moderne: 0, ancien: null).Should().Be(TriState.Off);

    [Fact]
    public void L_ancien_nom_seul_reste_lu_les_machines_deja_reglees_ne_regressent_pas()
        => Lire(moderne: null, ancien: 1).Should().Be(TriState.On);

    [Fact]
    public void L_ancien_nom_a_zero_rend_Off()
        => Lire(moderne: null, ancien: 0).Should().Be(TriState.Off);

    [Fact]
    public void Quand_les_deux_se_contredisent_le_nom_MODERNE_tranche()
        // C'est tout l'interet de la correction : celui que Windows 11 honore l'emporte.
        => Lire(moderne: 0, ancien: 1).Should().Be(TriState.Off);

    // Aucune des deux valeurs (ni la cle) : le mode Jeu n'a jamais ete regle, il est donc dans l'etat par defaut de
    // Windows, actif (Microsoft l'active par defaut pour tout le systeme). « Non lu » disait une panne pour un mode Jeu
    // actif.
    [Fact]
    public void Aucune_des_deux_valeurs_est_le_defaut_de_Windows_actif()
        => Lire(moderne: null, ancien: null).Should().Be(TriState.On);

    [Theory]
    [InlineData("1")]
    [InlineData(1L)]
    [InlineData(true)]
    public void Une_valeur_qui_n_est_pas_un_entier_rend_Unknown(object valeur)
        // Une valeur d'un type inattendu est un « je n'ai pas lu », jamais un « c'est eteint ».
        => Lire(moderne: valeur, ancien: null).Should().Be(TriState.Unknown);
}
