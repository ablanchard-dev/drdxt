using DrDxt.Measurement.Cpu;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.Cpu;

/// <summary>
/// En jeu, sur la machine de reference, le paquet tenait 66,5 W plats (63,2-69,5) et
/// les cœurs P restaient a 4,0 GHz : la limite longue du BIOS, que les indicateurs de Windows ne
/// voient pas. Le banc soutenu releve maintenant la puissance, et le juge le dit quand elle est plate.
/// </summary>
public class LimiteDePuissanceSoutenueTests
{
    private static CpuBaseline Baisse(double? watts, double? ecart)
        => new("m", DateTimeOffset.UtcNow, 8, RafaleMops: 100, SoutenuMops: 70, BruitPercent: 0.5,
            PuissanceSoutenueW: watts, PuissanceEcartPercent: ecart);

    [Fact]
    public void Une_baisse_a_puissance_plate_est_dite_limite_de_puissance()
        => CpuBaselineService.Juger(Baisse(65.8, 9.5)).Lisible
            .Should().Contain("66 W").And.Contain("limite de puissance longue");

    // Sur un Windows anglais, le verdict du banc se dit en anglais (point décimal, date mois/jour).
    [Fact]
    public void La_baisse_se_dit_en_anglais()
    {
        using var _ = DrDxt.Core.Docteur.Portee(System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var b = Baisse(65.8, 9.5) with { TakenUtc = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero) };

        CpuBaselineService.Juger(b).Lisible.Should().StartWith("Under a long load on 8 cores, your processor holds 30.0% less power")
            .And.Contain("an average of 66 W").And.EndWith("Measured on 10/03/2026.");
    }

    [Fact]
    public void Le_verdict_porte_le_drapeau_de_limite_de_puissance()
    {
        CpuBaselineService.Juger(Baisse(65.8, 9.5)).LimiteDePuissance.Should().BeTrue();
        CpuBaselineService.Juger(Baisse(65.8, 25)).LimiteDePuissance.Should().BeFalse();
        CpuBaselineService.Juger(Baisse(null, null)).LimiteDePuissance.Should().BeFalse();
    }

    // Une mutation qui levait le drapeau sans baisse démontrée restait verte. Une puissance plate
    // sur un processeur qui ne perd rien n'est pas une limite : l'écran désignerait le BIOS pour rien.
    [Fact]
    public void Une_puissance_plate_sans_baisse_ne_designe_pas_le_BIOS()
    {
        var sansBaisse = new CpuBaseline("m", DateTimeOffset.UtcNow, 8, RafaleMops: 100, SoutenuMops: 100,
            BruitPercent: 0.5, PuissanceSoutenueW: 65.8, PuissanceEcartPercent: 9.5);
        var v = CpuBaselineService.Juger(sansBaisse);
        v.Demontrable.Should().BeFalse();
        v.LimiteDePuissance.Should().BeFalse();
    }

    [Fact]
    public void Une_puissance_qui_varie_ne_permet_pas_de_conclure()
        => CpuBaselineService.Juger(Baisse(65.8, 25)).Lisible.Should().NotContain("limite de puissance");

    [Fact]
    public void Une_puissance_qui_varie_est_dite_avec_son_chiffre_et_sans_conclusion()
    {
        // Mesure fraîche sur la machine de reference : 63 W à 12,8 % près ; la phrase dit la puissance.
        var lisible = CpuBaselineService.Juger(Baisse(63.2, 12.8)).Lisible;
        lisible.Should().Contain("63 W en moyenne").And.Contain("±13 %").And.Contain("trop incertain");
        CpuBaselineService.Juger(Baisse(65.8, 9.5)).Lisible.Should().NotContain("trop incertain");
    }

    [Fact]
    public void Une_mesure_trop_bruitee_ne_dit_pas_que_le_processeur_garde_sa_puissance()
    {
        // Remesure réelle du 25/09/2026 à 18:28 : précision ±14,8 %, donc seuil de 44,5 %.
        var bruitee = CpuBaselineService.Juger(new CpuBaseline("m", DateTimeOffset.UtcNow, 8, 100, 95, 14.8)).Lisible;
        bruitee.Should().Contain("trop varié").And.NotContain("garde sa puissance quand");

        // Témoin : une mesure précise sans baisse le dit toujours.
        CpuBaselineService.Juger(new CpuBaseline("m", DateTimeOffset.UtcNow, 8, 100, 99.9, 0.5)).Lisible
            .Should().Contain("garde sa puissance quand")
            // Sous un autre programme lourd, ce verdict sortait à tort : sa condition est dite.
            .And.Contain("si aucun autre programme lourd ne tournait pendant la mesure");
    }

    [Fact]
    public void Sans_releve_de_puissance_la_phrase_reste_celle_d_avant()
        => CpuBaselineService.Juger(Baisse(null, null)).Lisible.Should().NotContain("limite de puissance");

    [Fact]
    public void Sans_baisse_demontree_la_puissance_ne_fait_rien_dire()
        => CpuBaselineService.Juger(new CpuBaseline("m", DateTimeOffset.UtcNow, 8, 100, 99.9, 0.5, 65, 2))
            .Lisible.Should().NotContain("limite de puissance");

    [Fact]
    public void Une_reference_relue_sans_debit_soutenu_ne_fabrique_pas_cent_pour_cent_de_perte()
    {
        // La reference se relit depuis %LOCALAPPDATA%, que tout programme de l'utilisateur
        // peut modifier. Un debit soutenu a 0 y rendait « 100 % de puissance en moins », demontrable.
        var j = CpuBaselineService.Juger(new CpuBaseline("m", DateTimeOffset.UtcNow, 8, 100, 0, 0.5));
        j.Demontrable.Should().BeFalse();
        j.Lisible.Should().NotContain("100");
    }

    // Un fichier sans « BruitPercent » se relit avec 0. Le seuil (3 fois le bruit) tombait
    // a 0 et 2 % d'ecart devenaient une baisse « demontrable », BIOS designe compris.
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Une_reference_relue_sans_precision_ne_demontre_rien(double bruit)
    {
        var j = CpuBaselineService.Juger(new CpuBaseline("m", DateTimeOffset.UtcNow, 8, 100, 98, bruit, 65, 2));
        j.Demontrable.Should().BeFalse();
        j.LimiteDePuissance.Should().BeFalse();
        j.Lisible.Should().Contain("précision");
    }

    [Fact]
    public void Le_resume_des_releves_rend_moyenne_et_ecart_type_relatif()
    {
        var r = RealCpuSustainedBench.Resumer([63.2, 66.5, 69.5]);
        r.Should().NotBeNull();
        r!.Value.MoyenneW.Should().BeApproximately(66.4, 0.01);
        r.Value.EcartPercent.Should().BeApproximately(Math.Sqrt(19.86 / 3) / Math.Sqrt(3) / 66.4 * 100, 0.01);
    }

    [Fact]
    public void Les_releves_reels_du_banc_sont_dits_plats()
    {
        // Les 13 releves du banc reel, 25/09/2026, machine de reference.
        var r = RealCpuSustainedBench.Resumer([65.6, 65.3, 71.3, 58.4, 60.8, 63.0, 60.6, 62.8, 61.5, 64.2, 73.7, 56.3, 61.2]);
        r!.Value.EcartPercent.Should().BeLessThan(CpuBaselineService.EcartPlatPercent);
    }

    [Fact]
    public void Les_quatre_mesures_de_la_machine_de_reference_donnent_le_meme_verdict()
    {
        // Même machine, même état ; juger sur l'écart-type des relevés dirait « tenue » 2 fois sur 4.
        // Sur 17 relevés, ces écarts-types donnent une incertitude de la moyenne sous le seuil, les quatre fois.
        foreach (var ecartType in new[] { 7.0, 12.8, 10.4, 7.9 })
        {
            var releves = Enumerable.Range(0, 17).Select(i => 64.0 * (1 + ecartType / 100 * (i % 2 == 0 ? 1 : -1))).ToList();
            RealCpuSustainedBench.Resumer(releves)!.Value.EcartPercent.Should().BeLessThan(CpuBaselineService.EcartPlatPercent);
        }
    }

    [Fact]
    public void Moins_de_trois_releves_ne_font_pas_un_resume()
        => RealCpuSustainedBench.Resumer([65, 66]).Should().BeNull();

    [Fact]
    public void Une_ancienne_reference_sans_puissance_se_relit()
    {
        var json = """{"MachineId":"m","TakenUtc":"2026-09-11T19:46:21+00:00","Threads":8,"RafaleMops":100,"SoutenuMops":70,"BruitPercent":0.5}""";
        var lu = System.Text.Json.JsonSerializer.Deserialize<CpuBaseline>(json);
        lu!.PuissanceSoutenueW.Should().BeNull();
        lu.SoutenuMops.Should().Be(70);
    }

    [Fact]
    public void Une_reference_sans_puissance_invite_a_remesurer()
    {
        // Une reference ancienne sans puissance, quand une mesure fraiche dit « limite du BIOS » :
        // la phrase le dit, sans rien remesurer a la place de l'utilisateur.
        CpuBaselineService.Juger(Baisse(null, null)).Lisible.Should().Contain("Remesurer ton processeur");
        CpuBaselineService.Juger(Baisse(65.8, 9.5)).Lisible.Should().NotContain("Remesurer ton processeur");
    }

    [Theory]
    [InlineData(70)]    // baisse demontree
    [InlineData(99.9)]  // pas de baisse
    public void La_phrase_porte_la_date_de_la_mesure(double soutenu)
    {
        // Une reference ancienne presentee sans date passerait pour d'aujourd'hui.
        var onzeSeptembre = new DateTimeOffset(2026, 9, 11, 17, 46, 0, TimeSpan.Zero);
        CpuBaselineService.Juger(new CpuBaseline("m", onzeSeptembre, 8, 100, soutenu, 0.5)).Lisible
            .Should().EndWith($"Mesuré le {onzeSeptembre.ToLocalTime():dd/MM/yyyy}.");
    }
}
