using System.Reflection;
using DrDxt.Core;
using FluentAssertions;
using Xunit;

namespace DrDxt.Core.Tests;

/// <summary>
/// La non-nuisance la plus solide est l'ABSENCE : ce qui n'existe pas ne peut pas abimer une carte.
///
/// Mesure du 05/09/2026 : `GpuArchitectureGate.SupportsVoltageUndervolt` refuse le voltage sur
/// RDNA3 et sur toute architecture non nommee, et sa garantie est reelle (mutation du defaut
/// conservateur en permissif : rouge). Mais son seul appelant de production est le RAPPORT DE
/// COUVERTURE. Elle ne refuse aucune ecriture — parce qu'aucun op privilegie n'ecrit une tension de carte graphique
/// (la seule tension admise est celle des coeurs Intel, SetCpuVoltageOffsetOp). L'undervolt de la carte est un offset
/// d'horloge ; la courbe V/F n'existe pas.
///
/// Autrement dit la garde protege aujourd'hui une VERITE D'AFFICHAGE, pas un geste. Le jour ou
/// quelqu'un ajoutera l'op d'ecriture, la garde ne se mettra pas toute seule sur le chemin — et
/// personne ne se souviendra qu'elle etait ecrite pour ca.
///
/// Ce controle est ce souvenir. Il tombe le jour ou un op de tension apparait. Quand il tombe :
/// cabler `SupportsVoltageUndervolt` Dans le chemin d'ecriture, le prouver par mutation, et
/// seulement ensuite retirer le nom de la liste interdite.
/// </summary>
public sealed class NoVoltageWriteOpExistsGateTests
{
    private static readonly string[] Forbidden = ["voltage", "undervolt", "millivolt", "vfcurve", "vfpoint"];

    /// <summary>La seule tension admise, celle des cœurs du PROCESSEUR vers le bas (decision produit ; voir
    /// ThePrivilegedSurfaceIsClosedGateTests.TensionAdmise et docs/NON-NUISANCE.md). Ce controle reste le souvenir pour les
    /// cartes graphiques : une op de tension de carte le fait toujours tomber.</summary>
    private static readonly HashSet<string> TensionAdmise = ["SetCpuVoltageOffsetOp"];

    private static IEnumerable<string> OpNames() =>
        typeof(PrivilegedOp).Assembly
            .GetTypes()
            .Where(t => t.IsSubclassOf(typeof(PrivilegedOp)))
            .Select(t => t.Name);

    private static List<string> Offenders(IEnumerable<string> names) =>
        names.Where(n => !TensionAdmise.Contains(n) && Forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)))
             .OrderBy(n => n, StringComparer.Ordinal)
             .ToList();

    [Fact]
    public void No_privileged_op_writes_a_voltage()
    {
        var names = OpNames().ToList();

        // Sans ceci, une reflexion cassee rendrait la meme couleur qu'un depot sain.
        names.Should().NotBeEmpty("les ops privilegies doivent etre trouvables, sinon ce gate est vert pour rien");

        Offenders(names).Should().BeEmpty(
            "ecrire une tension sur une carte est le seul geste de ce produit qui puisse abimer du "
            + "materiel de facon irreversible ; s'il apparait, il doit passer par GpuArchitectureGate");
    }

    [Fact]
    public void The_gate_would_actually_catch_a_voltage_op_if_one_appeared()
    {
        // Controle POSITIF sur des noms synthetiques : il prouve que le detecteur sait dire non.
        Offenders(["PingOp", "SetGpuVoltageOffsetOp", "SetActivePowerPlanOp", "SetCpuVoltageOffsetOp"])
            .Should().ContainSingle().Which.Should().Be("SetGpuVoltageOffsetOp", "la tension d'une carte reste attrapée, celle des cœurs admise est nommée");
    }
}
