using System.Reflection;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// OpAllowlist : valider par type et par cible, feuille exacte, refus par défaut.
//
// C'est la défense n° 1 : pas le HMAC, mais la liste blanche fermée, feuille exacte.
// Une cible interdite ou inconnue est refusée (HELPER.NOT_ALLOWLISTED)
// avant toute action privilégiée.
// -----------------------------------------------------------------------
public sealed class OpAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    private static SetRegistryValueOp Reg(string path, string name = "x") =>
        new(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "1");

    // La liste noire du README ne tient pas seulement par les identifiants du catalogue : un levier nommé autrement qui
    // écrirait un mythe passerait par l'une de ces feuilles, et le helper la refuse.
    [Theory]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "SystemResponsiveness")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{00000000-0000-0000-0000-000000000000}", "TcpAckFrequency")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{00000000-0000-0000-0000-000000000000}", "TCPNoDelay")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\cod.exe\PerfOptions", "CpuPriorityClass")]
    [InlineData(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff")]
    public void Les_feuilles_des_mythes_sont_refusees(RegistryHiveKind ruche, string path, string name)
    {
        var op = new SetRegistryValueOp(ruche, path, name, RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeFalse();
        _allow.Validate(new DeleteRegistryValueOp(ruche, path, name)).IsOk.Should().BeFalse();
    }

    // --- Registre : les feuilles exactes autorisées passent ---

    [Theory]
    // NVIDIA Low Latency, gardée pour défaire une écriture passée.
    [InlineData(@"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "LowLatencyMode")]
    // HAGS (planification GPU accélérée par le matériel).
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")]
    public void Registry_allowed_exact_leaf_passes(string path, string name)
    {
        var op = new SetRegistryValueOp(RegistryHiveKind.LocalMachine, path, name, RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    // Les feuilles machine (HKLM) sans valeur bornée acceptaient tout nombre.
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
        // Le Mode Jeu activé pose la bascule moderne des Paramètres (AutoGameModeEnabled) à côté
        // d'AllowAutoGameMode : la documentation les pose ensemble sous GameBar.
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled",
            RegistryValueType.DWord, "1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Registry_gamedvr_historical_capture_under_currentuser_passes()
    {
        // Le Game DVR en arrière-plan désactivé bascule « Enregistrer ce qui s'est passé »
        // (HistoricalCaptureEnabled) sous HKCU GameDVR : chirurgical (la capture manuelle reste intacte).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR",
            "HistoricalCaptureEnabled", RegistryValueType.DWord, "0");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Registry_directx_user_global_settings_string_under_currentuser_passes()
    {
        // VRR global et modèle de retournement vivent dans cette chaîne REG_SZ (fusionnée, AutoHDR préservé).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences",
            "DirectXUserGlobalSettings", RegistryValueType.String, "VRROptimizeEnable=1;SwapEffectUpgradeEnable=1");
        _allow.Validate(op).IsOk.Should().BeTrue();
    }

    // L'allowlist ne verifiait que la cible. Un op forge (le rejeu au demarrage en rejoue depuis
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
    // Ce que l'Annuler de fixit.pagefile_ssd remet : une taille geree par le systeme sur un lecteur nomme.
    [InlineData(@"D:\pagefile.sys 0 0")]
    [InlineData(@"D:\pagefile.sys")]
    [InlineData("C:\\pagefile.sys 0 0\nD:\\pagefile.sys 0 0")]
    public void PagingFiles_accepte_les_deux_valeurs_que_le_levier_ecrit(string valeur) =>
        _allow.Validate(PagingFiles(valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(@"C:\pagefile.sys 16 16")]
    [InlineData(@"C:\pagefile.sys 4096 8192")]
    [InlineData(@"C:\Windows\evil.sys 4096 4096")]
    [InlineData("?:\\pagefile.sys\nC:\\pagefile.sys 16 16")]
    [InlineData(@"D:\pagefile.sys 0 16")]
    [InlineData(@"DD:\pagefile.sys 0 0")]
    [InlineData(@"D:\dossier\pagefile.sys 0 0")]
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

    // --- Registre : la liste noire dure refuse toujours (Defender/Update/BITS/bam/stratégies) ---

    [Theory]
    [InlineData(@"SYSTEM\CurrentControlSet\Services\WinDefend")]            // Defender
    [InlineData(@"SOFTWARE\Microsoft\Windows Defender")]                   // Defender
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate")]     // Windows Update
    [InlineData(@"SYSTEM\CurrentControlSet\Services\wuauserv")]            // service Windows Update
    [InlineData(@"SYSTEM\CurrentControlSet\Services\BITS")]               // BITS
    [InlineData(@"SYSTEM\CurrentControlSet\Services\bam")]                // bam
    [InlineData(@"SYSTEM\CurrentControlSet\Services\bam\State")]          // sous-arbre bam
    [InlineData(@"SOFTWARE\Policies\Microsoft\Windows\Safer")]            // stratégie de sécurité
    [InlineData(@"SECURITY\Policy")]                                       // zone de ruche des stratégies de sécurité
    public void Registry_blacklisted_subtree_is_denied(string path)
    {
        _allow.Validate(Reg(path)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // C'est la liste noire qui refuse ceux-là, pas le refus par défaut feuille exacte qui les
    // refuserait de toute façon. Les deux rideaux rendent NotAllowlisted : vérifier le code seul ne
    // les distingue pas, et retirer les entrées Defender de BlacklistedSubtrees laissait toute la
    // suite verte (vérifié par mutation). La liste noire existe pour le cas que le code nomme :
    // « même si une règle d'autorisation future la recouvre ». L'assertion doit donc nommer la
    // règle qui a joué, sinon le second rideau est décoratif.
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

    // La theorie ci-dessus liste les sous-arbres a la main : une liste ecrite a la main en oublie
    // (wuauserv, bam, Safer, SECURITY, SAM, le WindowsUpdate de CurrentVersion), et elle n'exerce que la
    // CLE EXACTE, jamais un enfant. Mesure par mutation : remplacer la branche
    // `StartsWith(subtree + separateur)` d'IsUnderSubtree par `false` laisserait la theorie verte, la
    // liste noire ne couvrant plus que des cles precises au lieu de sous-ARBRES. C'est pourtant le cas
    // qu'elle existe pour couvrir : une regle d'autorisation future qui viserait une feuille sous Defender.
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

    // --- Registre : tout ce qui n'est pas explicitement autorisé est refusé (refus par défaut) ---

    [Theory]
    [InlineData(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run")]   // démarrage automatique : pas à nous
    [InlineData(@"SOFTWARE\Random\Vendor\Key")]
    [InlineData(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers")] // chemin autorisé, mais mauvais nom de valeur
    public void Registry_unallowed_target_is_denied(string path)
    {
        // Dans le troisième cas le chemin est autorisé mais le nom de valeur « x » n'est pas la
        // feuille autorisée exacte : feuille exacte veut dire que le couple (chemin, nom) compte.
        _allow.Validate(Reg(path)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Registry_allowed_path_but_wrong_value_name_is_denied_leaf_exact()
    {
        // Le chemin GraphicsDrivers n'est autorisé que pour HwSchMode ; un autre nom de valeur
        // sous le même chemin doit être refusé (feuille exacte, pas sous-arbre).
        var op = new SetRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
            "SomethingElse", RegistryValueType.DWord, "1");
        _allow.Validate(op).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void Registry_allowed_leaf_but_wrong_hive_is_denied()
    {
        // HwSchMode est une feuille HKLM ; le même couple (chemin, nom) sous HKCU n'est pas autorisé.
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

        // ReflexEnabled n'est plus autorisée, aucun levier ne l'écrivait.
        var retiree = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SOFTWARE\NVIDIA Corporation\Global\NVTweak", "ReflexEnabled");
        _allow.Validate(retiree).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

        var denied = new DeleteRegistryValueOp(
            RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Services\WinDefend", "Start");
        _allow.Validate(denied).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- Plan d'alimentation : seulement les GUID Windows connus ---

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

    // --- Le passage au firmware. L'op ne porte qu'un délai : cette borne est toute
    //     l'autorisation, et la tester est donc tout l'enjeu. ---

    [Fact]
    public void Firmware_reboot_inside_the_band_passes()
    {
        _allow.Validate(new RebootToFirmwareSetupOp(10)).IsOk.Should().BeTrue();
        _allow.Validate(new RebootToFirmwareSetupOp(300)).IsOk.Should().BeTrue();
    }

    // Un redémarrage immédiat, à la demande de n'importe quel programme du compte (ou d'un JSON où le délai manque, qui
    // vaut 0), coupait la partie sans laisser le temps de l'annuler. L'App demande 10 s ; c'est le plancher.
    [Fact]
    public void Un_redemarrage_immediat_vers_le_firmware_est_refuse()
    {
        _allow.Validate(new RebootToFirmwareSetupOp(0)).IsOk.Should().BeFalse();
        _allow.Validate(new RebootToFirmwareSetupOp(9)).IsOk.Should().BeFalse();
        _allow.Validate(PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new RebootToFirmwareSetupOp(0)).Value!).Value!)
            .IsOk.Should().BeFalse();
    }

    [Fact]
    public void Firmware_reboot_outside_the_band_is_denied()
    {
        // Passé quelques minutes, le joueur est passé à autre chose et ne relie plus le redémarrage
        // à un clic : un redémarrage sans cause visible est ce qui détruit la confiance.
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
        // Refuser d'annuler ne pourrait que nuire : cela laisserait armé un redémarrage que le
        // joueur a demandé d'annuler. Il n'y a rien à valider ni à filtrer.
        _allow.Validate(new CancelPendingFirmwareBootOp()).IsOk.Should().BeTrue();
    }

    // --- Contrôle de service : seulement AMD3DVCacheSvc avec les actions autorisées ---

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

    // --- Le varstore CONSTRUCTEUR, en lecture seule -------------------------------------

    [Fact]
    public void ReadFirmwareVariable_the_vendor_setup_varstore_is_readable()
    {
        // Mesure sur la carte de reference (MSI PRO B760-P, AMI 1.H0) : 5081 octets lus sous ce GUID.
        // Le GUID etait un candidat que ce depot ne peut citer depuis aucune specification ; c'est
        // la carte qui a tranche, pas une supposition.
        _allow.Validate(new ReadFirmwareVariableOp("Setup", OpAllowlist.AmiSetupVarstoreGuid))
              .IsOk.Should().BeTrue();
    }

    [Fact]
    public void ReadFirmwareVariable_the_factory_defaults_store_is_readable_under_its_own_guid_only()
    {
        // GUID nommé AmiStandardDefaultsVariable par la liste de UEFITool (common/guids.csv), et relevé le 17/10/2026 sur la
        // carte de référence : 11 356 octets sous ce GUID. Sous le GUID de Setup, le même nom reste fermé.
        _allow.Validate(new ReadFirmwareVariableOp("StdDefaults", OpAllowlist.AmiStandardDefaultsGuid))
              .IsOk.Should().BeTrue();
        _allow.Validate(new ReadFirmwareVariableOp("StdDefaults", OpAllowlist.AmiSetupVarstoreGuid))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        _allow.Validate(new ReadFirmwareVariableOp("Setup", OpAllowlist.AmiStandardDefaultsGuid))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Fact]
    public void ReadFirmwareVariable_every_variable_an_AMD_board_table_reads_is_allowlisted_and_nothing_more()
    {
        // Une table BIOS AMD qui entre nomme ses variables ; si le helper ne les lit pas, la lecture rendrait « non lu »
        // en silence. Ce test rougit tant que la liste blanche n'a pas le nom exact sous le GUID exact.
        foreach (var v in DrDxt.Core.TablesBiosAmd.Connues.SelectMany(t => t.Variables))
            _allow.Validate(new ReadFirmwareVariableOp(v.Nom, v.Guid)).IsOk.Should().BeTrue($"{v.Nom} {v.Guid}");
        // Les noms AMD jamais relevés sur une carte restent fermés, sous n'importe quel GUID (inventés ici).
        _allow.Validate(new ReadFirmwareVariableOp("AodSetup", "{0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d}"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
        _allow.Validate(new ReadFirmwareVariableOp("AmdSetup", "{7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d}"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
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

    // --- InstallPresentMonService est en liste blanche (borné au binaire embarqué) ---

    [Fact]
    public void InstallPresentMonService_is_allowed()
    {
        _allow.Validate(new InstallPresentMonServiceOp()).IsOk.Should().BeTrue();
    }

    // --- NVAPI / ADLX : validation structurelle (le bornage SafeBounds vient à part) ---

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

    // --- La couche élevée impose elle-même la bande de politique GpuSafeBounds du cœur
    // (refuser, pas borner). Elle ne délègue jamais les bornes de valeur au client. Bande = [-200, +100].

    [Theory]
    [InlineData(100)]   // borne haute : acceptée
    [InlineData(-200)]  // borne basse : acceptée
    [InlineData(0)]     // origine : acceptée
    [InlineData(-120)]  // un undervolt ordinaire : accepté
    public void NvApiClockOffset_core_within_policy_band_is_accepted(int coreOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, coreOffsetMhz, 0)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(101)]   // un cran au-dessus de la bande
    [InlineData(500)]   // l'ancien rail dur (±500), désormais refusé à la frontière
    [InlineData(-201)]  // un cran sous la bande
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)] // pas de débordement Math.Abs
    public void NvApiClockOffset_core_outside_policy_band_is_rejected(int coreOffsetMhz) =>
        _allow.Validate(new ApplyNvApiClockOffsetOp(0, coreOffsetMhz, 0))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Theory]
    [InlineData(500)]   // borne haute mémoire : acceptée
    [InlineData(-500)]  // borne basse mémoire : acceptée
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
        // 0x10835002 = l'identifiant FRL_FPS corrigé.
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, "Base Profile")).IsOk.Should().BeTrue();
        _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, ""))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    [Theory]
    // L'ensemble corrigé d'identifiants DRS feuille exacte (recette §C) passe en entier.
    [InlineData(0x007BA09Eu)] // PRERENDERLIMIT (inchangé)
    [InlineData(0x00A879CFu)] // VSYNCMODE (corrigé, l'ancien était 0x00fc00fa)
    [InlineData(0x10835002u)] // FRL_FPS (corrigé, l'ancien était 0x10835000)
    public void NvApiDrsSetting_corrected_ids_pass(uint settingId) =>
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, ValeurNormale(settingId), "Base Profile")).IsOk.Should().BeTrue();

    [Theory]
    // HISTORIQUE, parce qu'il explique la forme de la regle : le ProfileName porte par l'op etait
    // DECORATIF. « cod.exe » et « Base Profile » produisaient la meme ecriture machine entiere, et
    // l'allowlist laissait passer les deux tant que la chaine n'etait pas vide -- une escalade de
    // portee silencieuse, ou un appelant demandant un changement par jeu en obtenait un pour toute
    // la machine sans qu'aucune couche ne le dise.
    //
    // L'allowlist a d'abord refuse toute portee que l'executeur ne savait pas honorer. Depuis,
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
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, profile))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void NvApiDrsSetting_the_base_profile_is_the_machine_wide_scope()
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, "Base Profile"))
              .IsOk.Should().BeTrue();

    [Theory]
    // La portee par JEU, desormais honorable, donc desormais autorisee. Un chemin complet
    // est la seule forme que NVIDIA accepte : sur un nom nu le driver rend
    // NVAPI_EXECUTABLE_PATH_IS_AMBIGUOUS, et l'ecriture echouerait au bord natif apres avoir
    // laisse croire qu'elle etait permise.
    [InlineData(@"C:\Program Files\Call of Duty\cod.exe")]
    [InlineData(@"D:\Games\game.EXE")]     // l'extension se compare sans la casse
    [InlineData("C:/Games/game.exe")]      // NVIDIA accepte les deux separateurs
    public void NvApiDrsSetting_a_full_executable_path_is_now_an_allowed_scope(string profile)
        => _allow.Validate(new ApplyNvApiDrsSettingOp(0x10835002u, 141u, profile))
              .IsOk.Should().BeTrue();

    [Fact]
    public void NvApiDrsSetting_a_per_application_scope_does_not_widen_the_setting_id_set()
    {
        // La garde qui compte : ouvrir la PORTEE ne doit pas ouvrir le reste. Un identifiant
        // forge reste refuse, quelle que soit la finesse de la portee demandee.
        _allow.Validate(new ApplyNvApiDrsSettingOp(0xDEADBEEFu, 3u, @"C:\Games\game.exe"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // --- La SUPPRESSION au profil d'un jeu, plus etroite que l'ecriture ---

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
    // Les anciens identifiants erronés doivent être refusés : la correction a bien remplacé les
    // valeurs dans l'ensemble fermé, elle ne s'est pas ajoutée à côté.
    [InlineData(0x00fc00fau)] // ancien VSYNC
    [InlineData(0x10835000u)] // ancien FRL
    [InlineData(0xDEADBEEFu)] // forged
    public void NvApiDrsSetting_wrong_or_forged_id_is_denied_leaf_exact(uint settingId) =>
        // « Base Profile » à dessein : avec un autre nom, le refus viendrait du contrôle de portée
        // et ce test passerait sans jamais exercer l'ensemble d'identifiants qu'il doit figer.
        _allow.Validate(new ApplyNvApiDrsSettingOp(settingId, ValeurNormale(settingId), "Base Profile"))
              .Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // --- EnableSystemRestore : feuille exacte, lecteur système seulement ---

    [Fact]
    public void EnableSystemRestore_system_drive_passes()
    {
        // Le disque système tel que l'allowlist le lit (par l'API, pas par %SystemDrive%, qu'un autre test
        // remplace un instant par un piège en parallèle).
        var sysDrive = OpAllowlist.ResolveSystemDriveLetter().TrimEnd('\\');
        _allow.Validate(new EnableSystemRestoreOp(sysDrive)).IsOk.Should().BeTrue();
        // Les formes avec barre finale ou lettre nue se normalisent vers la même feuille.
        _allow.Validate(new EnableSystemRestoreOp(sysDrive + "\\")).IsOk.Should().BeTrue();
    }

    [Theory]
    [InlineData("D:")]          // un lecteur de données : DrDXT n'y active jamais la restauration système
    [InlineData("Z:")]
    [InlineData(@"C:\Windows")] // un chemin, pas une lettre de lecteur
    [InlineData("")]
    [InlineData("notadrive")]
    public void EnableSystemRestore_non_system_or_invalid_drive_is_denied(string drive)
    {
        // (D:/Z: supposent que le lecteur système est C: en CI, vrai sur les runners standard.)
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

    // --- Point de restauration et Ping passent (aucune cible à contraindre) ---

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

    // --- Refus par défaut : une op non modélisée ou nulle est refusée ---

    [Fact]
    public void Null_op_is_denied()
    {
        _allow.Validate(null!).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // Le seul chemin restant pour dériver hors de Core : le constructeur de copie, protégé.
    private sealed record RogueOp() : PrivilegedOp(new PingOp());

    [Fact]
    public void Unknown_op_subtype_is_denied_default_deny()
    {
        _allow.Validate(new RogueOp()).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);
    }

    // Les deux heures d'activité du réglage utilisateur, une heure de 0 à 23, rien d'autre de Windows Update.
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

    // Le mode de boost secteur (PERFBOOSTMODE). Microsoft Learn ne documente que 0 a 4 ; powercfg en enumere 7
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

    // L'economie d'energie de la carte sans fil, valeurs documentees seulement, plan non vide.
    [Theory]
    [InlineData(0u)]
    [InlineData(3u)]
    public void Economie_sans_fil_accepte_une_valeur_documentee(uint valeur) =>
        _allow.Validate(new SetWirelessPowerSavingOp(KnownPowerPlans.Balanced, valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(4u)]
    [InlineData(uint.MaxValue)]
    public void Economie_sans_fil_refuse_une_valeur_forgee(uint valeur) =>
        _allow.Validate(new SetWirelessPowerSavingOp(KnownPowerPlans.Balanced, valeur)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    [Fact]
    public void Economie_sans_fil_refuse_un_plan_vide() =>
        _allow.Validate(new SetWirelessPowerSavingOp(Guid.Empty, 0)).IsOk.Should().BeFalse();

    // La limite de puissance du processeur ne bouge que par PL1, dans la PL2 de la carte, sans verrou.
    private const ulong Pl2_219W = 1752UL << 32;
    private const ulong Origine65W = Pl2_219W | (1UL << 47) | (1UL << 15) | 520UL;

    [Fact]
    public void La_limite_processeur_accepte_une_PL1_montee_jusqu_a_la_PL2_de_la_carte()
    {
        _allow.Validate(new SetCpuPackagePowerLimitOp((Origine65W & ~0x7FFFUL) | 1752UL, Origine65W)).IsOk.Should().BeTrue();
        _allow.Validate(new SetCpuPackagePowerLimitOp(Origine65W, (Origine65W & ~0x7FFFUL) | 1752UL)).IsOk.Should().BeTrue("le retour à l'origine passe aussi");
    }

    [Theory]
    [InlineData((Origine65W & ~0x7FFFUL) | 1753UL)]            // PL1 au-dessus de PL2
    [InlineData(Origine65W | (1UL << 63))]                      // verrou posé
    [InlineData((Origine65W & ~0x7FFFUL) | 1752UL ^ (1UL << 40))] // PL2 touchée
    [InlineData(Origine65W & ~0x7FFFUL)]                        // PL1 à zéro
    public void La_limite_processeur_refuse_tout_ce_qui_n_est_pas_PL1_dans_la_PL2(ulong valeur) =>
        _allow.Validate(new SetCpuPackagePowerLimitOp(valeur, Origine65W)).Error!.Value.Code.Should().Be(ErrorCodes.NotAllowlisted);

    // Une valeur qu'un réglage normal porte, par identifiant (la liste blanche borne aussi la valeur).
    private static uint ValeurNormale(uint id) => id switch { 0x00A879CFu => 0x47814940u, 0x10835002u => 141u, _ => 1u };
}
