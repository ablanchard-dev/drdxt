using System.IO;
using System.Text.RegularExpressions;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.App.Tests;

/// <summary>
/// Ce que DrDXT ne peut pas faire sort en rapport final, étape par étape. Le docteur dit des choses que DrDXT n'écrit pas (BIOS, câbles, réglages du
/// jeu, disques) : chacune doit avoir son étape dans docs/A-FAIRE-A-LA-MAIN.md, sinon le joueur lit un constat sans
/// geste. La garde relie chaque fichier qui émet une recommandation, et chaque réglage de firmware que DrDXT guide, à
/// une phrase du guide. Un nouveau conseil sans étape fait échouer ce test : c'est voulu.
/// </summary>
public sealed class ChaqueConseilDuDocteurASonEtapeTests
{
    private static DirectoryInfo Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        return d!;
    }

    // Une emission : l'action passee a un constat (virgule, parenthese, ou branche d'un ternaire). Le motif laisse
    // passer `Action is RepairAction.Recommandation or` (un test de valeur) et `=> "recommandation"` (un libelle).
    private static readonly Regex Emission = new(@"RepairAction\.Recommandation\s*[,):]");

    // Fichier qui emet ⇒ phrase du guide (espaces normalises : une phrase peut passer a la ligne dans le .md).
    private static readonly Dictionary<string, string> EtapeParFichier = new()
    {
        ["TypeDeNat.cs"] = "Double NAT (DrDXT dit « box derrière box »)",
        ["ObsStudio.cs"] = "Pertes en stream au-dessus de 5 %",
        ["PiloteRTCore.cs"] = "Pilote RTCore présent",
        ["EcransDupliques.cs"] = "Écrans dupliqués à des fréquences différentes",
        ["PiloteGraphique32Bits.cs"] = "Pilote graphique 32 bits absent ou manquant",
        ["VitesseDeLaCarte.cs"] = "Vitesse ou duplex de la carte réseau forcés",
        ["SuperpositionsMaterielles.cs"] = "Superpositions matérielles coupées",
        ["HorlogeQpc.cs"] = "Horloge haute résolution lente ou qui recule",
        ["StabiliteApresChangement.cs"] = "la semaine qui a suivi un changement",
        ["ConnexionDeJeu.cs"] = "Câble Ethernet plutôt que Wi-Fi",
        ["CurveOptimizerAmd.cs"] = "Curve Optimizer",
        ["FichierDEchangeSurDisqueDur.cs"] = "Fichier d'échange sur un disque dur",
        ["MemoireSaturee.cs"] = "Mémoire saturée (PC de bureau comme PC de jeu)",
        ["LimiteDePuissance.cs"] = "Limite de puissance du processeur (Intel)",
        ["ChaleurEnPartie.cs"] = "Trop chaud en partie",
        ["UsureBatterie.cs"] = "Batterie usée (portable)",
        ["VentilateursEnPartie.cs"] = "Ventilateur qui s'arrête ou ne tourne plus",
        ["CarteChaudeEnPartie.cs"] = "Carte graphique freinée par la chaleur",
        ["TestDeRefroidissement.cs"] = "Le test de refroidissement de DrDXT",
        ["HeuresActives.cs"] = "hors de tes heures d'activité",
        ["JeuxSurDisqueDur.cs"] = "Jeux sur un disque dur",
        ["MicrocodeRaptorLake.cs"] = "microcode antérieur au correctif",
        ["EcranBride.cs"] = "écran bridé",
        ["ReglagesCod.cs"] = "Reflex : « Activé »",
        ["ReglagesUnreal.cs"] = "Éclairage global et reflets (jeux Unreal Engine 5",
        ["BranchementDeLaCarte.cs"] = "Lien PCIe de la carte graphique",
        ["EnregistrementNvidia.cs"] = "Instant Replay",
        ["MagasinDePilotes.cs"] = "pnputil /delete-driver",
        ["VersionDuPilote.cs"] = "DrDXT te dit si tu es en retard",
        ["FrequenceSouris.cs"] = "polling rate",
        ["MisesAJourEnAttente.cs"] = "Windows Update > Rechercher des mises à jour",
        ["PeripheriquesEnErreur.cs"] = "pilote est en échec",
        ["StorageWearReader.cs"] = "chkdsk",
        ["WindowsUpdateLog.cs"] = "Microsoft Store > Bibliothèque",
        ["AvantLIncident.cs"] = "les logiciels installés avant un incident",
        ["DemarrageEtDisque.cs"] = "Place libre du disque système",
        ["ErreursMaterielles.cs"] = "les erreurs matérielles corrigées",
        ["ModeAlimentation.cs"] = "repasse en Équilibré",
        ["PlantagesDeJeu.cs"] = "les plantages d'un jeu",
        ["PointDeRestaurationSain.cs"] = "point de restauration qui contient le défaut",
        ["ProgrammesAuDemarrage.cs"] = "Applications de démarrage",
        ["RepairAuditService.cs"] = "le logiciel à qui il appartient",
        ["RepairAuditService.Processeur.cs"] = "Plan d'alimentation et boost",
        ["RepairAuditService.CarteGraphique.cs"] = "écran bridé",
        ["RepairAuditService.PeripheriquesEtDisques.cs"] = "Endurance d'un SSD consommée",
        ["RepairAuditService.Stabilite.cs"] = "mdsched.exe",
        ["BcdInterpretation.cs"] = "la politique de démarrage",
        ["CrashDumpPolicy.cs"] = "le type de vidage",
        ["TestMemoireEnSession.cs"] = "le test mémoire en session",
        ["RepriseDuTestMemoire.cs"] = "que la machine n'a pas fini",
        ["DefautsConnus.cs"] = "Firmware d'un SSD signalé par son fabricant",
        ["FtpmAmd.cs"] = "Ryzen avec le TPM du processeur (fTPM) actif",
        ["Bufferbloat.cs"] = "SQM",
        ["JournalDesLeviersAppliques.cs"] = "relance l'optimisation",
        ["SanteWindows.cs"] = "Récupération > « Réinstaller maintenant »",
        ["OptionsNvidiaDuJoueur.cs"] = "Surcouche DLSS et mode faible latence",
        ["ReglagesRadeonDuJoueur.cs"] = "Réglages Radeon (Boost, Image Sharpening, RSR, AFMF, HYPR-RX)",
        ["ConfigurationCallOfDuty.cs"] = "Fils de rendu (Call of Duty)",
        ["EcransBleus.cs"] = "le compte par trois sources",
        ["PannesDuJournal.cs"] = "Pilote graphique relancé par Windows",
        ["SanteNvme.cs"] = "lève son avertissement critique",
        ["ChiffrementDuDisque.cs"] = "Reprendre la protection",
        ["PiloteExigeParLeJeu.cs"] = "Pilote sous le plancher qu'un jeu exige",
        ["InventaireDesPilotes.cs"] = "Pilote non signé : si tu ne t'en sers plus",
        ["InstallationDePilote.cs"] = "Installation de pilote bloquée par stratégie",
        ["PreferencesDefender.cs"] = "Accès contrôlé aux dossiers activé sans ton jeu",
    };

    // Chaque reglage de firmware que DrDXT lit et guide (jamais ecrit) ⇒ son etape.
    private static readonly Dictionary<FirmwareSettingKind, string> EtapeParReglage = new()
    {
        [FirmwareSettingKind.XmpExpo] = "XMP sur Intel, EXPO sur AMD",
        [FirmwareSettingKind.ReBar] = "Re-Size BAR Support",
        [FirmwareSettingKind.Above4G] = "Above 4G Decoding",
        [FirmwareSettingKind.Csm] = "CSM désactivé",
        [FirmwareSettingKind.X3dCppc] = "CPPC Preferred Cores",
        [FirmwareSettingKind.PcieGen] = "PCIe x16 Link Speed",
        [FirmwareSettingKind.BiosVersion] = "dernier BIOS",
    };

    private static string Guide() => Regex.Replace(
        File.ReadAllText(Path.Combine(Racine().FullName, "docs", "A-FAIRE-A-LA-MAIN.md")), @"\s+", " ");

    private static IReadOnlyList<string> FichiersQuiEmettent() =>
        Directory.EnumerateFiles(Path.Combine(Racine().FullName, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => Emission.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList()!;

    [Fact]
    public void Le_crible_reconnait_une_emission_et_laisse_passer_un_test_de_valeur()
    {
        Emission.IsMatch("\"texte\", RepairAction.Recommandation);").Should().BeTrue();
        Emission.IsMatch("repete ? RepairAction.Recommandation : RepairAction.Aucune);").Should().BeTrue();
        Emission.IsMatch("Action is RepairAction.Recommandation or RepairAction.GuidedBios;").Should().BeFalse();
        Emission.IsMatch("RepairAction.Recommandation => \"recommandation\",").Should().BeFalse();
    }

    [Fact]
    public void Chaque_fichier_qui_recommande_a_son_etape_dans_le_guide()
    {
        var fichiers = FichiersQuiEmettent();
        fichiers.Count.Should().BeGreaterThan(25, "un crible qui ne voit presque rien n'accuse personne");

        fichiers.Should().BeEquivalentTo(EtapeParFichier.Keys,
            "un nouveau conseil du docteur demande son étape dans docs/A-FAIRE-A-LA-MAIN.md et sa ligne ici ; un conseil retiré, sa ligne en moins");

        var guide = Guide();
        foreach (var (fichier, phrase) in EtapeParFichier)
            guide.Contains(phrase, StringComparison.Ordinal).Should().BeTrue($"{fichier} recommande un geste : le guide doit dire « {phrase} »");
    }

    [Fact]
    public void Chaque_reglage_de_firmware_guide_a_son_etape_dans_le_guide()
    {
        Enum.GetValues<FirmwareSettingKind>().Should().BeEquivalentTo(EtapeParReglage.Keys,
            "un réglage de firmware de plus se guide à la main : son étape va dans le guide et sa ligne ici");

        var guide = Guide();
        foreach (var (reglage, phrase) in EtapeParReglage)
            guide.Contains(phrase, StringComparison.Ordinal).Should().BeTrue($"{reglage} se règle au BIOS : le guide doit dire « {phrase} »");
    }
}
