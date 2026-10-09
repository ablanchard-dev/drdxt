using System.Security.Cryptography;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

/// <summary>PAWN1 (09/10/2026) : la température lue dans les registres Intel par PawnIO, quand PawnIO est déjà installé.
/// Le décodage est pur et testé ici ; le module embarqué est figé par son empreinte ; la lecture réelle est un test
/// Machine (il faut PawnIO sur la machine qui lance la suite).</summary>
public sealed class TemperaturePawnIoTests
{
    [Fact]
    public void TjMax_moins_l_ecart_donne_la_temperature_avec_sa_source()
    {
        // 0x1A2 : TjMax = 100 (bits 23:16) ; 0x1B1 : écart = 37 (bits 22:16) ⇒ 63 °C.
        var lu = TemperaturePawnIo.Decoder(100UL << 16, 37UL << 16);

        lu.Should().NotBeNull();
        lu!.Celsius.Should().Be(63);
        lu.Libelle.Should().Be(TemperaturePawnIo.Libelle);
    }

    [Theory]
    [InlineData(0UL, 0UL)]                 // TjMax 0 : registre vide
    [InlineData(200UL << 16, 10UL << 16)]  // TjMax 200 : hors de ce qu'un processeur Intel publie
    [InlineData(90UL << 16, 95UL << 16)]   // écart au-dessus de TjMax : impossible
    public void Un_registre_implausible_ne_donne_rien(ulong target, ulong status)
        => TemperaturePawnIo.Decoder(target, status).Should().BeNull("non lu vaut mieux qu'un chiffre faux");

    [Fact]
    public void Le_module_embarque_est_celui_relu_et_fige()
    {
        using var flux = typeof(TemperaturePawnIo).Assembly.GetManifestResourceStream("DrDxt.Detection.Cpu.PawnIO.IntelMSR.bin");
        flux.Should().NotBeNull("le module IntelMSR.bin de namazso/PawnIO.Modules est embarqué");
        using var ms = new MemoryStream();
        flux!.CopyTo(ms);
        Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())).Should().Be(TemperaturePawnIo.Empreinte,
            "un module changé se relit (ses fonctions, ses registres autorisés) avant d'être figé ici");
    }

    [Fact]
    public void Sans_PawnIO_rien_n_est_lu_et_la_source_composite_ne_plante_pas()
    {
        // Avec ou sans PawnIO, la source composite répond (null compris) sans exception.
        var act = () => TemperatureProcesseur.Lire();
        act.Should().NotThrow();
    }

    [Fact]
    public void Les_limites_de_puissance_se_decodent_avec_l_unite_et_le_verrou()
    {
        // 0x606 : unité 1/8 W (bits 3:0 = 3) ; 0x610 : PL1 = 520 (65 W), PL2 = 1752 (219 W), verrou bit 63 ; 0x614 : base 520 (65 W).
        var lu = TemperaturePawnIo.DecoderLimites(0x3, (1UL << 63) | (1752UL << 32) | 520UL, 520UL);

        lu.Should().NotBeNull();
        lu!.Pl1W.Should().Be(65); lu.Pl2W.Should().Be(219); lu.NominaleW.Should().Be(65); lu.Verrou.Should().BeTrue();
        lu.Pl1Active.Should().BeFalse("bit 15 à zéro dans ce registre d'essai");
        TemperaturePawnIo.DecoderLimites(0x3, 0UL, 520UL).Should().BeNull("PL1 nul = registre vide");
        // Revue du 09/10 : le registre au maximum (0x7FFF) n'est pas une panne, c'est « sans limite » ; le bit 15 dit « activée ».
        var max = TemperaturePawnIo.DecoderLimites(0x3, (1UL << 15) | 0x7FFFUL, 520UL);
        max.Should().NotBeNull();
        max!.Pl1W.Should().Be(4095.875); max.Pl1Active.Should().BeTrue(); max.SansLimite.Should().BeTrue();
        // Contre-revue : « sans limite » vient du champ brut, pas des watts : avec une unité d'un seizième, 0x7FFF vaut 2 047 W et reste « sans limite ».
        TemperaturePawnIo.DecoderLimites(0x4, 0x7FFFUL, 520UL)!.SansLimite.Should().BeTrue();
        TemperaturePawnIo.DecoderLimites(0x3, 280UL, 520UL)!.SansLimite.Should().BeFalse("35 W avec le bit 15 à zéro : une limite, pas « sans limite »");
    }

    [Fact]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_PawnIO_donne_des_limites_plausibles()
    {
        if (!TemperaturePawnIo.Installe() || !TemperaturePawnIo.ProcesseurIntel()) return;

        var lu = TemperaturePawnIo.LireLimites();

        lu.Should().NotBeNull();
        lu!.Pl1W.Should().BeInRange(10, 1000); lu.Pl2W.Should().BeGreaterThanOrEqualTo(lu.Pl1W); lu.NominaleW.Should().BeInRange(10, 500);
    }

    /// <summary>La preuve sur la machine : PawnIO installé ici (09/10/2026), un processeur Intel ⇒ une vraie température.</summary>
    [Fact]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_PawnIO_donne_une_temperature_plausible()
    {
        if (!TemperaturePawnIo.Installe() || !TemperaturePawnIo.ProcesseurIntel()) return;

        var lu = TemperaturePawnIo.Lire();

        lu.Should().NotBeNull("PawnIO est installé et le processeur est Intel : les registres se lisent");
        lu!.Celsius.Should().BeInRange(15, 105);
        lu.Libelle.Should().Be(TemperaturePawnIo.Libelle);
    }
}
