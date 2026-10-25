using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le codec ne lie que ce que l'op déclare : un champ manquant, inconnu ou une énumération hors plage n'est pas comblé par
// une valeur par défaut que l'allowlist devrait ensuite deviner. L'allowlist reste l'autorité sur les valeurs.
public sealed class CodecFermeTests
{
    private const string Registre = """{"op":1,"Hive":0,"Path":"SOFTWARE\\X","Name":"n","Kind":0,"Value":"1"}""";

    [Fact]
    public void Une_op_complete_se_lie()
        => PrivilegedOpCodec.Deserialize(Registre).IsOk.Should().BeTrue("témoin : la charge de référence passe");

    [Theory]
    [InlineData("""{"op":1,"Path":"SOFTWARE\\X","Name":"n","Kind":0,"Value":"1"}""")]                 // Hive absent
    [InlineData("""{"op":1,"Hive":0,"Name":"n","Kind":0,"Value":"1"}""")]                              // Path absent
    [InlineData("""{"op":1,"Hive":7,"Path":"SOFTWARE\\X","Name":"n","Kind":0,"Value":"1"}""")]         // Hive hors plage
    [InlineData("""{"op":1,"Hive":0,"Path":"SOFTWARE\\X","Name":"n","Kind":99,"Value":"1"}""")]        // Kind hors plage
    [InlineData("""{"op":1,"Hive":0,"Path":"SOFTWARE\\X","Name":"n","Kind":0,"Value":"1","X":1}""")]   // champ inconnu
    [InlineData("""{"op":1,"Hive":0,"Path":null,"Name":"n","Kind":0,"Value":"1"}""")]                  // nul non permis
    [InlineData("""{"op":9,"ServiceName":"PresentMonService","Action":5}""")]                         // action hors plage
    public void Une_charge_incomplete_ou_hors_plage_ne_se_lie_pas(string json)
        => PrivilegedOpCodec.Deserialize(json).IsOk.Should().BeFalse();

    [Fact]
    public void Toute_op_serialisee_se_relit_a_l_identique()
    {
        PrivilegedOp[] ops =
        [
            new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SOFTWARE\X", "n", RegistryValueType.DWord, "1"),
            new RunServiceControlOp("PresentMonService", ServiceAction.Start),
            new ApplyUploadCapOp("League of Legends.exe", 400_000_000),
            new PingOp(),
        ];
        foreach (var op in ops)
            PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(op).Value).Value.Should().Be(op);
    }
}
