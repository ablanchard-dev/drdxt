using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le BCD est le magasin de demarrage. L'allowlist ne laisse passer que les cinq
// elements que BcdInterpretation sait juger, et seulement leurs valeurs documentees.
public sealed class BcdAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    [Theory]
    [InlineData("bootstatuspolicy")]
    [InlineData("useplatformclock")]
    [InlineData("useplatformtick")]
    [InlineData("disabledynamictick")]
    [InlineData("tscsyncpolicy")]
    public void Les_cinq_elements_juges_peuvent_etre_retires(string element)
        => _allow.Validate(new DeleteBcdValueOp(element)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData("testsigning")]
    [InlineData("nointegritychecks")]
    [InlineData("nx")]
    [InlineData("safeboot")]
    [InlineData("hypervisorlaunchtype")]
    [InlineData("device")]
    [InlineData("path")]
    [InlineData("BootStatusPolicy")]
    [InlineData("")]
    public void Tout_autre_element_est_refuse_meme_en_retrait(string element)
        => _allow.Validate(new DeleteBcdValueOp(element)).IsOk.Should().BeFalse();

    [Theory]
    [InlineData("bootstatuspolicy", "IgnoreAllFailures")]
    [InlineData("bootstatuspolicy", "DisplayAllFailures")]
    [InlineData("useplatformclock", "Yes")]
    [InlineData("tscsyncpolicy", "Enhanced")]
    public void L_inverse_ne_repose_qu_une_valeur_documentee(string element, string valeur)
        => _allow.Validate(new SetBcdValueOp(element, valeur)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData("bootstatuspolicy", "IgnoreAllFailures & calc")]
    [InlineData("bootstatuspolicy", "")]
    [InlineData("useplatformclock", "true")]
    [InlineData("testsigning", "Yes")]
    [InlineData("safeboot", "Minimal")]
    public void Une_valeur_ou_un_element_hors_liste_est_refuse(string element, string valeur)
        => _allow.Validate(new SetBcdValueOp(element, valeur)).IsOk.Should().BeFalse();
}
