using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

// -----------------------------------------------------------------------
// L'echelle de recuperation est la page qu'on lit quand la machine
// ne demarre plus apres un reglage BIOS : ecran noir normal, entrainement
// memoire, Clear CMOS, Load Optimized Defaults, un seul changement a la fois.
// Le generateur la rend en deux exemplaires -- `Recovery.Top` et
// `Recovery.Bottom` -- pour l'encadrer au-dessus et en dessous des etapes.
//
// Mesure du jour : l'interface n'affiche que `BiosGuide.RecoveryTop`. Aucun
// `ItemsSource` ne lie `RecoveryBottom`, nulle part. Ce n'est pas un defaut
// aujourd'hui, parce que `BuildRecovery` bati les deux moities avec la meme
// methode : elles sont identiques, donc en montrer une suffit a tout montrer.
//
// Mais cette completude ne tient qu'a cette egalite, et rien ne la prouvait.
// Differencier `Bottom` -- y ajouter une etape de derniere chance, par exemple --
// est un geste naturel qui ferait disparaitre cette etape de l'ecran sans
// qu'aucun test ne rougisse, pour la personne qui en a le plus besoin.
// -----------------------------------------------------------------------
public class LEchelleDeRecuperationEstEntiereALEcranTests
{
    // Le meme banc que GuideGeneratorTests : une carte ASUS B650 couverte, en UEFI.
    private static HardwareInventory Hw()
        => new(
            Cpu: new CpuInfo("Ryzen 7 7800X3D", "AuthenticAMD", 8, 16, true, 2),
            Gpus: new[] { new GpuInfo("RTX 4080", "NVIDIA", 16L << 30, "999.99", TriState.Off) },
            Memory: new[] { new MemoryModule("PN", "Mfr", 4800, 6000) },
            Board: new MotherboardInfo("ASUSTeK COMPUTER INC.", "ROG STRIX B650-A GAMING WIFI"),
            Bios: new BiosInfo("2.0", DateTimeOffset.UtcNow.AddMonths(-2), true),
            Chassis: ChassisType.Desktop);

    private static PersonalizedGuide Guide()
    {
        var constat = new BiosFinding(
            FirmwareSettingKind.XmpExpo, Severity.Major, "title", "rationale",
            BiosMatrixClass.Guide, TriState.Off, BiosRecommendedAction.Enable, "action", null,
            StateNotReadable: false);

        var r = new GuideGenerator(GuideLibraryFactory.CreateDefault())
            .Generate(Hw(), new[] { constat });
        r.IsOk.Should().BeTrue("le banc doit produire un guide reel, pas un echec");
        return r.Value!;
    }

    [Fact]
    public void Les_deux_moities_de_l_echelle_disent_la_meme_chose()
    {
        var recuperation = Guide().Recovery;

        var haut = recuperation.Top.Select(s => s.InstructionKey).ToList();
        var bas = recuperation.Bottom.Select(s => s.InstructionKey).ToList();

        bas.Should().Equal(haut,
            "l'interface n'affiche que BiosGuide.RecoveryTop : tout ce que Bottom dirait " +
            "en plus serait invisible pour quelqu'un dont la machine ne redemarre pas. " +
            "Si les deux moities doivent diverger, lier aussi RecoveryBottom dans la vue de scan (Views/Scan/BiosEtSante.xaml)");
    }

    [Fact]
    public void L_echelle_affichee_contient_bien_les_cinq_etapes_obligatoires()
    {
        // La garantie positive : le test ci-dessus serait vert sur deux listes vides.
        var haut = Guide().Recovery.Top.Select(s => s.InstructionKey).ToList();

        haut.Should().Contain(new[]
        {
            BiosGuideStringKeys.RecoveryNoBootNormal,
            BiosGuideStringKeys.RecoveryMemoryTraining,
            BiosGuideStringKeys.RecoveryClearCmos,
            BiosGuideStringKeys.RecoveryLoadDefaults,
            BiosGuideStringKeys.RecoveryOneChangeAtATime,
        });
    }

    [Fact]
    public void Les_deux_moities_restent_des_listes_independantes()
    {
        // Temoin de l'intention d'origine : le generateur promet en commentaire des
        // instances distinctes. Si un jour Top et Bottom devenaient la meme liste, le
        // test d'egalite ci-dessus deviendrait une tautologie et cesserait de mordre.
        var recuperation = Guide().Recovery;

        recuperation.Bottom.Should().NotBeSameAs(recuperation.Top);
    }
}
