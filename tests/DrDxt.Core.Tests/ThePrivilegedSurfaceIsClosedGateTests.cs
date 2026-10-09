using System.Reflection;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La surface privilegiee est fermee, et elle le reste.
///
/// Tout ce que DrDXT fait avec des droits d'administrateur passe par un canal : le tube nomme du
/// helper, dont le fil est `PrivilegedOpCodec`. Ce codec ne transporte que des sous-types de
/// <see cref="PrivilegedOp"/>, et le dispatch de l'executeur se ferme sur
/// `_ => Fail("Unsupported privileged op.")`.
///
/// Autrement dit : **cette liste est la surface d'attaque du produit sur la machine du client**, et
/// la reponse a « est-ce qu'une branche de l'app peut abimer le materiel, le BIOS ou Windows ? »
/// commence par « voici les 20 choses qu'elle peut demander, et rien d'autre ».
///
/// Une liste qui n'est pas figee n'est pas une surface : c'est une tendance. Ce controle la fige.
///
/// ══ quand ce test tombe ══
/// Ce n'est pas lui qu'on corrige en ajoutant le nom. Un op de plus, c'est un pouvoir de plus sur
/// la machine de quelqu'un : il se documente dans `docs/NON-NUISANCE.md` (fichier, API, garde,
/// classe de nuisance, test de mutation, residuel) avant d'entrer dans cette liste.
/// </summary>
public sealed class ThePrivilegedSurfaceIsClosedGateTests
{
    /// <summary>
    /// Les 20 operations privilegiees du produit. Figee volontairement : c'est le point de la garde.
    /// 18 au 05/09/2026 ; la 19e (`DeleteNvApiDrsSettingOp`, 10/09/2026) est l'INVERSE d'une
    /// ecriture au profil d'un jeu, documentee dans docs/NON-NUISANCE.md avant d'entrer ici.
    /// La 20e (`CommitManifestOp`, 11/09/2026) ecrit le manifeste signe sous %ProgramData%\DrDxt :
    /// aucun registre, aucun pilote, aucun firmware ; ses entrees ne passent que si ce qu'elles feront
    /// executer plus tard passe l'allowlist. Documentee dans docs/NON-NUISANCE.md (H-aucune).
    /// </summary>
    private static readonly string[] Frozen =
    [
        "ApplyAdlxTuningOp",
        "ApplyGpuFanCurveOp",
        "RestoreGpuFanAutoOp",
        "ApplyNvApiClockOffsetOp",
        "ApplyNvApiDrsSettingOp",
        "ApplyNvApiPowerLimitOp",
        "CancelPendingFirmwareBootOp",
        "CommitManifestOp",
        // 24/09/2026 : le plafond d'envoi QoS et son inverse (docs/NON-NUISANCE.md, section du 24/09).
        "ApplyUploadCapOp",
        "RemoveUploadCapOp",
        // 28/09/2026 : la protection du ping retenue pour le service, et son inverse (docs/NON-NUISANCE.md, section du 28/09).
        "RetainServiceUploadCapOp",
        "ForgetServiceUploadCapOp",
        // 24/09/2026 : retirer / reposer un element BCD de l'ensemble ferme (docs/NON-NUISANCE.md).
        "DeleteBcdValueOp",
        "SetBcdValueOp",
        "CreateRestorePointOp",
        "DeleteNvApiDrsSettingOp",
        "DeleteRegistryValueOp",
        "EnableSystemRestoreOp",
        "EstablishLeaseOp",
        "InstallPresentMonServiceOp",
        "PingOp",
        "ReadFirmwareVariableOp",
        // 25/09/2026 : lecture de l'etat du magasin de composants, sans parametre, rien ecrit
        // (docs/NON-NUISANCE.md, section du 25/09).
        "ReadComponentStoreHealthOp",
        "RebootToFirmwareSetupOp",
        "RenewLeaseOp",
        "RunServiceControlOp",
        "SetActivePowerPlanOp",
        // 07/10/2026 (L-CPU) : le mode de boost secteur d'un plan, seul reglage du plan ecrit (docs/NON-NUISANCE.md).
        "SetProcessorBoostModeOp",
        "SetRegistryValueOp",
    ];

    /// <summary>
    /// Deux mots interdits dans un NOM d'operation, et deux raisons distinctes :
    ///   • « voltage » : ecrire une tension est le seul geste de ce produit qui puisse abimer du
    ///     materiel de facon irreversible ;
    ///   • « write »/« set » + « firmware » : le varstore `Setup` n'est lisible que sous le GUID
    ///     constructeur, et la seule operation firmware du produit est une lecture.
    /// </summary>
    private static readonly string[] Forbidden =
        ["voltage", "undervolt", "vfcurve", "writefirmware", "setfirmware"];

    private static List<string> Offenders(IEnumerable<string> names) =>
        names.Where(n => Forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)))
             .OrderBy(n => n, StringComparer.Ordinal)
             .ToList();

    private static List<string> ActualOpNames() =>
        typeof(PrivilegedOp).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(PrivilegedOp)))
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    [Fact]
    public void The_set_of_privileged_operations_is_exactly_the_frozen_twenty_five()
    {
        var actual = ActualOpNames();

        // Sans ceci, une reflexion cassee rendrait la meme couleur qu'un depot sain.
        actual.Should().NotBeEmpty("les operations privilegiees doivent etre trouvables");

        actual.Should().Equal(
            Frozen.OrderBy(n => n, StringComparer.Ordinal),
            "cette liste est la surface d'attaque du produit sur la machine du client : un op de "
            + "plus est un pouvoir de plus, et il se documente dans docs/NON-NUISANCE.md avant "
            + "d'entrer ici");
    }

    [Fact]
    public void Nothing_privileged_writes_a_voltage_or_a_firmware_variable()
    {
        // Deux mots interdits dans un NOM d'operation, et deux raisons distinctes :
        //   • « voltage » : ecrire une tension est le seul geste de ce produit qui puisse abimer
        //     du materiel de facon irreversible. Il n'existe pas, et c'est ce qui rend la
        //     non-nuisance materielle vraie par ABSENCE plutot que par garde.
        //   • « write »/« set » + « firmware » : aucune ecriture firmware n'existe ; la seule
        //     operation firmware est une lecture (ReadFirmwareVariableOp, Setup et CpuSetup compris).
        Offenders(ActualOpNames())
            .Should().BeEmpty(
                "ce qui n'existe pas ne peut pas nuire, et c'est la forme la plus solide de "
                + "non-nuisance — la perdre ne doit pas etre silencieux");
    }

    /// <summary>
    /// Controle POSITIF du filtre de noms, sur des noms synthetiques.
    ///
    /// Il manquait, et le trou etait mesurable : le 06/09/2026, remplacer « setfirmware » par
    /// « setfirmwar3 » dans la liste interdite laissait les 164 tests du projet au vert. C'est
    /// normal, et c'est bien le probleme — le filtre porte sur une liste d'ops ou aucun nom
    /// interdit n'apparait, donc il rend « aucun coupable » que ses motifs soient justes ou
    /// casses. Une garde d'ABSENCE ne peut pas se prouver sur son propre echantillon : il faut
    /// lui donner un coupable fabrique.
    ///
    /// Le controle positif d'a cote (`The_detector_would_actually_catch_a_new_operation`) ne
    /// couvrait pas celui-ci : il eprouve la comparaison a la liste figee, pas le filtre de mots.
    /// </summary>
    [Fact]
    public void The_forbidden_name_filter_would_actually_catch_a_voltage_or_firmware_write()
    {
        Offenders(["PingOp", "SetGpuVoltageOffsetOp", "WriteFirmwareVariableOp", "PingOp2"])
            .Should().Equal("SetGpuVoltageOffsetOp", "WriteFirmwareVariableOp");
    }

    [Fact]
    public void The_detector_would_actually_catch_a_new_operation()
    {
        // Controle POSITIF : sans lui, une reflexion qui ne trouve rien passerait pour un depot
        // sain. Il prouve que la comparaison sait dire non.
        var withAnExtra = ActualOpNames().Append("SetGpuVoltageOp")
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        withAnExtra.Should().NotEqual(Frozen.OrderBy(n => n, StringComparer.Ordinal));
    }
}
