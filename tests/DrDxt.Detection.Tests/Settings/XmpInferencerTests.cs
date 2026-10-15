using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

public class XmpInferencerTests
{
    private static MemoryModule Mod(int cfg, int rated) => new("PN", "Corsair", cfg, rated);

    [Fact]
    public void Configured_below_rated_means_xmp_probably_off()
    {
        XmpInferencer.Infer(new[] { Mod(4800, 6000) }).Should().Be(TriState.Off);
    }

    [Fact]
    public void Configured_at_rated_means_xmp_on()
    {
        XmpInferencer.Infer(new[] { Mod(6000, 6000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void Within_tolerance_counts_as_on()
    {
        // DDR5 affiche parfois 5990 vs 6000 → ~0.2% d'écart = ON.
        XmpInferencer.Infer(new[] { Mod(5990, 6000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void Missing_rated_speed_is_unknown_not_a_lie()
    {
        XmpInferencer.Infer(new[] { Mod(4800, 0) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void No_modules_is_unknown()
    {
        XmpInferencer.Infer(Array.Empty<MemoryModule>()).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Any_module_below_rated_drops_whole_to_off()
    {
        XmpInferencer.Infer(new[] { Mod(6000, 6000), Mod(4800, 6000) }).Should().Be(TriState.Off);
    }

    // --- Heuristique de vitesse de base JEDEC (inférence, probable) ---

    [Fact]
    public void Ddr5_configured_at_jedec_base_with_higher_rated_is_xmp_off()
    {
        // Base JEDEC DDR5 = 4800. Configuré=4800, nominal=6000 → XMP probablement désactivé.
        XmpInferencer.Infer(new[] { Mod(4800, 6000) }).Should().Be(TriState.Off);
    }

    [Fact]
    public void Ddr5_configured_and_rated_both_above_jedec_and_equal_is_xmp_on()
    {
        // DDR5 toutes deux à 6000 → toutes deux au-dessus de la base JEDEC 4800 et égales → XMP probablement actif.
        XmpInferencer.Infer(new[] { Mod(6000, 6000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void Missing_configured_speed_returns_unknown_not_on()
    {
        // ConfiguredMhz=0 → impossible de comparer → Unknown, jamais On.
        XmpInferencer.Infer(new[] { Mod(0, 6000) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Ddr4_at_jedec_base_with_nothing_proving_a_faster_kit_is_unknown_not_on()
    {
        // DDR4 3200/3200 sans tension lue. Apres un reset XMP, Windows rabat souvent la vitesse
        // nominale sur la vitesse reglee : 3200/3200 peut etre un kit 4000 tombe au socle. Rien
        // ne dit « profil charge » ; affirmer On serait un faux « XMP actif ».
        XmpInferencer.Infer(new[] { Mod(3200, 3200) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Flash_bios_reset_on_a_kit_whose_part_number_hides_its_speed_is_never_on()
    {
        // Un flash BIOS qui rabat a 2667/2667, sur un kit dont la reference ne
        // donne pas la vitesse (Kingston Fury compact) : le part number ne sauve plus rien.
        // Tension JEDEC : aucune preuve de profil. On ne sait pas ; on ne dit surtout pas On.
        XmpInferencer.Infer(new[] { Volt(2667, 2667, 1200) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void At_jedec_base_a_profile_voltage_still_proves_the_profile()
    {
        // Kit XMP 3200 a 1,35 V : la frequence ne tranche pas, la tension si.
        XmpInferencer.Infer(new[] { Volt(3200, 3200, 1350) }).Should().Be(TriState.On);
    }

    [Fact]
    public void High_rated_ddr4_kit_run_below_its_max_but_above_jedec_is_xmp_on()
    {
        // Cas réel (G.Skill F4-4000C18 stable à 3800) : nominal 4000, tourne à 3800. 3800 est bien
        // au-dessus de la base JEDEC DDR4 (3200), donc XMP est actif ; il n'est juste pas au nominal max.
        // Comparer au nominal max signalait ceci « désactivé » = inventer un problème (interdit).
        XmpInferencer.Infer(new[] { Mod(3800, 4000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void High_rated_ddr4_kit_dropped_to_jedec_is_xmp_off()
    {
        // Le même kit nominal 4000 posé au JEDEC 2400 (après un flash du BIOS qui remet XMP à zéro) est bien désactivé.
        XmpInferencer.Infer(new[] { Mod(2400, 4000) }).Should().Be(TriState.Off);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  La TENSION comme seconde source. Mesuré : DDR4 JEDEC = 1200 mV,
    //  la machine de référence tourne à 1350 mV, ce qui prouve un profil chargé
    //  sans rien devoir à la fréquence.
    // ══════════════════════════════════════════════════════════════════════════

    private const int Ddr4 = 26;
    private const int Ddr5 = 34;

    private static MemoryModule Volt(int cfg, int rated, int mv, int type = Ddr4) =>
        new("PN", "G.Skill", cfg, rated, SmbiosMemoryType: type, ConfiguredVoltageMv: mv);

    [Fact]
    public void Voltage_resolves_the_case_the_frequency_cannot_judge()
    {
        // Vitesse nominale illisible (part number exotique) : avant, c'était Unknown.
        // 1350 mV sur du DDR4 (JEDEC 1200) prouve qu'un profil est chargé.
        XmpInferencer.Infer(new[] { Volt(3800, 0, 1350) }).Should().Be(TriState.On);
    }

    [Fact]
    public void Voltage_NEVER_hides_a_module_stuck_at_JEDEC_speed()
    {
        // Le piège. Une carte peut charger la tension du profil puis échouer l'entraînement
        // mémoire et retomber au socle. On lit alors 1350 mV À 2133 MT/s. Conclure « profil
        // chargé » masquerait le défaut exact que l'utilisateur doit corriger.
        XmpInferencer.Infer(new[] { Volt(2133, 4000, 1350) }).Should().Be(TriState.Off);
    }

    [Fact]
    public void Voltage_at_JEDEC_never_concludes_OFF_by_itself()
    {
        // Asymétrie. Des profils XMP existent à la tension JEDEC : 1200 mV ne prouve rien.
        // La fréquence décide seule, et ici elle dit ON.
        XmpInferencer.Infer(new[] { Volt(3600, 3600, 1200) }).Should().Be(TriState.On);
    }

    [Fact]
    public void An_unread_voltage_is_never_treated_as_zero_volts()
    {
        // 0 mV = « pas lu », jamais « zéro volt ». Aucun signal n'en est tiré.
        XmpInferencer.Infer(new[] { Volt(3800, 0, 0) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void The_memory_type_is_READ_instead_of_guessed_from_the_speed()
    {
        // Un DDR5 lu 4800/4800 à 1100 mV est au socle JEDEC du type LU : rien ne prouve un profil,
        // et un kit 6000 remis à zéro dont Windows rabat la nominale donnerait la même lecture.
        // Donc Unknown, pas On : On serait un faux « XMP actif ».
        XmpInferencer.Infer(new[] { Volt(4800, 4800, 1100, Ddr5) }).Should().Be(TriState.Unknown);

        // Et un DDR5 à 1250 mV (JEDEC 1100) est prouvé chargé même sans vitesse nominale.
        XmpInferencer.Infer(new[] { Volt(6000, 0, 1250, Ddr5) }).Should().Be(TriState.On);
    }
}
