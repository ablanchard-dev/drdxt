using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// NvApiDrsActions : le contrat fail-clean, sans jamais toucher au pilote de la machine.
//
// Ces tests passaient 141 au vrai pilote, et chaque `dotnet test` plafonnait la machine
// de dev a 141 images/s. Corrige alors en reecrivant « la valeur deja en place ».
//
// Ce n'etait pas assez. Reecrire la meme valeur passe quand meme par SetSetting puis
// SaveSettings : la base du pilote (nvdrsdb0.bin) est reecrite a chaque suite (vu : 09:55, pendant la
// suite complete), la provenance du reglage devient « pose par un humain » (drsprobe : SetByUser), et
// NVIDIA documente que SaveSettings ecrase ce qu'un autre processus a modifie entre-temps (le panneau
// NVIDIA ouvert pendant les tests). La provenance, DrDXT s'en sert pour conseiller. Et les ecritures
// sur la carte sont refusees par le proprietaire de la machine.
//
// Regle : un test n'appelle jamais le vrai pilote en ecriture. Le chemin natif reel est prouve par le
// smoke manuel (tools/m2smoke) ; ici, un pilote factice.
// -----------------------------------------------------------------------
public sealed class NvApiDrsActionsTests
{
    private static INvApiNative Pilote(bool disponible)
    {
        var n = Substitute.For<INvApiNative>();
        n.IsAvailable.Returns(disponible);
        return n;
    }

    [Fact]
    public void Sans_NVAPI_echec_propre_et_aucune_ecriture_tentee()
    {
        var pilote = Pilote(disponible: false);

        var r = new NvApiDrsActions(pilote).SetGlobalSetting(0x10835002u, 141u);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.HelperOpFailed);
        pilote.DidNotReceive().TrySetDrsGlobalSetting(Arg.Any<uint>(), Arg.Any<uint>());
    }

    [Fact]
    public void Une_exception_du_pilote_ne_traverse_jamais_le_helper()
    {
        var pilote = Pilote(disponible: true);
        pilote.TrySetDrsGlobalSetting(Arg.Any<uint>(), Arg.Any<uint>()).Throws(new AccessViolationException());

        var act = () => new NvApiDrsActions(pilote).SetGlobalSetting(0x10835002u, 141u);

        act.Should().NotThrow().Subject.IsOk.Should().BeFalse();
    }

    [Fact]
    public void Ecriture_acceptee_par_le_pilote_rend_Ok()
    {
        var pilote = Pilote(disponible: true);
        pilote.TrySetDrsGlobalSetting(0x10835002u, 141u).Returns(true);

        new NvApiDrsActions(pilote).SetGlobalSetting(0x10835002u, 141u).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Aucun_test_ne_construit_les_actions_sur_le_vrai_pilote()
    {
        var racine = new DirectoryInfo(AppContext.BaseDirectory);
        while (racine is not null && !File.Exists(Path.Combine(racine.FullName, "DrDXT.sln")))
            racine = racine.Parent;
        racine.Should().NotBeNull();

        const string VraiPilote = "new NvApiDrsActions()";
        // Temoin : le motif se reconnait bien dans du code.
        "var drs = new NvApiDrsActions();".Should().Contain(VraiPilote);

        var fautes = Directory.EnumerateFiles(Path.Combine(racine!.FullName, "tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && !f.EndsWith("NvApiDrsActionsTests.cs", StringComparison.Ordinal))
            .Where(f => File.ReadAllText(f).Contains(VraiPilote, StringComparison.Ordinal))
            .ToList();

        fautes.Should().BeEmpty("un test qui ecrit dans le vrai pilote change la machine ou il tourne");
    }
}
