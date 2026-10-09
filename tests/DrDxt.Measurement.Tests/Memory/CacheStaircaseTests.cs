using DrDxt.Measurement.Memory;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Memory;

public class CacheStaircaseTests
{
    private const long Ko = 1024;
    private const long Mo = 1024 * 1024;

    // Une hierarchie fabriquee a la main, aux dimensions d'un i7-14700F : L1 48 Ko, L2 2 Mo,
    // L3 33 Mo. La courbe est plate a l'interieur d'un niveau et saute au passage suivant --
    // c'est cette forme, et pas les valeurs, que la detection doit retrouver.
    private static StaircasePoint[] Hierarchie() =>
    [
        new(32 * Ko, 1.1),
        new(48 * Ko, 1.2),
        new(256 * Ko, 4.0),
        new(2 * Mo, 4.2),
        new(16 * Mo, 14.0),
        new(32 * Mo, 14.5),
        new(128 * Mo, 66.0),
        new(256 * Mo, 67.0),
    ];

    [Fact]
    public void Un_saut_designe_la_derniere_fenetre_avant_le_bond_de_latence()
    {
        // Trois sauts : 48 Ko -> 256 Ko, 2 Mo -> 16 Mo, 32 Mo -> 128 Mo. Le saut designe la
        // Derniere fenetre encore rapide, parce que c'est elle qui minore la taille du niveau.
        CacheStaircase.FindJumps(Hierarchie())
            .Should().Equal(48 * Ko, 2 * Mo, 32 * Mo);
    }

    [Fact]
    public void Une_courbe_plate_n_a_aucun_saut_et_la_detection_n_en_invente_pas()
    {
        StaircasePoint[] plate =
        [
            new(32 * Ko, 60.0),
            new(2 * Mo, 61.0),
            new(128 * Mo, 62.0),
        ];

        CacheStaircase.FindJumps(plate).Should().BeEmpty();
    }

    [Fact]
    public void La_vraie_courbe_du_06_09_rend_CINQ_sauts_pour_TROIS_frontieres_de_cache()
    {
        // Les latences reellement relevees par tools/memprobe le 06/09/2026 (i7-14700F, L1 48 Ko,
        // L2 2 Mo, L3 33 Mo). Ce test ne decrit pas un succes : il grave la LIMITE de la methode,
        // pour qu'aucune session future ne construise « ton L2 fait tant » par-dessus.
        // 2,75 -> 4,28 (x1,56) et 23,26 -> 36,39 (x1,56) sont de la croissance A L'INTERIEUR d'un
        // niveau. Et remonter le seuil ne sauve rien : le vrai passage L3 -> DRAM ne vaut que
        // x1,69.
        StaircasePoint[] mesuree =
        [
            new(32 * Ko, 0.96),
            new(48 * Ko, 0.95),
            new(256 * Ko, 2.75),
            new(2 * Mo, 4.28),
            new(16 * Mo, 23.26),
            new(32 * Mo, 36.39),
            new(128 * Mo, 61.45),
        ];

        CacheStaircase.FindJumps(mesuree).Should().HaveCount(5);
    }
}
