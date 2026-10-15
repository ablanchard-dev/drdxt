using System.Security.Cryptography;
using DrDxt.Core;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

/// <summary>La température lue dans les registres Intel par PawnIO, quand PawnIO est déjà installé.
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
        using var flux = typeof(DrDxt.GpuNative.PawnIo.PawnIoIntel).Assembly.GetManifestResourceStream("DrDxt.GpuNative.PawnIo.IntelMSR.bin");
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

    // Composer une nouvelle PL1 ne touche à rien d'autre dans le registre.
    [Fact]
    public void Composer_PL1_ne_change_que_le_champ_PL1_et_l_active()
    {
        var origine = (1UL << 63) | (1UL << 47) | (1752UL << 32) | (0x1EUL << 17) | (1UL << 16) | 520UL; // verrou, PL2 219 W, fenêtre, bride, PL1 65 W
        var compose = RegistreDeLimiteCpu.ComposerPl1(origine, 219, 0x3);
        (compose & 0x7FFF).Should().Be(1752, "PL1 = 219 W en huitièmes de watt");
        ((compose >> 15) & 1).Should().Be(1, "PL1 activée");
        (compose & ~0xFFFFUL).Should().Be(origine & ~0xFFFFUL, "PL2, fenêtres, bride et verrou intouchés");
        (RegistreDeLimiteCpu.ComposerPl1(origine, 100000, 0x3) & 0x7FFF).Should().Be(0x7FFF, "borné au maximum du champ");
        (RegistreDeLimiteCpu.ComposerPl1(origine, 0, 0x3) & 0x7FFF).Should().Be(1, "jamais zéro");
        RegistreDeLimiteCpu.Decoder(0x3, origine, (1752UL << 32) | 520UL)!.MaximaleW.Should().Be(219);
        // La forme admise par le helper, sur un registre sans verrou (un verrou se refuse de toute façon).
        var sansVerrou = origine & ~(1UL << 63); var composeSansVerrou = compose & ~(1UL << 63);
        RegistreDeLimiteCpu.NeToucheQuePl1(composeSansVerrou, sansVerrou).Should().BeTrue();
        RegistreDeLimiteCpu.NeToucheQuePl1(sansVerrou, composeSansVerrou).Should().BeTrue("le retour à l'origine est admissible aussi");
        RegistreDeLimiteCpu.NeToucheQuePl1(compose, origine).Should().BeFalse("jamais de verrou posé");
        RegistreDeLimiteCpu.NeToucheQuePl1(composeSansVerrou ^ (1UL << 40), sansVerrou).Should().BeFalse("PL2 intouchée");
        RegistreDeLimiteCpu.NeToucheQuePl1((composeSansVerrou & ~0x7FFFUL) | 1753, sansVerrou).Should().BeFalse("PL1 jamais au-dessus de PL2");
        TemperaturePawnIo.CopieMmioPl1W(0x10000UL, 0x3).Should().Be(0, "copie présente mais inactive (machine de référence)");
        TemperaturePawnIo.CopieMmioPl1W((1UL << 15) | 520UL, 0x3).Should().Be(65, "copie active : 65 W");
        TemperaturePawnIo.CopieMmioPl1W(null, 0x3).Should().BeNull();
    }

    [Fact]
    public void Le_module_MCHBAR_embarque_est_celui_relu_et_fige()
    {
        using var flux = typeof(DrDxt.GpuNative.PawnIo.PawnIoIntel).Assembly.GetManifestResourceStream("DrDxt.GpuNative.PawnIo.IntelMCHBAR.bin");
        flux.Should().NotBeNull();
        using var ms = new MemoryStream();
        flux!.CopyTo(ms);
        Convert.ToHexStringLower(SHA256.HashData(ms.ToArray())).Should().Be(TemperaturePawnIo.EmpreinteMchbar);
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
        // Le registre au maximum (0x7FFF) n'est pas une panne, c'est « sans limite » ; le bit 15 dit « activée ».
        var max = TemperaturePawnIo.DecoderLimites(0x3, (1UL << 15) | 0x7FFFUL, 520UL);
        max.Should().NotBeNull();
        max!.Pl1W.Should().Be(4095.875); max.Pl1Active.Should().BeTrue(); max.SansLimite.Should().BeTrue();
        // « sans limite » vient du champ brut, pas des watts : avec une unité d'un seizième, 0x7FFF vaut 2 047 W et reste « sans limite ».
        TemperaturePawnIo.DecoderLimites(0x4, 0x7FFFUL, 520UL)!.SansLimite.Should().BeTrue();
        TemperaturePawnIo.DecoderLimites(0x3, 280UL, 520UL)!.SansLimite.Should().BeFalse("35 W avec le bit 15 à zéro : une limite, pas « sans limite »");
    }


    // La seconde du processeur, lue sur la vraie machine.
    // La seconde du processeur porte le ventilateur CPU (en-tête 0, CPU_FAN sur les cartes lues), en admin comme
    // par le service ; sans ventilateurs lus, la seconde reste entière, sans ventilateur.
    [Fact]
    public void La_seconde_du_processeur_porte_le_ventilateur_CPU_quand_il_est_lu()
    {
        var e = new EtatCpu(98, 77, true, false, false, false);
        TemperaturePawnIo.AvecVentilo(e, [new VentilateurLu(5, 1210, 21), new VentilateurLu(0, 1265, 55)])!.VentiloCpu
            .Should().Be(new VentilateurLu(0, 1265, 55));
        TemperaturePawnIo.AvecVentilo(e, null).Should().Be(e);
        TemperaturePawnIo.AvecVentilo(null, [new VentilateurLu(0, 1265, 55)]).Should().BeNull("pas d'état : pas de seconde");
        // Des ventilateurs HWiNFO (sans cycle) : gardés pour les arrêts, jamais pris pour celui du processeur.
        var hw = TemperaturePawnIo.AvecVentilo(e, [new VentilateurLu(100, 1180, null, "CPU")])!;
        hw.VentiloCpu.Should().BeNull();
        hw.Ventilateurs.Should().ContainSingle();
    }

    [FactSiPawnIoLisible]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_l_etat_du_processeur_se_lit_avec_le_coeur_0()
    {
        var e = TemperaturePawnIo.LireEtat();
        e.Should().NotBeNull();
        e!.ChaudC.Should().BeInRange(15, 105);
        e.Coeur0C.Should().NotBeNull().And.BeLessThanOrEqualTo(e.ChaudC + 1);
    }

    [FactSiPawnIoLisible]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_PawnIO_donne_des_limites_plausibles()
    {

        var lu = TemperaturePawnIo.LireLimites();

        lu.Should().NotBeNull();
        lu!.Pl1W.Should().BeInRange(10, 1000); lu.Pl2W.Should().BeGreaterThanOrEqualTo(lu.Pl1W); lu.NominaleW.Should().BeInRange(10, 500);
    }

    // La copie MMIO de la limite, lue sur la vraie machine, et comparée au registre MSR (lecture seule).
    [FactSiPawnIoLisible]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_la_copie_MMIO_de_la_limite_se_lit_et_se_compare_au_registre()
    {
        var lu = TemperaturePawnIo.LireLimites();
        Console.WriteLine("MCHBAR : " + DrDxt.GpuNative.PawnIo.PawnIoIntel.DiagnostiquerMchbar());
        Console.WriteLine($"MSR : {lu?.Pl1W} W / {lu?.Pl2W} W, verrou {lu?.Verrou} ; copie MMIO PL1 : {lu?.CopieMmioPl1W} W ; max 0x614 : {lu?.MaximaleW} W");
        lu.Should().NotBeNull();
        lu!.CopieMmioPl1W.Should().NotBeNull("la fenêtre MCHBAR se lit sur cette machine");
    }

    /// <summary>La preuve sur la machine : PawnIO installé ici, un processeur Intel ⇒ une vraie température.</summary>
    [FactSiPawnIoLisible]
    [Trait("Category", "Machine")]
    public void Sur_cette_machine_PawnIO_donne_une_temperature_plausible()
    {

        var lu = TemperaturePawnIo.Lire();

        lu.Should().NotBeNull("PawnIO est installé et le processeur est Intel : les registres se lisent");
        lu!.Celsius.Should().BeInRange(15, 105);
        lu.Libelle.Should().Be(TemperaturePawnIo.Libelle);
    }
}

/// <summary>Un test de la vraie machine qui ne peut rien lire ici (pas de PawnIO, pas Intel,
/// pas administrateur) se dit IGNORÉ avec sa raison, au lieu de passer au vert sans rien avoir vérifié. xUnit 2 : la
/// condition se lit à la découverte, sans paquet de plus.</summary>
public sealed class FactSiPawnIoLisibleAttribute : FactAttribute
{
    public FactSiPawnIoLisibleAttribute()
    {
        var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        if (!TemperaturePawnIo.Installe()) Skip = "PawnIO n'est pas installé sur cette machine";
        else if (!TemperaturePawnIo.ProcesseurIntel()) Skip = "processeur non Intel : les registres lus sont ceux d'Intel";
        else if (!admin) Skip = "session sans droits administrateur : PawnIO ne répond qu'à un processus élevé";
    }
}

/// <summary>La seconde du processeur lue sur la vraie machine porte le ventilateur CPU (carte MSI à puce NCT6687).</summary>
public sealed class SecondeAvecVentiloSurLaVraieCarteTests
{
    [FactSiCarteMsiEtPawnIo]
    [Trait("Category", "Machine")]
    public void La_seconde_lue_porte_un_ventilateur_CPU_qui_tourne()
    {
        var e = TemperaturePawnIo.LireEtat();
        e.Should().NotBeNull();
        e!.VentiloCpu.Should().NotBeNull();
        e.VentiloCpu!.TrMin.Should().BeGreaterThan(200);
    }
}

public sealed class FactSiCarteMsiEtPawnIoAttribute : FactAttribute
{
    public FactSiCarteMsiEtPawnIoAttribute()
    {
        Skip = new FactSiPawnIoLisibleAttribute().Skip;
        if (Skip is not null) return;
        using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var fabricant = bios?.GetValue("BaseBoardManufacturer") as string ?? "";
        if (!fabricant.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase))
            Skip = $"carte « {fabricant} » : la puce NCT6687 est celle des cartes MSI";
    }
}
