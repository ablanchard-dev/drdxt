using DrDxt.Application.Parties;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Parties;

// Le test de refroidissement, prouvé sans charger la machine (charge, lecture et attente injectées).
public sealed class TestDeRefroidissementTests
{
    private sealed class Charge : IDisposable { public bool Arretee; public void Dispose() => Arretee = true; }

    private static (TestDeRefroidissement Test, Charge Charge) Construire(IEnumerable<int?> chaud)
    {
        // La première lecture sert au départ (sans elle, la charge ne démarre pas).
        var suite = new Queue<int?>(new int?[] { 80 }.Concat(chaud));
        var charge = new Charge();
        var test = new TestDeRefroidissement(_ => charge,
            () => suite.Count > 0 && suite.Dequeue() is { } c ? new EtatCpu(c, c - 15, c >= 100, false, false, false) : null,
            (_, _) => Task.CompletedTask);
        return (test, charge);
    }

    [Fact]
    public async Task Dix_secondes_a_99_degres_arretent_le_test_et_la_charge()
    {
        // Relevé sur la machine de référence : 100 °C dès 35 s.
        var (test, charge) = Construire(Enumerable.Repeat<int?>(85, 34).Concat(Enumerable.Repeat<int?>(100, 30)));

        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);

        r.ArretSecurite.Should().BeTrue();
        r.SecondesAvantSeuil.Should().Be(35);
        r.Secondes.Should().Be(44);
        charge.Arretee.Should().BeTrue("la charge s'arrête avec le test");
        TestDeRefroidissement.Juger(r, new LimiteDePuissanceLue(160, 160, 65, false)).Explication
            .Should().Contain("au bout de 35 s").And.Contain("PL1 160 W").And.Contain("plus dure qu'un jeu");
    }

    [Fact]
    public async Task Un_pic_bref_ne_l_arrete_pas_et_un_refroidissement_qui_tient_se_dit()
    {
        var (test, _) = Construire(Enumerable.Repeat<int?>(80, 50).Concat(Enumerable.Repeat<int?>(99, 5)).Concat(Enumerable.Repeat<int?>(80, 65)));
        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);

        r.ArretSecurite.Should().BeFalse();
        r.Secondes.Should().Be(120);
        TestDeRefroidissement.Juger(r, null).Statut.Should().Be(RepairStatus.Observed);
    }

    // Sans lecture, pas de sécurité ; la charge ne démarre pas, ou s'arrête au bout de 3 s sans lecture.
    [Fact]
    public async Task Sans_premiere_lecture_la_charge_ne_demarre_pas()
    {
        var demarree = false;
        var test = new TestDeRefroidissement(_ => { demarree = true; return new Charge(); }, () => null, (_, _) => Task.CompletedTask);
        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
        demarree.Should().BeFalse();
        TestDeRefroidissement.Juger(r, null).Statut.Should().Be(RepairStatus.Unknown);
    }

    [Fact]
    public async Task Cinq_secondes_sans_lecture_arretent_la_charge()
    {
        var (test, charge) = Construire(new int?[] { 80, 80, null, null, null, null, null }.Concat(Enumerable.Repeat<int?>(80, 100)));
        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
        r.LectureePerdue.Should().BeTrue();
        r.Secondes.Should().Be(7, "Le temps de charge, deux secondes lues puis cinq sans lecture");
        r.Freinage!.Secondes.Should().Be(2);
        charge.Arretee.Should().BeTrue();
        TestDeRefroidissement.Juger(r, null).Explication.Should().Contain("pas de sécurité");
    }

    // Un raté isolé du tube (le client se met 2 s en pause) fait 3 secondes sans lecture ; ce n'est
    // pas une perte de lecture, le test continue.
    [Fact]
    public async Task Un_rate_isole_du_service_n_arrete_pas_le_test()
    {
        var (test, _) = Construire(new int?[] { 80, 80, null, null, null }.Concat(Enumerable.Repeat<int?>(80, 115)));
        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
        r.LectureePerdue.Should().BeFalse();
        r.Secondes.Should().Be(120, "120 s de charge, dont 117 lues");
        r.Freinage!.Secondes.Should().Be(117);
    }

    [Fact]
    public void La_charge_pleine_s_arrete_quand_on_la_libere()
    {
        var charge = (TestDeRefroidissement.Charge)TestDeRefroidissement.ChargePleine(CancellationToken.None);
        charge.Tourne.Should().BeTrue();
        charge.Dispose();
        charge.Tourne.Should().BeFalse("libérer annule : les fils sortent de leur boucle");
    }

    // Compter les secondes lues fausserait « arrêté à N s » : avec un raté du service (3 secondes nulles), le
    // verdict dirait « arrêté à 41 s » et « 99 °C au bout de 35 s » sur une ligne de temps fausse. Le temps rendu est
    // celui de la charge.
    [Fact]
    public async Task Les_secondes_rendues_sont_le_temps_de_charge_meme_avec_des_secondes_non_lues()
    {
        var (test, _) = Construire(new int?[] { null, null, null }.Concat(Enumerable.Repeat<int?>(85, 31)).Concat(Enumerable.Repeat<int?>(100, 30)));

        var r = await test.LancerAsync(TimeSpan.FromMinutes(2), CancellationToken.None);

        r.ArretSecurite.Should().BeTrue();
        r.SecondesAvantSeuil.Should().Be(35);
        r.Secondes.Should().Be(44, "34 secondes avant le seuil, puis 10 au seuil : la charge a duré 44 s, dont 41 lues");
        r.Freinage!.Secondes.Should().Be(41);
    }

    // B*x - x*b vaut exactement 0, x grandissait de 1,0001 à chaque tour, débordait en +Inf puis en NaN
    // au bout de quelques millisecondes : la charge tournait sur des NaN. Le pas doit rester fini.
    [Fact]
    public void Le_pas_de_la_charge_reste_fini()
    {
        var x = System.Numerics.Vector<float>.One;
        for (var i = 0; i < 2_000_000; i++) x = TestDeRefroidissement.Charge.Pas(x);
        for (var k = 0; k < System.Numerics.Vector<float>.Count; k++) float.IsFinite(x[k]).Should().BeTrue();
    }
}
