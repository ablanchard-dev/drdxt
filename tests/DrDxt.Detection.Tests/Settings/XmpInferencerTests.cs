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

    // --- JEDEC base-speed heuristic (inference, probably) ---

    [Fact]
    public void Ddr5_configured_at_jedec_base_with_higher_rated_is_xmp_off()
    {
        // DDR5 JEDEC base = 4800. Configured=4800, Rated=6000 → XMP probably Off.
        XmpInferencer.Infer(new[] { Mod(4800, 6000) }).Should().Be(TriState.Off);
    }

    [Fact]
    public void Ddr5_configured_and_rated_both_above_jedec_and_equal_is_xmp_on()
    {
        // DDR5 both at 6000 → both above JEDEC base 4800 and equal → XMP probably On.
        XmpInferencer.Infer(new[] { Mod(6000, 6000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void Missing_configured_speed_returns_unknown_not_on()
    {
        // ConfiguredMhz=0 → cannot compare → Unknown, never On.
        XmpInferencer.Infer(new[] { Mod(0, 6000) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Ddr4_at_jedec_base_with_nothing_proving_a_faster_kit_is_unknown_not_on()
    {
        // DDR4 3200/3200 sans tension lue. Apres un reset XMP, Windows rabat souvent la vitesse
        // nominale sur la vitesse reglee : 3200/3200 peut etre un kit 4000 tombe au socle. Rien
        // ne dit « profil charge » ; affirmer On serait le faux « XMP actif » du 29/06.
        XmpInferencer.Infer(new[] { Mod(3200, 3200) }).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Flash_bios_reset_on_a_kit_whose_part_number_hides_its_speed_is_never_on()
    {
        // Le cas du 29/06 (flash BIOS -> 2667/2667), mais sur un kit dont la reference ne
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
        // Real case (G.Skill F4-4000C18 stable at 3800): rated 4000, running 3800. 3800 is far
        // above the DDR4 JEDEC base (3200), so XMP is ON — it just is not at the rated max.
        // Comparing to the rated max used to flag this "off" = inventing a problem (banned §6/§11).
        XmpInferencer.Infer(new[] { Mod(3800, 4000) }).Should().Be(TriState.On);
    }

    [Fact]
    public void High_rated_ddr4_kit_dropped_to_jedec_is_xmp_off()
    {
        // Same 4000-rated kit sitting at JEDEC 2400 (e.g. after a BIOS flash reset XMP) IS off.
        XmpInferencer.Infer(new[] { Mod(2400, 4000) }).Should().Be(TriState.Off);
    }

    // ══════════════════════════════════════════════════════════════════════════
    //  26/08 — la TENSION comme seconde source. Mesuré : DDR4 JEDEC = 1200 mV,
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
        // Donc Unknown, pas On (24/09 : l'ancien On était le faux « XMP actif » du 29/06).
        XmpInferencer.Infer(new[] { Volt(4800, 4800, 1100, Ddr5) }).Should().Be(TriState.Unknown);

        // Et un DDR5 à 1250 mV (JEDEC 1100) est prouvé chargé même sans vitesse nominale.
        XmpInferencer.Infer(new[] { Volt(6000, 0, 1250, Ddr5) }).Should().Be(TriState.On);
    }
}
