using System.Text;
using DrDxt.Core;
using DrDxt.ElevatedHelper.Ipc;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Les octets signés d'une requête sont « typed-v1 », l'op en JSON et le nonce, séparés par le séparateur d'unité ASCII
/// (0x1F). Cette forme n'est sans ambiguïté que si le séparateur ne peut jamais apparaître brut dans un champ : l'op sort
/// toujours du codec, dont le JSON échappe les caractères de contrôle, et le nonce est un GUID en hexadécimal. Ces tests
/// tiennent les trois faits ; le séparateur s'écrit en échappement dans le source, pour qu'aucun éditeur ne l'efface.
/// </summary>
public sealed class LaFormeSigneeResteSansAmbiguiteTests
{
    [Fact]
    public void Les_champs_sont_separes_par_le_separateur_d_unite()
        => TypedRequestHmac.CanonicalBytes("{}", "n").Should().Equal(Encoding.UTF8.GetBytes("typed-v1\u001F{}\u001Fn"));

    [Fact]
    public void Le_json_d_une_op_n_a_jamais_le_separateur_brut()
    {
        var op = new SetRegistryValueOp(RegistryHiveKind.CurrentUser, @"Software\DrDXT", "a\u001Fb", RegistryValueType.String, "c\u001Fd");

        var json = PrivilegedOpCodec.Serialize(op);

        json.IsOk.Should().BeTrue("témoin : l'op se sérialise");
        json.Value.Should().NotContain("\u001F", "le codec échappe les caractères de contrôle");
    }

    [Fact]
    public void Le_nonce_n_a_jamais_le_separateur()
        => Guid.NewGuid().ToString("N").Should().MatchRegex("^[0-9a-f]{32}$", "le client signe avec un GUID en hexadécimal");
}
