using System.Reflection;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// A2 — OpAllowlist: validate BY TYPE + by target, leaf-exact, default-DENY.
//
// This is the master plan's defense n°1 (§9.13): not the HMAC, but the closed,
// leaf-exact allowlist. A blacklisted or unknown target is refused
// (HELPER.NOT_ALLOWLISTED) before any privileged action.
// -----------------------------------------------------------------------
public sealed class OpAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    private static SetRegistryValueOp Reg(string path, string name = "x") =>
        new(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "1");

    // --- Registry: allowed exact leaves pass ---

    [Theory]
    // NVIDIA Low Latency, gardée pour défaire une écriture passée.
    [InlineData(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")]
    // HAGS (Hardware-Accelerated GPU Scheduling).
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    public void Registry_allowed_exact_leaf_passes(string path, string name)
    {
        var op = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    // 04/10/2026 (revue sécurité 6.1) : les feuilles machine (HKLM) sans valeur bornée acceptaient tout nombre.
    // Les valeurs du compte (HKCU) restent libres : l'utilisateur peut déjà les écrire sans droits.
    [Theory]
    [InlineData(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\Processor", "CppcEnable")]
    public void Une_feuille_machine_refuse_une_valeur_hors_de_0_a_2(string path, string name)
    {
        _allow.Validate(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "2"))
            .IsOk.Should().BeTrue();
        _allow.Validate(new SetRegistryValueOp(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "4294967295"))
            .IsOk.Should().BeFalse();
    }

    [Fact]
    public void Registry_gamebar_allowmode_under_currentuser_passes()
    {
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AllowAutoGameMode",
            RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Registry_gamebar_autogamemode_enabled_under_currentuser_passes()
    {
        // NS-1 Game Mode ON sets the modern Settings toggle (AutoGameModeEnabled) alongside
        // AllowAutoGameMode — both are documented to be set together under GameBar.
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled",
            RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Registry_gamedvr_historical_capture_under_currentuser_passes()
    {
        // NS-1 Game DVR background-recording OFF flips the "Record what happened" toggle
        // (HistoricalCaptureEnabled) under HKCU GameDVR — surgical (manual capture untouched).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "HistoricalCaptureEnabled", RegistryValueType.DWord, "0");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Registry_directx_user_global_settings_string_under_currentuser_passes()
    {
        // NS-1 VRR global + flip-model live in this REG_SZ string (merged, AutoHDR preserved).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences",
            "DirectXUserGlobalSettings", RegistryValueType.String, "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    // 26/09/2026 : l'allowlist ne verifiait que la cible. Un op forge (le rejeu au demarrage en rejoue depuis
    // un fichier du compte) pouvait ecrire n'importe quel type et n'importe quelle valeur dans une feuille
    // permise : par exemple un fichier d'echange de 16 Mo, ou place ailleurs que sur un disque.
    // Le levier n'ecrit que deux valeurs : la gestion automatique, et, pour Annuler, le vide qu'il a lu
    // (il ne s'applique que si le fichier d'echange est desactive). Tout le reste est refuse.
    private const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private static SetRegistryValueOp PagingFiles(string valeur, RegistryValueType type = RegistryValueType.MultiString) =>
        new(RegistryHiveKind.LocalMachine, MemoryManagement, "PagingFiles", type, valeur);

    [Theory]
    [InlineData(@"?:\pagefile.sys")] // ce que le levier pose
    [InlineData("")]                 // ce que son Annuler remet : l'etat d'origine, desactive
    [InlineData(" \n ")]             // idem, lu avec des entrees blanches
    public void PagingFiles_accepte_les_deux_valeurs_que_le_levier_ecrit(string valeur) =>
        _allow.Validate(PagingFiles(valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(@"C:\pagefile.sys 16 16")]
    [InlineData(@"C:\pagefile.sys 4096 8192")]
    [InlineData(@"C:\Windows\evil.sys 4096 4096")]
    [InlineData("?:\\pagefile.sys\nC:\\pagefile.sys 16 16")]
    public void PagingFiles_refuse_toute_autre_valeur(string valeur) =>
        _allow.Validate(PagingFiles(valeur)).IsOk.Should().BeFalse();

    [Fact]
    public void Une_feuille_refuse_un_autre_type_que_le_sien()
    {
        _allow.Validate(PagingFiles(@"?:\pagefile.sys", RegistryValueType.String)).IsOk.Should().BeFalse();
        _allow.Validate(new SetRegistryValueOp(RegistryHiveKind.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.String, "2"))
            .IsOk.Should().BeFalse();
        _allow.Validate(new SetRegistryValueOp(RegistryHiveKind.CurrentUser,
            @"Software\Microsoft\DirectX\UserGpuPreferences", "DirectXUserGlobalSettings", RegistryValueType.DWord, "1"))
            .IsOk.Should().BeFalse();
    }

    // --- Registry: HARD BLACKLIST always denies (Defender/Update/BITS/bam/policies) ---

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend")]            // Defender
    [InlineData(@"SOFTWARE\Microsoft\Windows Defender")]                   // Defender
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]     // Windows Update
    [InlineData(@"SYSTEM\CurrentControlSet\Services\wuauserv")]            // Windows Update service
    [InlineData(@"SYSTEM\CurrentControlSet\Services\BITS")]               // BITS
    [InlineData(@"SYSTEM\CurrentControlSet\Services\bam")]                // bam
    [InlineData(@"SYSTEM\CurrentControlSet\Services\bam\State")]          // bam subtree
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\Safer")]            // security policy
    [InlineData(@"SECURITY\Policy")]                                       // security policy hive area
    public void Registry_blacklisted_subtree_is_denied(string path)
    {
        _allow.Validate(Reg(path)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // It is the BLACKLIST that denies these — not the leaf-exact default-DENY that would
    // refuse them anyway. Both curtains return NotAllowlisted, so asserting the code alone
    // cannot tell them apart: deleting the Defender entries from BlacklistedSubtrees left
    // the whole suite green (verified by mutation 2026-09-01). The blacklist exists for the
    // case the code comments out: "even if some future allow-rule overlaps it" — so the
    // assertion has to name which rule fired, or the second curtain is decorative.
    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend")]
    [InlineData(@"SOFTWARE\Microsoft\Windows Defender")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows Defender")]
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\BITS")]
    public void Registry_protected_subtree_is_denied_BY_THE_BLACKLIST(string path)
    {
        _allow.Validate(Reg(path)).Error!.Value.Message
            .Should().Contain("blacklist",
                "removing this subtree from BlacklistedSubtrees must fail a test, not pass silently");
    }

    // La theorie ci-dessus liste les sous-arbres a la main, et elle en oubliait la moitie :
    // wuauserv, bam, Safer, SECURITY, SAM et le WindowsUpdate de CurrentVersion n'y etaient pas.
    // Elle n'exercait par ailleurs que la CLE EXACTE — jamais un enfant. Mutation du 06/09/2026 :
    // remplacer la branche `StartsWith(subtree + separateur)` d'IsUnderSubtree par `false` laisse
    // les 397 tests au vert. Autrement dit, la liste noire ne couvrait plus que 11 cles precises
    // au lieu de 11 sous-ARBRES, et rien ne le disait — alors que c'est exactement le cas qu'elle
    // existe pour couvrir : une regle d'autorisation future qui viserait une feuille sous Defender.
    //
    // Ce controle enumere donc le tableau reel par reflexion : il ne peut plus prendre du retard
    // sur lui. Et il exige le message, pas seulement le code : les deux rideaux rendent
    // NotAllowlisted, seul le motif dit lequel a ferme.
    [Fact]
    public void Every_blacklisted_subtree_denies_the_key_AND_its_children_by_the_blacklist()
    {
        var subtrees = (string[])typeof(OpAllowlist)
            .GetField("BlacklistedSubtrees", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

        // Garde de non-vacuite : une reflexion cassee rendrait la meme couleur qu'un depot sain.
        subtrees.Should().HaveCountGreaterThanOrEqualTo(
            11, "la liste noire du 06/09/2026 compte 11 sous-arbres ; en perdre un doit rougir");

        foreach (var subtree in subtrees)
        {
            _allow.Validate(Reg(subtree)).Error!.Value.Message
                .Should().Contain("blacklist", "la cle {0} elle-meme doit tomber sur la liste noire", subtree);

            _allow.Validate(Reg(subtree + @"\Enfant\PlusProfond")).Error!.Value.Message
                .Should().Contain("blacklist", "un ENFANT de {0} doit tomber sur la liste noire, "
                    + "sinon une future feuille autorisee sous ce sous-arbre passerait", subtree);
        }
    }

    // --- Registry: anything not explicitly allowed is denied (default-DENY) ---

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]   // autostart — not ours
    [InlineData(@"SOFTWARE\Random\Vendor\Key")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers")] // allowed path, but wrong VALUE name
    public void Registry_unallowed_target_is_denied(string path)
    {
        // For the third case the path is allowed but the value name "x" is not the
        // exact allowed leaf — leaf-exact means (path, name) both matter.
        _allow.Validate(Reg(path)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Registry_allowed_path_but_wrong_value_name_is_denied_leaf_exact()
    {
        // GraphicsDrivers path is allowed for HwSchMode only; a different value
        // name under the same path must be refused (leaf-exact, not subtree).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "SomethingElse", RegistryValueType.DWord, "1");
        _allow.Validate(op).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Registry_allowed_leaf_but_wrong_hive_is_denied()
    {
        // HwSchMode is an HKLM leaf; the same (path,name) under HKCU is not allowed.
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "HwSchMode", RegistryValueType.DWord, "1");
        _allow.Validate(op).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void DeleteRegistryValueOp_uses_same_leaf_exact_allowlist()
    {
        var allowed = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode");
        _allow.Validate(allowed).IsOk.Should().BeTrue();

        // 24/09/2026 : ReflexEnabled n'est plus autorisée, aucun levier ne l'écrivait.
        var retiree = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "ReflexEnabled");
        _allow.Validate(retiree).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

        var denied = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start");
        _allow.Validate(denied).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- Power plan: only known Windows GUIDs ---

    [Fact]
    public void PowerPlan_known_guids_pass()
    {
        _allow.Validate(new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance)).IsOk.Should().BeTrue();
        _allow.Validate(new SetActivePowerPlanOp(KnownPowerPlans.Balanced)).IsOk.Should().BeTrue();
        _allow.Validate(new SetActivePowerPlanOp(KnownPowerPlans.Ultimate)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void PowerPlan_random_guid_is_denied()
    {
        _allow.Validate(new SetActivePowerPlanOp(Guid.NewGuid()))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- M3 lot 2: the firmware trip. The op carries only a delay, so that bound IS the
    //     whole authorization — which makes testing it the whole point. ---

    [Fact]
    public void Firmware_reboot_inside_the_band_passes()
    {
        _allow.Validate(new RebootToFirmwareSetupOp(0)).IsOk.Should().BeTrue();
        _allow.Validate(new RebootToFirmwareSetupOp(10)).IsOk.Should().BeTrue();
        _allow.Validate(new RebootToFirmwareSetupOp(300)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Firmware_reboot_outside_the_band_is_denied()
    {
        // Past a few minutes the user has moved on and no longer connects the restart to
        // anything they clicked — a reboot with no visible cause is what destroys trust.
        _allow.Validate(new RebootToFirmwareSetupOp(301))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        _allow.Validate(new RebootToFirmwareSetupOp(86_400))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        _allow.Validate(new RebootToFirmwareSetupOp(-1))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Cancelling_a_pending_firmware_boot_is_always_allowed()
    {
        // Refusing to CANCEL could only ever harm the user: it would leave a reboot armed
        // that they asked to call off. There is nothing to validate and nothing to gate.
        _allow.Validate(new CancelPendingFirmwareBootOp()).IsOk.Should().BeTrue();
    }

    // --- Service control: only AMD3DVCacheSvc with allowed actions ---

    [Theory]
    [InlineData(ServiceAction.Install)]
    [InlineData(ServiceAction.Start)]
    [InlineData(ServiceAction.Stop)]
    [InlineData(ServiceAction.Query)]
    public void Service_amd3dvcache_allowed_actions_pass(ServiceAction action)
    {
        _allow.Validate(new RunServiceControlOp("AMD3DVCacheSvc", action)).IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("wuauserv")]
    [InlineData("WinDefend")]
    [InlineData("BITS")]
    [InlineData("Spooler")]
    [InlineData("")]
    public void Service_unknown_or_blacklisted_name_is_denied(string name)
    {
        _allow.Validate(new RunServiceControlOp(name, ServiceAction.Stop))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- Lecture firmware : espace EFI global uniquement, noms feuille-exacte ---

    [Theory]
    [InlineData("BootOrder")]
    [InlineData("SecureBoot")]
    [InlineData("BootCurrent")]
    public void ReadFirmwareVariable_KnownGlobalVariable_IsAllowed(string name)
    {
        _allow.Validate(new ReadFirmwareVariableOp(name, OpAllowlist.EfiGlobalVariableGuid))
              .IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("Setup")]        // « Setup » n'existe pas dans l'espace EFI global (mesure 27/08 : 203)
    [InlineData("setup")]        // la casse ne doit pas ouvrir une porte fermee
    [InlineData("")]
    [InlineData("BootOrderX")]
    public void ReadFirmwareVariable_UnknownOrProprietaryName_IsDenied(string name)
    {
        _allow.Validate(new ReadFirmwareVariableOp(name, OpAllowlist.EfiGlobalVariableGuid))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void ReadFirmwareVariable_ForeignGuid_IsDenied()
    {
        // Un nom permis reste refuse hors des espaces connus : le GUID est le premier verrou.
        _allow.Validate(new ReadFirmwareVariableOp("BootOrder", "{deadbeef-0000-0000-0000-000000000000}"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- L3.1 : le varstore CONSTRUCTEUR, en lecture seule -------------------------------------

    [Fact]
    public void ReadFirmwareVariable_the_vendor_setup_varstore_is_readable()
    {
        // Mesure sur la carte de reference (MSI PRO B760-P, AMI 1.H0) : 5081 octets lus sous ce GUID.
        // Le GUID etait un candidat que ce depot ne peut citer depuis aucune specification ; c'est
        // la carte qui a tranche, pas une supposition.
        _allow.Validate(new ReadFirmwareVariableOp("Setup", OpAllowlist.AmiSetupVarstoreGuid))
              .IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("BootOrder")]
    [InlineData("SecureBoot")]
    [InlineData("setup")]
    public void ReadFirmwareVariable_the_vendor_namespace_does_not_inherit_the_global_names(string name)
    {
        // Chaque espace a SON ensemble ferme. Ouvrir le varstore constructeur pour « Setup » ne
        // doit pas y ouvrir tout ce qui etait permis ailleurs : deux espaces, deux listes, et une
        // liste commune serait le raccourci qui les elargit toutes les deux.
        _allow.Validate(new ReadFirmwareVariableOp(name, OpAllowlist.AmiSetupVarstoreGuid))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void No_firmware_WRITE_op_exists_at_all_so_opening_a_read_cannot_open_a_write()
    {
        // La garantie structurelle du lot, et la raison pour laquelle ouvrir « Setup » en lecture
        // n'ouvre rien d'autre : il n'existe aucune op d'ecriture firmware dans l'enumeration
        // fermee. Ce n'est pas une regle qu'on applique, c'est une capacite qu'on n'a pas
        // construite -- et une capacite absente est une capacite qu'un defaut futur ne peut pas
        // detourner. Ecrire au mauvais offset peut rendre une carte non demarrable, et le clear
        // CMOS ne suffit pas toujours.
        // L'ensemble EXHAUSTIF des ops qui touchent au firmware, et ce que chacune fait :
        //   ReadFirmwareVariable      — LIT une variable. Aucune ecriture.
        //   RebootToFirmwareSetup     — demande a WINDOWS d'envoyer le prochain demarrage dans le
        //                               setup (shutdown.exe /r /fw). C'est Windows qui detient le
        //                               bit OsIndications ; DrDXT n'ecrit aucune variable.
        //   CancelPendingFirmwareBoot — retire cette demande.
        // Aucune n'ecrit dans NVRAM, et il n'existe pas de quatrieme membre.
        Enum.GetNames<PrivilegedOpType>()
            .Where(n => n.Contains("Firmware", StringComparison.Ordinal))
            .Should().BeEquivalentTo(
                new[]
                {
                    nameof(PrivilegedOpType.ReadFirmwareVariable),
                    nameof(PrivilegedOpType.RebootToFirmwareSetup),
                    nameof(PrivilegedOpType.CancelPendingFirmwareBoot),
                },
                "toute op firmware NOUVELLE doit etre un acte delibere, visible dans ce test");

        // Et la garde qui porte vraiment le sens : aucun nom ne parle d'ECRIRE.
        Enum.GetNames<PrivilegedOpType>()
            .Should().NotContain(n => n.Contains("WriteFirmware", StringComparison.Ordinal)
                                   || n.Contains("SetFirmwareVariable", StringComparison.Ordinal));
    }

    // --- InstallPresentMonService is allowlisted (bounded to the vendored binary) ---

    [Fact]
    public void InstallPresentMonService_is_allowed()
    {
        _allow.Validate(new InstallPresentMonServiceOp()).IsOk.Should().BeTrue();
    }

    // --- NVAPI / ADLX: structural validation (SafeBounds clamping is Phase E) ---

    [Fact]
    public void NvApiClockOffset_structurally_valid_passes()
    {
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, -100, 0)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void NvApiClockOffset_negative_gpu_index_is_denied()
    {
        _allow.Validate(new ApplyNvApiClockOffsetOp(-1, -100, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- M1: the ELEVATED layer enforces the GpuSafeBounds CORE policy band itself
    // (reject, don't clamp). It never defers value bounds to the client. Band = [-200, +100].

    [Theory]
    [InlineData(100)]   // upper bound — accepted
    [InlineData(-200)]  // lower bound — accepted
    [InlineData(0)]     // stock — accepted
    [InlineData(-120)]  // a normal undervolt — accepted
    public void NvApiClockOffset_core_within_policy_band_is_accepted(int coreOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, coreOffsetMhz, 0)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(101)]   // one past the top of the band
    [InlineData(500)]   // the old hard rail (±500) — now REJECTED at the boundary
    [InlineData(-201)]  // one past the bottom of the band
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)] // no Math.Abs overflow
    public void NvApiClockOffset_core_outside_policy_band_is_rejected(int coreOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, coreOffsetMhz, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Theory]
    [InlineData(500)]   // upper memory bound — accepted
    [InlineData(-500)]  // lower memory bound — accepted
    public void NvApiClockOffset_memory_within_policy_band_is_accepted(int memOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, 0, memOffsetMhz)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(501)]
    [InlineData(-501)]
    [InlineData(5000)]
    public void NvApiClockOffset_memory_outside_policy_band_is_rejected(int memOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, 0, memOffsetMhz))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void NvApiPowerLimit_out_of_0_100_range_is_denied()
    {
        _allow.Validate(new ApplyNvApiPowerLimitOp(0, 250))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        _allow.Validate(new ApplyNvApiPowerLimitOp(0, -5))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void NvApiPowerLimit_in_range_passes()
    {
        _allow.Validate(new ApplyNvApiPowerLimitOp(0, 90)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void NvApiDrsSetting_valid_passes_and_blank_profile_denied()
    {
        // 0x10835002 = the CORRECTED FRL_FPS id.
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 3u, "Base Profile")).IsOk.Should().BeTrue();
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 3u, ""))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Theory]
    // The CORRECTED leaf-exact DRS id set (recipe §C) all pass.
    [InlineData(0x007BA09Eu)] // PRERENDERLIMIT (unchanged)
    [InlineData(0x00A879CFu)] // VSYNCMODE (corrected, was 0x00fc00fa)
    [InlineData(0x10835002u)] // FRL_FPS  (corrected, was 0x10835000)
    public void NvApiDrsSetting_corrected_ids_pass(uint settingId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, 1u, "Base Profile")).IsOk.Should().BeTrue();

    [Theory]
    // HISTORIQUE, parce qu'il explique la forme de la regle : le ProfileName porte par l'op etait
    // DECORATIF. « cod.exe » et « Base Profile » produisaient la meme ecriture machine entiere, et
    // l'allowlist laissait passer les deux tant que la chaine n'etait pas vide -- une escalade de
    // portee silencieuse, ou un appelant demandant un changement par jeu en obtenait un pour toute
    // la machine sans qu'aucune couche ne le dise.
    //
    // L'allowlist a d'abord refuse toute portee que l'executeur ne savait pas honorer. Depuis L2,
    // il sait viser un profil d'application, et la regle ouvre cette portee -- mais seulement sous
    // la forme que NVIDIA accepte : un chemin d'executable complet. Tout ce qui suit reste refuse.
    [InlineData("cod.exe")]                 // nom nu : NVIDIA le rejette comme ambigu
    [InlineData("Call of Duty HQ")]         // un libelle, pas un chemin
    [InlineData("Global")]                  // pas davantage le nom du profil de base
    [InlineData("base profile")]            // correspondance EXACTE : aucune tolerance de casse
    [InlineData(@"games\cod.exe")]          // relatif
    [InlineData(@"C:\games\..\cod.exe")]    // remontee : la portee doit se lire telle quelle
    [InlineData(@"\\nas\games\cod.exe")]    // UNC : un profil de jeu vit sur la machine
    [InlineData(@"C:\games\cod.dll")]      // pas un executable
    [InlineData(@"C:\games\cod.exe?")]     // caractere impossible dans un chemin
    public void NvApiDrsSetting_a_profile_the_executor_cannot_honour_is_denied(string profile)
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 3u, profile))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void NvApiDrsSetting_the_base_profile_is_the_machine_wide_scope()
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 3u, "Base Profile"))
              .IsOk.Should().BeTrue();

    [Theory]
    // L2 — la portee par JEU, desormais honorable, donc desormais autorisee. Un chemin complet
    // est la seule forme que NVIDIA accepte : sur un nom nu le driver rend
    // NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS, et l'ecriture echouerait au bord natif apres avoir
    // laisse croire qu'elle etait permise.
    [InlineData(@"C:\Program Files\Call of Duty\cod.exe")]
    [InlineData(@"D:\Games\game.EXE")]     // l'extension se compare sans la casse
    [InlineData("C:/Games/game.exe")]      // NVIDIA accepte les deux separateurs
    public void NvApiDrsSetting_a_full_executable_path_is_now_an_allowed_scope(string profile)
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 3u, profile))
              .IsOk.Should().BeTrue();

    [Fact]
    public void NvApiDrsSetting_a_per_application_scope_does_not_widen_the_setting_id_set()
    {
        // La garde qui compte : ouvrir la PORTEE ne doit pas ouvrir le reste. Un identifiant
        // forge reste refuse, quelle que soit la finesse de la portee demandee.
        _allow.Validate(new ApplyNvApiDrsSettingOp(0xDEADBEEFu, 3u, @"C:\Games\game.exe"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- PHASE 18 F1 lot 3 : la SUPPRESSION au profil d'un jeu, plus etroite que l'ecriture ---

    private const string GameExe = @"C:\Program Files\Call of Duty\cod.exe";

    [Fact]
    public void NvApiDrsDelete_an_inherited_setting_at_game_scope_is_allowed()
        => _allow.Validate(new DeleteNvApiDrsSettingOp(0x1057EB71u, GameExe,
                DrsValueOrigin.InheritedFromGlobalProfile, 0))
              .IsOk.Should().BeTrue();

    [Fact]
    public void NvApiDrsDelete_NEVER_touches_the_base_profile()
        // Supprimer au global effacerait un reglage pour toute la machine : ce n'est l'inverse de
        // rien que le produit ecrive.
        => _allow.Validate(new DeleteNvApiDrsSettingOp(0x1057EB71u, "Base Profile",
                DrsValueOrigin.InheritedFromGlobalProfile, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Theory]
    [InlineData("")]
    [InlineData("cod.exe")]
    [InlineData(@"\\nas\games\cod.exe")]
    public void NvApiDrsDelete_a_scope_that_is_not_a_full_game_path_is_denied(string profile)
        => _allow.Validate(new DeleteNvApiDrsSettingOp(0x1057EB71u, profile,
                DrsValueOrigin.InheritedFromGlobalProfile, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void NvApiDrsDelete_does_not_widen_the_setting_id_set()
        => _allow.Validate(new DeleteNvApiDrsSettingOp(0xDEADBEEFu, GameExe,
                DrsValueOrigin.InheritedFromGlobalProfile, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Theory]
    [InlineData(DrsValueOrigin.SetByUser)]  // se restaure par reecriture, jamais par suppression
    [InlineData(DrsValueOrigin.Unknown)]    // ne fonde aucun geste
    [InlineData((DrsValueOrigin)99)]        // valeur hors enum = fil forge
    public void NvApiDrsDelete_an_origin_the_profile_OWNED_or_that_is_unknown_is_denied(DrsValueOrigin origine)
        => _allow.Validate(new DeleteNvApiDrsSettingOp(0x1057EB71u, GameExe, origine, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Theory]
    // The OLD (wrong) ids must now be DENIED — proves the correction landed in the
    // closed allow-set, not just added alongside the old values.
    [InlineData(0x00fc00fau)] // old VSYNC
    [InlineData(0x10835000u)] // old FRL
    [InlineData(0xDEADBEEFu)] // forged
    public void NvApiDrsSetting_wrong_or_forged_id_is_denied_leaf_exact(uint settingId) =>
        // "Base Profile" on purpose: with any other name the denial would come from the SCOPE
        // check and this test would pass without ever exercising the id set it exists to pin.
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, 1u, "Base Profile"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- EnableSystemRestore: leaf-exact to the SYSTEM drive only ---

    [Fact]
    public void EnableSystemRestore_system_drive_passes()
    {
        // The system drive in canonical form (resolved from %SystemDrive%, ~"C:").
        var sysDrive = (Environment.GetEnvironmentVariable("SystemDrive") ?? "C:").TrimEnd('\\');
        _allow.Validate(new EnableSystemRestoreOp(sysDrive)).IsOk.Should().BeTrue();
        // Trailing-slash / bare-letter forms normalize to the same leaf.
        _allow.Validate(new EnableSystemRestoreOp(sysDrive + "\\")).IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("D:")]          // a data drive — never SR-enabled by DrDXT
    [InlineData("Z:")]
    [InlineData(@"C:\Windows")] // a path, not a drive letter
    [InlineData("")]
    [InlineData("notadrive")]
    public void EnableSystemRestore_non_system_or_invalid_drive_is_denied(string drive)
    {
        // (D:/Z: assume the system drive is C: in CI — true on the standard runners.)
        _allow.Validate(new EnableSystemRestoreOp(drive))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void AdlxTuning_known_kind_passes_and_bad_index_denied()
    {
        _allow.Validate(new ApplyAdlxTuningOp(0, AdlxTuningKind.AntiLag, 1)).IsOk.Should().BeTrue();
        _allow.Validate(new ApplyAdlxTuningOp(-1, AdlxTuningKind.AntiLag, 1))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- Restore point + Ping pass (no target to constrain) ---

    [Fact]
    public void CreateRestorePoint_passes()
    {
        _allow.Validate(new CreateRestorePointOp(CreateRestorePointOp.NomDrDxt)).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Ping_passes()
    {
        _allow.Validate(new PingOp()).IsOk.Should().BeTrue();
    }

    // --- Default-DENY: an unmodelled / null op is refused ---

    [Fact]
    public void Null_op_is_denied()
    {
        _allow.Validate(null!).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    private sealed record RogueOp : PrivilegedOp;

    [Fact]
    public void Unknown_op_subtype_is_denied_default_deny()
    {
        _allow.Validate(new RogueOp()).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // L-WU : les deux heures d'activité du réglage utilisateur, une heure de 0 à 23, rien d'autre de Windows Update.
    private static SetRegistryValueOp Heure(string nom, string valeur, string chemin = HeuresActives.Cle,
        RegistryValueType type = RegistryValueType.DWord) => new(RegistryHiveKind.LocalMachine, chemin, nom, type, valeur);

    [Theory]
    [InlineData(HeuresActives.NomDebut, "15")]
    [InlineData(HeuresActives.NomFin, "9")]
    [InlineData(HeuresActives.NomFin, "0")]
    [InlineData(HeuresActives.NomDebut, "23")]
    public void Heures_d_activite_accepte_une_heure(string nom, string valeur) =>
        _allow.Validate(Heure(nom, valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(HeuresActives.NomDebut, "24")]
    [InlineData(HeuresActives.NomFin, "-1")]
    [InlineData(HeuresActives.NomFin, "09")]
    [InlineData(HeuresActives.NomFin, "4294967295")]
    [InlineData(HeuresActives.NomAutomatique, "0")]
    [InlineData("IsExpedited", "1")]
    public void Heures_d_activite_refuse_le_reste(string nom, string valeur) =>
        _allow.Validate(Heure(nom, valeur)).IsOk.Should().BeFalse();

    [Fact]
    public void La_strategie_Windows_Update_reste_interdite_meme_pour_les_heures()
    {
        _allow.Validate(Heure("ActiveHoursStart", "15", @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")).IsOk.Should().BeFalse();
        _allow.Validate(Heure(HeuresActives.NomDebut, "15", type: RegistryValueType.String)).IsOk.Should().BeFalse();
    }

    // L-CPU : le mode de boost secteur (PERFBOOSTMODE). Microsoft Learn ne documente que 0 a 4 ; powercfg en enumere 7
    // sur Windows 11 26200 (5 et 6 « a garanti »), jamais ecrits. Un plan vide ne designe rien.
    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(2u)]
    [InlineData(4u)]
    public void Mode_de_boost_accepte_une_valeur_documentee(uint valeur) =>
        _allow.Validate(new SetProcessorBoostModeOp(KnownPowerPlans.Balanced, valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(5u)]
    [InlineData(6u)]
    [InlineData(uint.MaxValue)]
    public void Mode_de_boost_refuse_une_valeur_forgee(uint valeur) =>
        _allow.Validate(new SetProcessorBoostModeOp(KnownPowerPlans.Balanced, valeur)).Error!.Value.Code
            .Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void Mode_de_boost_refuse_un_plan_vide() =>
        _allow.Validate(new SetProcessorBoostModeOp(Guid.Empty, 2)).IsOk.Should().BeFalse();
}
