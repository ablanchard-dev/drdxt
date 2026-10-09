using System.Reflection;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;

namespace DrDxt.Measurement.Tests.PresentMon;

/// <summary>
/// Le client nomme le tube qu'il ouvre, au lieu de dependre d'un defaut implicite.
///
/// `pmOpenSession()` — la variante sans argument — ouvre le tube par defaut sans que rien, dans le
/// code de DrDXT, ne dise lequel ni pourquoi. `pmOpenSessionWithPipe(handle, nom)` existe dans
/// l'en-tete vendore depuis le premier jour (`third_party/presentmon/PresentMonAPI.h:394`) et
/// n'avait jamais ete liee.
///
/// Ce que ce test ne dit pas : il ne promet aucun tube prive. Mesures du 07/09/2026 par le SCM
/// (`docs/PRESENTMON-COEXISTENCE.md`) : le serveur ignore `--control-pipe` et publie toujours le
/// nom par defaut — en 2.4.1 comme avec le binaire Intel 2.5.1. Il n'existe donc qu'UN nom
/// possible, et <see cref="DrDxt.Core.PresentMonNaming.ControlPipe"/> le porte.
///
/// Ce que le test protege, et qui vaut d'etre protege : **une seule ligne du produit decide a quel
/// service DrDXT parle.** C'est elle qui changera le jour ou l'API cliente montera de version pour
/// consommer le service Intel deja present — la seule voie qui fermerait la cohabitation.
///
/// Le test verifie la LIAISON declaree, pas l'appel : la DLL est absente en CI. La preuve de bout
/// en bout est `tools/pmoncoexist`, qui a rendu `IsServiceAvailable = True` sur la machine de
/// reference apres reparation du service par le code du produit.
/// </summary>
public class PresentMonPipeBindingTests
{
    private static IEnumerable<MethodInfo> PInvokes() =>
        typeof(PresentMonApi2)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(m => m.Attributes.HasFlag(MethodAttributes.PinvokeImpl));

    [Fact]
    public void La_variante_SANS_nom_de_tube_n_est_plus_liee_du_tout()
    {
        // Tant qu'elle est liee, elle peut etre rappelee par distraction — et elle ouvre alors un
        // tube que personne n'a choisi, sans qu'aucune lecture du code ne le revele.
        PInvokes().Select(m => m.Name).Should().NotContain("pmOpenSession");
    }

    [Fact]
    public void La_session_s_ouvre_par_la_variante_QUI_PREND_UN_NOM_DE_TUBE()
    {
        var avecTube = PInvokes().SingleOrDefault(m => m.Name == "pmOpenSessionWithPipe");

        avecTube.Should().NotBeNull("c'est le seul point d'entree qui laisse choisir le tube");
        // `PM_STATUS pmOpenSessionWithPipe(PM_SESSION_HANDLE* pHandle, const char* controlPipeName)`
        var parametres = avecTube!.GetParameters();
        parametres.Should().HaveCount(2);
        parametres[1].ParameterType.Should().Be(typeof(string));
    }
}
