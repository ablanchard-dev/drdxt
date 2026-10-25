using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// La connaissance BIOS reutilisable, pas le parcours de clics : ici se construit `BiosQualification`.
///
/// ══ pourquoi ce fichier n'est pas `guides.json` ══
/// `data/guides.json` repond a « ou le joueur doit-il cliquer ? ». C'est un frontend, indexe par
/// constructeur x generation d'interface, et il ne porte ni defaut, ni risque, ni recuperation, ni
/// mecanisme de lecture. Cette matrice-ci repond a « que sait-on de ce reglage, sur cette carte,
/// avec ce firmware, et a-t-on le droit d'y toucher ? » : la connaissance qui alimentera un jour
/// une ecriture qualifiee. Le PDF et le QR sont un affichage de cette connaissance, pas l'inverse.
///
/// ══ ce qui est mesure, et ce qui ne l'est pas ══
/// Mesure sur la machine de reference le 08/09/2026 :
///   `Win32_BaseBoard`  Micro-Star International Co., Ltd. : PRO B760-P WIFI DDR4 (MS-7D98), rev 1.0
///   `Win32_BIOS`       American Megatrends International : 1.H0, publie le 09/05/2026, SMBIOS 3.6
///
/// Non etabli, et donc laisse a `null` ou a `Unknown` plutot que devine :
///   · la voie de recuperation de cette carte. Quatre tentatives pour obtenir son manuel exact ont
///     echoue (404 sur l'URL deduite, 403 sur la page de support, la recherche ne rend que les
///     manuels de cartes voisines, et la fiche technique est un PDF non extractible ici).
///     « JBAT1 en position 2-3, comme sur les autres cartes MSI B760 » est exactement
///     l'inference que ce produit s'interdit : une carte voisine n'est pas cette carte.
///   · (etablis, eux : les valeurs possibles et le defaut, releves le 24/09/2026 dans l'IFR du BIOS
///     officiel, avec l'emplacement NVRAM de chaque reglage ; voir `SourceIfr`.)
///   · la valeur courante ne vit pas ici : elle se lit a l'execution (`NvramBiosSettingsReader`),
///     jamais figee dans une table. `MSI_BiosSetting` reste inutilise : on n'appelle pas au hasard
///     une methode ACPI non documentee.
///
/// Consequence assumee : `EcritureAutomatiqueAutorisee` est faux sur toutes ces entrees, et
/// `CeQuiManque` dit laquelle des quatre conditions manque. C'est le modele qui fonctionne, pas un
/// modele vide.
/// </summary>
public static class BiosQualificationMatrix
{
    /// <summary>Le chemin de menu vient de la bibliotheque de guides, ou il est marque
    /// `isVerified: false` : c'est le meilleur connu pour MSI Click BIOS 5, jamais confirme au banc
    /// sur cette carte. La matrice le recopie avec cette reserve plutot que de le taire.</summary>
    private const string SourceGuides = "DrDXT data/guides.json: MSI / Click BIOS 5, isVerified: false";

    private const string SourceSmbios = "mesure locale 08/09/2026 : Win32_BaseBoard + Win32_BIOS";

    /// <summary>
    /// Les entrees connues pour une carte et un firmware donnes. Liste vide quand la combinaison
    /// n'est pas dans la matrice — jamais une entree generique de repli.
    ///
    /// C'est le point de tout le type : une entree valable sur « MSI carte A / BIOS X » ne
    /// devient pas valable sur « MSI carte A / BIOS Y ». Rendre quelque chose « d'approchant »
    /// serait la faute que la matrice existe pour empecher.
    /// </summary>
    public static IReadOnlyList<BiosQualification> Pour(string? carte, string? firmware)
    {
        if (carte is null || firmware is null) return [];

        return carte.Contains("MS-7D98", StringComparison.OrdinalIgnoreCase)
               && firmware.Equals("1.H0", StringComparison.OrdinalIgnoreCase)
            ? ProB760PWifiDdr4_1H0
            : [];
    }

    private static readonly IReadOnlyList<BiosQualification> ProB760PWifiDdr4_1H0 =
    [
        Entree(
            reglage: "XMP",
            cible: "Profil 1",
            cheminMenu: "Advanced Mode (F7) > OC > Extreme Memory Profile (XMP)",
            // XMP touche l'entrainement memoire : un echec se voit par un ecran noir, et c'est
            // un resultat normal de la manœuvre, pas une panne. Le joueur doit le savoir avant.
            noPost: NoPostRisk.CanPreventPost,
            // Les six valeurs de l'IFR (tools/ifr/ifr2matrice.py). Avec trois seulement, un joueur en
            // « User Profile 1 » ferait jeter toute la lecture du BIOS.
            nvram: Setup(0xE65, Defaut: 0, (0, "Disabled"), (1, "Profile 1"), (2, "Profile 2"),
                (3, "Profile 3"), (4, "User Profile 1"), (5, "User Profile 2"))),

        Entree(
            reglage: "Re-Size BAR",
            cible: "Enabled",
            cheminMenu: "Advanced Mode (F7) > Settings > Advanced > PCIe/PCI Sub-system Settings "
                        + "> Re-Size BAR Support",
            noPost: NoPostRisk.CanPreventPost,
            nvram: Setup(0x10DC, Defaut: 1, (0, "Disabled"), (1, "Enabled"))),

        Entree(
            reglage: "Above 4G Decoding",
            cible: "Enabled",
            cheminMenu: "Advanced Mode (F7) > Settings > Advanced > PCIe/PCI Sub-system Settings "
                        + "> Above 4G memory / Crypto Currency mining",
            noPost: NoPostRisk.CanPreventPost,
            nvram: Setup(0x10DB, Defaut: 1, (0, "Disabled"), (1, "Enabled"))),

        Entree(
            reglage: "CSM",
            cible: "Disabled",
            cheminMenu: "Advanced Mode (F7) > Settings > Advanced > Windows OS Configuration "
                        + "> BIOS CSM/UEFI Mode",
            // Il decide de la sequence de demarrage : le materiel s'initialise, mais la machine
            // peut ne plus trouver de quoi demarrer.
            noPost: NoPostRisk.CanPreventPost,
            nvram: Setup(0x10C6, Defaut: 1, (0, "CSM"), (1, "UEFI"))),

        // La frequence de la memoire, en lecture. Question "DRAM Frequency" (QuestionId
        // 0x2967, VarOffset 0xD39, 230 valeurs, defaut Auto) de l'IFR 1.H0. Le BIOS en declare quatre
        // autres (0xD38, 0xD3A-0xD3C) que des conditions masquent selon le processeur et la memoire ;
        // 0xD39 est celle qui a rendu 84 = "3800 G1" sur la machine de reference, recoupe avec Windows (3800 MT/s).
        Lecture("DRAM Frequency", Setup(0xD39, Defaut: 0,
            (0, "Auto"), (3, "800 G1 (6x1.33x100.00)"), (4, "800 G1 (8x1.00x100.00)"), (5, "900 G1 (9x1.00x100.00)"),
            (6, "933 G1 (7x1.33x100.00)"), (7, "1000 G1 (10x1.00x100.00)"), (8, "1066 G1 (8x1.33x100.00)"),
            (9, "1100 G1 (11x1.00x100.00)"), (10, "1200 G1 (9x1.33x100.00)"), (11, "1200 G1 (12x1.00x100.00)"),
            (12, "1200 G2 (12x1.00x100.00)"), (13, "1300 G1 (13x1.00x100.00)"), (14, "1333 G1 (10x1.33x100.00)"),
            (15, "1400 G1 (14x1.00x100.00)"), (16, "1400 G2 (14x1.00x100.00)"), (17, "1466 G1 (11x1.33x100.00)"),
            (18, "1500 G1 (15x1.00x100.00)"), (19, "1600 G1 (12x1.33x100.00)"), (20, "1600 G2 (12x1.33x100.00)"),
            (21, "1600 G1 (16x1.00x100.00)"), (22, "1600 G2 (16x1.00x100.00)"), (23, "1700 G1 (17x1.00x100.00)"),
            (24, "1733 G1 (13x1.33x100.00)"), (25, "1800 G1 (18x1.00x100.00)"), (26, "1800 G2 (18x1.00x100.00)"),
            (27, "1866 G1 (14x1.33x100.00)"), (28, "1866 G2 (14x1.33x100.00)"), (29, "1900 G1 (19x1.00x100.00)"),
            (30, "2000 G1 (15x1.33x100.00)"), (31, "2000 G1 (20x1.00x100.00)"), (32, "2000 G2 (20x1.00x100.00)"),
            (33, "2100 G1 (21x1.00x100.00)"), (34, "2133 G1 (16x1.33x100.00)"), (35, "2133 G2 (16x1.33x100.00)"),
            (36, "2200 G1 (22x1.00x100.00)"), (37, "2200 G2 (22x1.00x100.00)"), (38, "2266 G1 (17x1.33x100.00)"),
            (39, "2300 G1 (23x1.00x100.00)"), (40, "2400 G1 (18x1.33x100.00)"), (41, "2400 G2 (18x1.33x100.00)"),
            (42, "2400 G1 (24x1.00x100.00)"), (43, "2400 G2 (24x1.00x100.00)"), (44, "2400 G4 (24x1.00x100.00)"),
            (45, "2500 G1 (25x1.00x100.00)"), (46, "2533 G1 (19x1.33x100.00)"), (47, "2600 G1 (26x1.00x100.00)"),
            (48, "2600 G2 (26x1.00x100.00)"), (49, "2666 G1 (20x1.33x100.00)"), (50, "2666 G2 (20x1.33x100.00)"),
            (51, "2700 G1 (27x1.00x100.00)"), (52, "2800 G1 (21x1.33x100.00)"), (53, "2800 G1 (28x1.00x100.00)"),
            (54, "2800 G2 (28x1.00x100.00)"), (55, "2800 G4 (28x1.00x100.00)"), (56, "2900 G1 (29x1.00x100.00)"),
            (57, "2933 G1 (22x1.33x100.00)"), (58, "2933 G2 (22x1.33x100.00)"), (59, "3000 G1 (30x1.00x100.00)"),
            (60, "3000 G2 (30x1.00x100.00)"), (61, "3066 G1 (23x1.33x100.00)"), (62, "3100 G1 (31x1.00x100.00)"),
            (63, "3200 G1 (24x1.33x100.00)"), (64, "3200 G2 (24x1.33x100.00)"), (65, "3200 G4 (24x1.33x100.00)"),
            (66, "3200 G1 (32x1.00x100.00)"), (67, "3200 G2 (32x1.00x100.00)"), (68, "3200 G4 (32x1.00x100.00)"),
            (69, "3300 G1 (33x1.00x100.00)"), (70, "3333 G1 (25x1.33x100.00)"), (71, "3400 G1 (34x1.00x100.00)"),
            (72, "3400 G2 (34x1.00x100.00)"), (73, "3466 G1 (26x1.33x100.00)"), (74, "3466 G2 (26x1.33x100.00)"),
            (75, "3500 G1 (35x1.00x100.00)"), (76, "3600 G1 (27x1.33x100.00)"), (77, "3600 G1 (36x1.00x100.00)"),
            (78, "3600 G2 (36x1.00x100.00)"), (79, "3600 G4 (36x1.00x100.00)"), (80, "3700 G1 (37x1.00x100.00)"),
            (81, "3733 G1 (28x1.33x100.00)"), (82, "3733 G2 (28x1.33x100.00)"), (83, "3733 G4 (28x1.33x100.00)"),
            (84, "3800 G1 (38x1.00x100.00)"), (85, "3800 G2 (38x1.00x100.00)"), (86, "3866 G1 (29x1.33x100.00)"),
            (87, "3900 G1 (39x1.00x100.00)"), (88, "4000 G1 (30x1.33x100.00)"), (89, "4000 G2 (30x1.33x100.00)"),
            (90, "4000 G1 (40x1.00x100.00)"), (91, "4000 G2 (40x1.00x100.00)"), (92, "4000 G4 (40x1.00x100.00)"),
            (93, "4100 G1 (41x1.00x100.00)"), (94, "4133 G1 (31x1.33x100.00)"), (95, "4200 G1 (42x1.00x100.00)"),
            (96, "4200 G2 (42x1.00x100.00)"), (97, "4266 G1 (32x1.33x100.00)"), (98, "4266 G2 (32x1.33x100.00)"),
            (99, "4266 G4 (32x1.33x100.00)"), (100, "4300 G1 (43x1.00x100.00)"), (101, "4400 G1 (33x1.33x100.00)"),
            (102, "4400 G1 (44x1.00x100.00)"), (103, "4400 G2 (44x1.00x100.00)"), (104, "4400 G4 (44x1.00x100.00)"),
            (105, "4500 G1 (45x1.00x100.00)"), (106, "4533 G1 (34x1.33x100.00)"), (107, "4533 G2 (34x1.33x100.00)"),
            (108, "4600 G1 (46x1.00x100.00)"), (109, "4600 G2 (46x1.00x100.00)"), (110, "4666 G1 (35x1.33x100.00)"),
            (111, "4700 G1 (47x1.00x100.00)"), (112, "4800 G1 (36x1.33x100.00)"), (113, "4800 G2 (36x1.33x100.00)"),
            (114, "4800 G4 (36x1.33x100.00)"), (115, "4800 G1 (48x1.00x100.00)"), (116, "4800 G2 (48x1.00x100.00)"),
            (117, "4800 G4 (48x1.00x100.00)"), (118, "4900 G1 (49x1.00x100.00)"), (119, "4933 G1 (37x1.33x100.00)"),
            (120, "5000 G1 (50x1.00x100.00)"), (121, "5000 G2 (50x1.00x100.00)"), (122, "5066 G1 (38x1.33x100.00)"),
            (123, "5066 G2 (38x1.33x100.00)"), (124, "5100 G1 (51x1.00x100.00)"), (125, "5200 G1 (39x1.33x100.00)"),
            (126, "5200 G1 (52x1.00x100.00)"), (127, "5200 G2 (52x1.00x100.00)"), (128, "5200 G4 (52x1.00x100.00)"),
            (129, "5300 G1 (53x1.00x100.00)"), (130, "5333 G1 (40x1.33x100.00)"), (131, "5333 G2 (40x1.33x100.00)"),
            (132, "5333 G4 (40x1.33x100.00)"), (133, "5400 G1 (54x1.00x100.00)"), (134, "5400 G2 (54x1.00x100.00)"),
            (135, "5466 G1 (41x1.33x100.00)"), (136, "5500 G1 (55x1.00x100.00)"), (137, "5600 G1 (42x1.33x100.00)"),
            (138, "5600 G2 (42x1.33x100.00)"), (139, "5600 G1 (56x1.00x100.00)"), (140, "5600 G2 (56x1.00x100.00)"),
            (141, "5600 G4 (56x1.00x100.00)"), (142, "5700 G1 (57x1.00x100.00)"), (143, "5733 G1 (43x1.33x100.00)"),
            (144, "5800 G1 (58x1.00x100.00)"), (145, "5800 G2 (58x1.00x100.00)"), (146, "5866 G1 (44x1.33x100.00)"),
            (147, "5866 G2 (44x1.33x100.00)"), (148, "5866 G4 (44x1.33x100.00)"), (149, "5900 G1 (59x1.00x100.00)"),
            (150, "6000 G1 (45x1.33x100.00)"), (151, "6000 G1 (60x1.00x100.00)"), (152, "6000 G2 (60x1.00x100.00)"),
            (153, "6000 G4 (60x1.00x100.00)"), (154, "6100 G1 (61x1.00x100.00)"), (155, "6133 G1 (46x1.33x100.00)"),
            (156, "6133 G2 (46x1.33x100.00)"), (157, "6200 G1 (62x1.00x100.00)"), (158, "6200 G2 (62x1.00x100.00)"),
            (159, "6266 G1 (47x1.33x100.00)"), (160, "6300 G1 (63x1.00x100.00)"), (161, "6400 G1 (48x1.33x100.00)"),
            (162, "6400 G2 (48x1.33x100.00)"), (163, "6400 G4 (48x1.33x100.00)"), (164, "6400 G1 (64x1.00x100.00)"),
            (165, "6400 G2 (64x1.00x100.00)"), (166, "6400 G4 (64x1.00x100.00)"), (167, "6500 G1 (65x1.00x100.00)"),
            (168, "6533 G1 (49x1.33x100.00)"), (169, "6600 G1 (66x1.00x100.00)"), (170, "6600 G2 (66x1.00x100.00)"),
            (171, "6666 G1 (50x1.33x100.00)"), (172, "6666 G2 (50x1.33x100.00)"), (173, "6800 G1 (51x1.33x100.00)"),
            (174, "6800 G2 (68x1.00x100.00)"), (175, "6800 G4 (68x1.00x100.00)"), (176, "6933 G1 (52x1.33x100.00)"),
            (177, "6933 G2 (52x1.33x100.00)"), (178, "6933 G4 (52x1.33x100.00)"), (179, "7000 G2 (70x1.00x100.00)"),
            (180, "7066 G1 (53x1.33x100.00)"), (181, "7200 G1 (54x1.33x100.00)"), (182, "7200 G2 (54x1.33x100.00)"),
            (183, "7200 G2 (72x1.00x100.00)"), (184, "7200 G4 (72x1.00x100.00)"), (185, "7333 G1 (55x1.33x100.00)"),
            (186, "7400 G2 (74x1.00x100.00)"), (187, "7466 G1 (56x1.33x100.00)"), (188, "7466 G2 (56x1.33x100.00)"),
            (189, "7466 G4 (56x1.33x100.00)"), (190, "7600 G1 (57x1.33x100.00)"), (191, "7600 G2 (76x1.00x100.00)"),
            (192, "7600 G4 (76x1.00x100.00)"), (193, "7733 G1 (58x1.33x100.00)"), (194, "7733 G2 (58x1.33x100.00)"),
            (195, "7800 G2 (78x1.00x100.00)"), (196, "7866 G1 (59x1.33x100.00)"), (197, "8000 G1 (60x1.33x100.00)"),
            (198, "8000 G2 (60x1.33x100.00)"), (199, "8000 G4 (60x1.33x100.00)"), (200, "8000 G2 (80x1.00x100.00)"),
            (201, "8000 G4 (80x1.00x100.00)"), (202, "8133 G1 (61x1.33x100.00)"), (203, "8200 G2 (82x1.00x100.00)"),
            (204, "8266 G1 (62x1.33x100.00)"), (205, "8266 G2 (62x1.33x100.00)"), (206, "8400 G1 (63x1.33x100.00)"),
            (207, "8400 G2 (84x1.00x100.00)"), (208, "8400 G4 (84x1.00x100.00)"), (209, "8533 G2 (64x1.33x100.00)"),
            (210, "8533 G4 (64x1.33x100.00)"), (211, "8600 G2 (86x1.00x100.00)"), (212, "8800 G2 (66x1.33x100.00)"),
            (213, "8800 G2 (88x1.00x100.00)"), (214, "8800 G4 (88x1.00x100.00)"), (215, "9000 G2 (90x1.00x100.00)"),
            (216, "9066 G2 (68x1.33x100.00)"), (217, "9066 G4 (68x1.33x100.00)"), (218, "9200 G2 (92x1.00x100.00)"),
            (219, "9200 G4 (92x1.00x100.00)"), (220, "9333 G2 (70x1.33x100.00)"), (221, "9400 G2 (94x1.00x100.00)"),
            (222, "9600 G2 (72x1.33x100.00)"), (223, "9600 G4 (72x1.33x100.00)"), (224, "9600 G2 (96x1.00x100.00)"),
            (225, "9600 G4 (96x1.00x100.00)"), (226, "9800 G2 (98x1.00x100.00)"), (227, "9866 G2 (74x1.33x100.00)"),
            (228, "10000 G2 (100x1.00x100.00)"), (229, "10000 G4 (100x1.00x100.00)"),
            (230, "10133 G2 (76x1.33x100.00)"), (231, "10133 G4 (76x1.33x100.00)"))),

        // Le bloc processeur, en lecture. Aucune cible : ce sont des constats, pas des
        // recommandations (ce que « Auto » applique sur cette carte n'est pas dans l'IFR).
        Lecture("Intel C-State", Cpu(0x14, Defaut: 2, (0, "Disabled"), (1, "Enabled"), (2, "Auto"))),
        Lecture("Package C State Limit", Cpu(0x4B, Defaut: 255, (0, "C0"), (1, "C2"), (2, "C3"), (3, "C6"),
            // L'IFR declare cette question deux fois sur le meme octet ; la seconde
            // ajoute 254 « Cpu Default ». Sans elle, cette valeur jetterait toute la lecture du BIOS.
            (4, "C7"), (5, "C7s"), (6, "C8"), (7, "C9"), (8, "C10"), (254, "Cpu Default"), (255, "Auto"))),
        Lecture("EIST", Cpu(0x9, Defaut: 1, (0, "Disabled"), (1, "Enabled"))),
        // Le reglage MSI qui decide des limites de puissance quand PL1/PL2 sont sur Auto.
        // Lu sur la machine de reference : 1 « Intel Default Settings », et le paquet tient 66,5 W en jeu (Base Power du
        // 14700F : 65 W). Une declaration unique dans l'IFR 1.H0 (tools/ifr/ifr2matrice.py). Lecture
        // seule, aucune cible : Intel et MSI recommandent de rester sur Intel Default Settings.
        Lecture("CPU Cooler Tuning", Setup(0xF88, Defaut: 1,
            (1, "Intel Default Settings"), (2, "MSI Performance Settings"), (3, "MSI Unlimited Settings"))),
        // Ce qui gouverne le temps du BIOS a l'allumage (Kernel-Boot 30, lu par l'audit
        // DEMARRAGE). Une declaration chacun dans l'IFR 1.H0. Sur la machine de reference : 0 et 0, et 11,2 a 11,8 s de BIOS
        // sauf l'allumage qui a suivi son reglage memoire (33,5 s, entrainement unique). Lecture seule :
        // aucune source ne chiffre ce que « MSI Fast Boot » ferait gagner, et il coupe la detection USB.
        Lecture("Memory Fast Boot", Setup(0xE67, Defaut: 0,
            (0, "Auto"), (1, "Disabled"), (2, "Enabled"), (3, "No Training"), (4, "Slow Training"))),
        Lecture("MSI Fast Boot", Setup(0xC60, Defaut: 0, (0, "Disabled"), (1, "Enabled"))),
        Lecture("Intel Turbo Boost", Cpu(0x16, Defaut: 1, (0, "Disabled"), (1, "Enabled"))),
        Lecture("Long Duration Power Limit (PL1)", CpuWatts(0x17)),
        Lecture("Short Duration Power Limit (PL2)", CpuWatts(0x1E)),
        // Trois réglages qui brident le processeur, relevés sur la machine de référence (IFR 1.H0, une
        // déclaration chacun) : la limite de courant (160 A mesurés : le processeur tombe de 4,9 à 3,9 GHz),
        // le Lite Load (tension ; Mode 9 = 80, relevé le 11/10) et la protection contre les excursions de courant (CEP).
        Lecture("CPU Current Limit", new NvramLocation("Setup", GuidsUefi.AmiSetup, VarSize: 0x13D9, 0xF29,
            new Dictionary<byte, string>(), Default: 0, Taille: 2, Min: 0, Max: 512, Unite: "A", ZeroVautAuto: true)),
        Lecture("CPU Lite Load Control", Setup(0xF62, Defaut: 0, (0, "Normal"), (1, "Advanced"), (2, "Intel Default"))),
        Lecture("CPU Lite Load", Setup(0xF63, Defaut: 0, (0, "Auto"), (1, "Mode 1"), (10, "Mode 2"), (20, "Mode 3"),
            (30, "Mode 4"), (40, "Mode 5"), (50, "Mode 6"), (60, "Mode 7"), (70, "Mode 8"), (80, "Mode 9"), (90, "Mode 10"),
            (100, "Mode 11"), (110, "Mode 12"), (120, "Mode 13"), (130, "Mode 14"), (140, "Mode 15"), (150, "Mode 16"),
            (160, "Mode 17"), (170, "Mode 18"), (180, "Mode 19"), (190, "Mode 20"), (200, "Mode 21"), (210, "Mode 22"),
            (220, "Mode 23"))),
        Lecture("IA CEP Support", Setup(0xFBC, Defaut: 0, (0, "Auto"), (1, "Disabled"), (2, "Enabled"))),
        // La courbe du ventilateur processeur (IFR 1.H0, Hardware Monitor : « CPU Fan1 Smart Fan Control », puis
        // « CPU Fan1 level N Temperature » et « CPU Fan1 level N Fan Speed », un octet chacun, Min 0, Max 100).
        // Relevée sur la machine de référence le 11/10 (40/62/80/92 °C pour 35/55/70/100 %) après qu'un ventilateur
        // unique n'a pas tenu 160 W : lue pour dater un changement de courbe et le juger sur les parties.
        Lecture("CPU Fan1 Smart Fan Control", Setup(0x1284, Defaut: 1, (0, "Disabled"), (1, "Enabled"))),
        Lecture("CPU Fan1 level 1 Temperature", SetupNombre(0x128A, Defaut: 0, Unite: "°C")),
        Lecture("CPU Fan1 level 2 Temperature", SetupNombre(0x128B, Defaut: 45, Unite: "°C")),
        Lecture("CPU Fan1 level 3 Temperature", SetupNombre(0x128C, Defaut: 65, Unite: "°C")),
        Lecture("CPU Fan1 level 4 Temperature", SetupNombre(0x128D, Defaut: 80, Unite: "°C")),
        Lecture("CPU Fan1 level 1 Fan Speed", SetupNombre(0x128E, Defaut: 20, Unite: "%")),
        Lecture("CPU Fan1 level 2 Fan Speed", SetupNombre(0x128F, Defaut: 35, Unite: "%")),
        Lecture("CPU Fan1 level 3 Fan Speed", SetupNombre(0x1290, Defaut: 70, Unite: "%")),
        Lecture("CPU Fan1 level 4 Fan Speed", SetupNombre(0x1291, Defaut: 100, Unite: "%")),
    ];

    private static NvramLocation Cpu(int offset, byte Defaut, params (byte Valeur, string Libelle)[] valeurs) =>
        new("CpuSetup", GuidsUefi.IntelCpuSetup, VarSize: 0x3C1, offset,
            valeurs.ToDictionary(v => v.Valeur, v => v.Libelle), Defaut);

    // IFR : « Max: 4096W / Min: 1W », Size 32, defaut 0 (Auto).
    private static NvramLocation CpuWatts(int offset) =>
        new("CpuSetup", GuidsUefi.IntelCpuSetup, VarSize: 0x3C1, offset,
            new Dictionary<byte, string>(), Default: 0, Taille: 4, Min: 0, Max: 4096, Unite: "W", ZeroVautAuto: true);

    private static BiosQualification Lecture(string reglage, NvramLocation nvram) =>
        Entree(reglage, cible: Docteur.T("BQ.LectureSeule"), cheminMenu: Docteur.T("BQ.NonReleve"),
            noPost: NoPostRisk.NoPostRiskUnknown, nvram);

    // Releve dans l'IFR du BIOS officiel 1.H0 (download.msi.com/bos_exe/mb/7D98v1H.zip,
    // E7D98IMS.1H0), module Setup 899407D7-99FE-43D8-9A21-79EC328CAC21, par IFRExtractor-RS 1.6.1.
    // VarStore « Setup », GUID ec87d643-eba4-4bb5-a1e5-3f3e36b20da9, taille 0x13D9. Les defauts
    // sont ceux du magasin StdDefaults de la meme image. Lu sur la machine de reference le meme
    // jour : taille 5081 octets, identique a l'IFR.
    private const string SourceIfr = "IFR du BIOS officiel MSI 1.H0 (7D98v1H.zip), IFRExtractor-RS 1.6.1, 24/09/2026";

    private static NvramLocation Setup(int offset, byte Defaut, params (byte Valeur, string Libelle)[] valeurs) =>
        new("Setup", GuidsUefi.AmiSetup, VarSize: 0x13D9, offset,
            valeurs.ToDictionary(v => v.Valeur, v => v.Libelle), Defaut);

    // Un réglage numérique d'un octet de la variable Setup (IFR : Min 0, Max 100, Step 1), avec son unité.
    private static NvramLocation SetupNombre(int offset, byte Defaut, string Unite) =>
        new("Setup", GuidsUefi.AmiSetup, VarSize: 0x13D9, offset,
            new Dictionary<byte, string>(), Defaut, Taille: 1, Min: 0, Max: 100, Unite: Unite);

    /// <summary>Toutes les entrees de cette carte partagent le meme etat de connaissance : chemin
    /// connu mais non verifie, valeurs et defaut releves dans l'IFR, valeur courante lue a l'execution seulement,
    /// recuperation inconnue. Le fabriquer en un seul endroit evite qu'une entree derive vers un
    /// optimisme que rien ne soutient.</summary>
    private static BiosQualification Entree(
        string reglage, string cible, string cheminMenu, NoPostRisk noPost, NvramLocation nvram) =>
        new(
            Board: "MSI PRO B760-P WIFI DDR4 (MS-7D98)",
            FirmwareVersion: "1.H0",
            Setting: reglage,
            // Illisible : aucune methode de `MSI_BiosSetting` n'est appelee.
            CurrentValue: null,
            TargetValue: cible,
            Provider: "aucun: niveau Guided",
            Capability: BiosCapabilityLevel.Guided,
            // Le manuel exact de cette carte n'a pas pu etre obtenu. Inferer depuis une carte
            // voisine serait precisement l'erreur que ce champ existe pour rendre visible.
            Recoverability: BiosRecoverability.RecoveryUnknown,
            RequiresReboot: true,
            Validated: false,
            BoardRevision: "1.0",
            MenuPath: cheminMenu,
            PossibleValues: nvram.Values.Values.ToList(),
            DefaultValue: nvram.Default is { } d ? (nvram.Values.TryGetValue(d, out var l) ? l : nvram.ZeroVautAuto && d == 0 ? "Auto" : d.ToString()) : null,
            NoPost: noPost,
            ReadMechanism: "NVRAM UEFI, lecture seule, par le helper eleve",
            DocumentationSource: SourceGuides + " · " + SourceSmbios + " · " + SourceIfr,
            Nvram: nvram);
}
