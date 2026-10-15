using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Lire le BIOS au lieu de le deviner, et ne rien dire des qu'un doute existe.
///
/// Les emplacements viennent de l'IFR du BIOS officiel MSI 1.H0 ; les valeurs « machine de
/// reference » sont celles relevees sur la carte de reference le 24/09 (XMP Profile 1, ReBAR et Above 4G
/// actifs, mode UEFI). La variable ici est synthetique, a la taille exacte de la vraie.
/// </summary>
public class NvramBiosSettingsReaderTests
{
    private const string Carte = "PRO B760-P WIFI DDR4 (MS-7D98)";
    private const string Firmware = "1.H0";
    private const int TailleSetup = 0x13D9;

    private static byte[] SetupDeReference()
    {
        var v = new byte[TailleSetup];
        v[0xE65] = 1;   // XMP : Profile 1
        v[0x10DC] = 1;  // Re-Size BAR : Enabled
        v[0x10DB] = 1;  // Above 4G : Enabled
        v[0x10C6] = 1;  // BIOS CSM/UEFI Mode : UEFI
        v[0xF88] = 1;   // CPU Cooler Tuning : Intel Default Settings (lu le 25/09)
        v[0xE67] = 0;   // Memory Fast Boot : Auto (lu le 25/09)
        v[0xC60] = 0;   // MSI Fast Boot : Disabled (lu le 25/09)
        return v;
    }

    private const int TailleCpuSetup = 0x3C1;

    // Le bloc processeur de la machine de reference, releve le 24/09 : tout a l'usine (C-State Auto, limite C Auto, EIST et Turbo
    // actifs, PL1/PL2 a 0 = Auto).
    private static byte[] CpuSetupDeReference(uint pl1 = 0)
    {
        var v = new byte[TailleCpuSetup];
        v[0x14] = 2; v[0x4B] = 255; v[0x9] = 1; v[0x16] = 1;
        BitConverter.GetBytes(pl1).CopyTo(v, 0x17);
        return v;
    }

    private static NvramBiosSettingsReader Lecteur(byte[]? setup, List<string>? appels = null, byte[]? cpu = null) =>
        new((nom, guid, _) =>
        {
            appels?.Add(nom);
            var v = nom == "CpuSetup" ? (setup is null ? null : cpu ?? CpuSetupDeReference()) : setup;
            return Task.FromResult(v is null
                ? Result<byte[]>.Fail(ErrorCodes.NotAllowlisted, "illisible")
                : Result<byte[]>.Ok(v));
        });

    private static SettingsState Etat(TriState xmp, TriState rebar, TriState csm = TriState.Unknown) =>
        new(xmp, rebar, "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c", TriState.On, TriState.On,
            Array.Empty<int>(), TriState.Unknown) { Csm = csm };

    [Fact]
    public async Task Les_reglages_de_la_machine_de_reference_se_lisent_tels_que_le_menu_les_affiche()
    {
        var lus = await Lecteur(SetupDeReference()).ReadAsync(Carte, Firmware);

        lus.Should().ContainEquivalentOf(new BiosSettingReading("XMP", 1, "Profile 1", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Re-Size BAR", 1, "Enabled", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Above 4G Decoding", 1, "Enabled", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CSM", 1, "UEFI", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Intel C-State", 2, "Auto", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Package C State Limit", 255, "Auto", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Long Duration Power Limit (PL1)", 0, "Auto", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Cooler Tuning", 1, "Intel Default Settings", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Memory Fast Boot", 0, "Auto", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("MSI Fast Boot", 0, "Disabled", IsDefault: true));
    }

    [Fact]
    public async Task La_variable_est_lue_une_seule_fois_pour_tous_ses_reglages()
    {
        var appels = new List<string>();
        await Lecteur(SetupDeReference(), appels).ReadAsync(Carte, Firmware);
        appels.Should().Equal("Setup", "CpuSetup");
    }

    [Fact]
    public async Task Une_variable_d_une_autre_taille_ne_decrit_pas_ce_firmware_et_rien_n_est_lu()
    {
        var autreVersion = new byte[TailleSetup + 8];
        autreVersion[0xE65] = 1;
        (await Lecteur(autreVersion).ReadAsync(Carte, Firmware)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_seul_octet_hors_des_valeurs_du_menu_jette_toute_la_lecture()
    {
        var v = SetupDeReference();
        v[0xE65] = 7;   // l'IFR du menu XMP s'arrete a 5
        (await Lecteur(v).ReadAsync(Carte, Firmware)).Should().BeEmpty();
    }

    [Fact]
    public async Task Un_profil_memoire_utilisateur_se_lit_et_ne_jette_pas_la_lecture()
    {
        var v = SetupDeReference();
        v[0xE65] = 4;   // « User Profile 1 », declare par l'IFR 1.H0
        var lus = await Lecteur(v).ReadAsync(Carte, Firmware);
        lus.Should().ContainEquivalentOf(new BiosSettingReading("XMP", 4, "User Profile 1", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Re-Size BAR", 1, "Enabled", IsDefault: true));
    }

    [Fact]
    public async Task La_limite_C_State_Cpu_Default_de_la_seconde_declaration_se_lit()
    {
        var cpu = CpuSetupDeReference();
        cpu[0x4B] = 254;   // « Cpu Default », seconde declaration de la question dans l'IFR 1.H0
        var lus = await Lecteur(SetupDeReference(), cpu: cpu).ReadAsync(Carte, Firmware);
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Package C State Limit", 254, "Cpu Default", IsDefault: false));
    }

    [Fact]
    public async Task Une_carte_ou_une_version_hors_matrice_ne_declenche_aucune_lecture()
    {
        var appels = new List<string>();
        (await Lecteur(SetupDeReference(), appels).ReadAsync(Carte, "1.H1")).Should().BeEmpty();
        (await Lecteur(SetupDeReference(), appels).ReadAsync("PRO Z790-A", Firmware)).Should().BeEmpty();
        appels.Should().BeEmpty();
    }

    [Fact]
    public async Task Une_lecture_refusee_ne_rend_rien_et_ne_jette_pas()
    {
        (await Lecteur(null).ReadAsync(Carte, Firmware)).Should().BeEmpty();
        var qui_jette = new NvramBiosSettingsReader((_, _, _) => throw new InvalidOperationException());
        (await qui_jette.ReadAsync(Carte, Firmware)).Should().BeEmpty();
    }

    [Fact]
    public void Le_cas_de_la_machine_de_reference_du_17_09_profil_desactive_et_RAM_reglee_a_la_main_se_dit_Off()
    {
        // L'inference voit 3800 MT/s au-dessus du standard et conclurait « XMP actif ». Le BIOS
        // lu dit « Disabled » : c'est lui qui a raison.
        var lus = new[] { new BiosSettingReading("XMP", 0, "Disabled", IsDefault: true) };
        var st = NvramBiosSettingsReader.Appliquer(Etat(TriState.On, TriState.Unknown), lus);
        st.XmpExpo.Should().Be(TriState.Off);
        st.BiosLu.Should().BeEquivalentTo(lus);
    }

    [Fact]
    public void Le_BIOS_dit_ReBAR_quand_Windows_ne_sait_pas_le_lire()
    {
        // Sur NVIDIA, Windows rend Unknown : la NVRAM est la seule source.
        var lus = new[] { new BiosSettingReading("Re-Size BAR", 1, "Enabled", true) };
        NvramBiosSettingsReader.Appliquer(Etat(TriState.On, TriState.Unknown), lus).ReBar.Should().Be(TriState.On);
    }

    [Fact]
    public void Si_Windows_voit_ReBAR_autrement_que_le_BIOS_toute_la_lecture_est_jetee()
    {
        var lus = new[]
        {
            new BiosSettingReading("XMP", 0, "Disabled", true),
            new BiosSettingReading("Re-Size BAR", 1, "Enabled", true),
        };
        var avant = Etat(TriState.On, TriState.Off);
        var apres = NvramBiosSettingsReader.Appliquer(avant, lus);
        apres.Should().Be(avant);
        apres.BiosLu.Should().BeNull();
    }

    [Fact]
    public void Un_demarrage_legacy_contredit_un_BIOS_lu_en_UEFI_et_jette_la_lecture()
    {
        var lus = new[] { new BiosSettingReading("CSM", 1, "UEFI", true) };
        var avant = Etat(TriState.On, TriState.Unknown, csm: TriState.On);
        NvramBiosSettingsReader.Appliquer(avant, lus).Should().Be(avant);
    }

    [Fact]
    public void La_lecture_n_ouvre_aucune_ecriture()
    {
        BiosQualificationMatrix.Pour(Carte, Firmware)
            .Should().OnlyContain(e => e.Nvram != null && !e.EcritureAutomatiqueAutorisee
                                       && e.Capability == BiosCapabilityLevel.Guided);
    }

    // Les réglages qui brident le processeur (NVRAM relevée le 11/10 : Current Limit 160 A puis Auto,
    // Lite Load Mode 9, IA CEP Disabled) se lisent avec le reste.
    [Fact]
    public async Task Limite_de_courant_Lite_Load_et_CEP_se_lisent_comme_le_menu_les_affiche()
    {
        var setup = SetupDeReference();
        BitConverter.GetBytes((ushort)160).CopyTo(setup, 0xF29);
        setup[0xF63] = 80;
        setup[0xFBC] = 1;

        var lus = await Lecteur(setup).ReadAsync("MS-7D98", "1.H0");

        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Current Limit", 160, "160 A", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Lite Load", 80, "Mode 9", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("IA CEP Support", 1, "Disabled", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Lite Load Control", 0, "Normal", IsDefault: true));

        BitConverter.GetBytes((ushort)0).CopyTo(setup, 0xF29);
        (await Lecteur(setup).ReadAsync("MS-7D98", "1.H0")).Should()
            .ContainEquivalentOf(new BiosSettingReading("CPU Current Limit", 0, "Auto", IsDefault: true));
    }

    // La courbe du ventilateur processeur relevée le 11/10 sur la machine de référence : 40/62/80/92 °C pour 35/55/70/100 %,
    // après qu'un ventilateur unique n'a pas tenu 160 W. Un octet par palier (IFR : Min 0, Max 100).
    [Fact]
    public async Task La_courbe_du_ventilateur_processeur_se_lit_palier_par_palier()
    {
        var setup = SetupDeReference();
        setup[0x1284] = 1;
        new byte[] { 40, 62, 80, 92 }.CopyTo(setup, 0x128A);
        new byte[] { 35, 55, 70, 100 }.CopyTo(setup, 0x128E);

        var lus = await Lecteur(setup).ReadAsync("MS-7D98", "1.H0");

        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Fan1 Smart Fan Control", 1, "Enabled", IsDefault: true));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Fan1 level 2 Temperature", 62, "62 °C", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Fan1 level 1 Fan Speed", 35, "35 %", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("CPU Fan1 level 4 Fan Speed", 100, "100 %", IsDefault: true));
    }

    [Fact]
    public async Task Une_limite_de_puissance_se_lit_en_watts_sur_quatre_octets()
    {
        var lus = await Lecteur(SetupDeReference(), cpu: CpuSetupDeReference(pl1: 125)).ReadAsync(Carte, Firmware);
        lus.Should().ContainEquivalentOf(new BiosSettingReading("Long Duration Power Limit (PL1)", 125, "125 W", IsDefault: false));
    }

    [Fact]
    public async Task Une_limite_hors_des_bornes_de_l_IFR_jette_toute_la_lecture()
    {
        // L'IFR borne PL1 a 4096 W : au-dela, la table ne decrit pas ce firmware.
        (await Lecteur(SetupDeReference(), cpu: CpuSetupDeReference(pl1: 5000)).ReadAsync(Carte, Firmware)).Should().BeEmpty();
    }

    [Fact]
    public async Task Les_reglages_de_demarrage_se_lisent_a_leur_offset_et_pas_sur_un_octet_vide()
    {
        // Sur la machine de reference les deux valent 0, comme un octet jamais ecrit : ce test-ci pose des valeurs non
        // nulles pour qu'un offset faux ne passe pas.
        var v = SetupDeReference();
        v[0xE67] = 3;   // Memory Fast Boot : No Training
        v[0xC60] = 1;   // MSI Fast Boot : Enabled
        var lus = await Lecteur(v).ReadAsync(Carte, Firmware);

        lus.Should().ContainEquivalentOf(new BiosSettingReading("Memory Fast Boot", 3, "No Training", IsDefault: false));
        lus.Should().ContainEquivalentOf(new BiosSettingReading("MSI Fast Boot", 1, "Enabled", IsDefault: false));
    }
}
