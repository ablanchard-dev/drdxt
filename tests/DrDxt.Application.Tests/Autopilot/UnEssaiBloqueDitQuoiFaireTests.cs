using DrDxt.Application.Autopilot;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using FluentAssertions;
using NSubstitute;

namespace DrDxt.Application.Tests.Autopilot;

// Un essai ne part jamais par-dessus un levier encore posé. Le refus n'est pas un cul-de-sac : il nomme le levier, la valeur
// relue et l'endroit où le remettre, et dit que les essais reprendront ensuite.
public sealed class UnEssaiBloqueDitQuoiFaireTests : IDisposable
{
    private const string Id = "latency.hagsOn";
    private readonly string _fichier = Path.Combine(Path.GetTempPath(), $"drdxt-bloque-{Guid.NewGuid():N}.json");
    public void Dispose() { try { File.Delete(_fichier); } catch (IOException) { } }

    private static TweakSnapshot Snapshot()
        => new(Id, DrDxt.Tests.Shared.InversesDeTest.Pour(Id, "1"), DrDxt.Tests.Shared.InversesDeTest.Pour(Id, "2"));

    [Fact]
    public void Le_refus_nomme_le_levier_la_valeur_lue_et_l_endroit()
    {
        var registre = Substitute.For<IRegistryReader>();
        registre.ReadDword(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode")
            .Returns(Result<uint?>.Ok(2u));
        var journal = new FichierRevertEnAttente(_fichier, registre: registre, nom: _ => "Planification GPU matérielle");
        journal.Poser(Id, Snapshot()).IsOk.Should().BeTrue();

        var refus = journal.Poser("fixit.power_plan", null);

        var cible = TweakReadback.Relire(Snapshot(), registre).Cible;
        cible.Should().NotBeNull("témoin : la feuille se relit");
        (refus.Error?.Message).Should().Be(Docteur.T("RV.EncoreEnEssai.Geste", "Planification GPU matérielle", "2", cible!));
    }

    [Fact]
    public void Sans_relecture_le_refus_dit_non_lu_et_l_endroit_du_reglage()
    {
        var journal = new FichierRevertEnAttente(_fichier);
        journal.Poser(Id, Snapshot()).IsOk.Should().BeTrue();

        (journal.Poser("fixit.power_plan", null).Error?.Message)
            .Should().Be(Docteur.T("RV.EncoreEnEssai.Geste", Id, Docteur.T("RV.ValeurNonLue"), Docteur.T("RV.EndroitDuReglage")));
    }
}
