using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// 26/09/2026 — un ordre dont les champs manquent se lie quand meme : le desérialiseur y met des
/// <c>null</c>. Le decodeur est total, mais c'est l'allowlist qui recoit ces <c>null</c> ensuite. Une
/// exception a cet endroit ferait tomber la connexion au lieu de refuser l'ordre. Chaque type, avec son
/// seul discriminant : refuse ou accepte proprement, jamais une exception.
/// </summary>
public sealed class UnOrdreAuxChampsVidesEstRefuseSansLeverTests
{
    public static TheoryData<int> Types()
    {
        var d = new TheoryData<int>();
        foreach (var t in Enum.GetValues<PrivilegedOpType>()) d.Add((int)t);
        return d;
    }

    [Theory]
    [MemberData(nameof(Types))]
    public void Un_ordre_reduit_a_son_type_ne_fait_jamais_lever_l_allowlist(int type)
    {
        var lie = PrivilegedOpCodec.Deserialize($"{{\"op\":{type}}}");
        if (!lie.IsOk) return;   // refuse au decodage : c'est deja un refus propre

        var valider = () => new OpAllowlist().Validate(lie.Value!);

        valider.Should().NotThrow();
    }

    [Fact]
    public void Le_temoin_couvre_bien_chaque_type_et_des_ordres_atteignent_vraiment_l_allowlist()
    {
        Types().Count().Should().Be(Enum.GetValues<PrivilegedOpType>().Length).And.BeGreaterThan(20);
        // Si tout etait refuse au decodage, la theorie ci-dessus ne toucherait jamais l'allowlist.
        Enum.GetValues<PrivilegedOpType>()
            .Count(t => PrivilegedOpCodec.Deserialize($"{{\"op\":{(int)t}}}").IsOk)
            .Should().BeGreaterThan(5);
    }
}
