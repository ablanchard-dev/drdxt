using System.Text;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// Le codec est total : quels que soient les caractères reçus, il rend un échec ou une feuille fermée, jamais une
// exception. La graine est fixe : un échec se rejoue à l'identique.
public sealed class CodecSousMutationTests
{
    private const int Graine = 0x0D5D7;
    private const int MutationsParCharge = 1500;

    private static readonly PrivilegedOp[] Corpus =
    [
        new PingOp(),
        new SetRegistryValueOp(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "2"),
        new SetActivePowerPlanOp(KnownPowerPlans.HighPerformance),
        new RunServiceControlOp("AMD3DVCacheSvc", ServiceAction.Start),
        new ApplyGpuFanCurveOp(0, [new FanCurvePointDto(50, 60), new FanCurvePointDto(80, 80)]),
        new EstablishLeaseOp("bail", """{"op":3,"PlanGuid":"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"}""", 60),
        new CommitManifestOp([], [], Retirer: ["os.hags"]),
    ];

    [Fact]
    public void Aucune_mutation_ne_fait_lever_et_toute_op_liee_se_relit_a_l_identique()
    {
        var alea = new Random(Graine);
        var liees = 0;
        var refusees = 0;

        foreach (var op in Corpus)
        {
            var json = PrivilegedOpCodec.Serialize(op).Value!;
            for (var i = 0; i < MutationsParCharge; i++)
            {
                var mutee = Muter(alea, json);
                var lue = Lire(mutee);
                if (!lue.IsOk) { refusees++; continue; }

                liees++;
                var serialisee = PrivilegedOpCodec.Serialize(lue.Value!).Value!;
                PrivilegedOpCodec.Serialize(Lire(serialisee).Value!).Value.Should().Be(serialisee, mutee);
            }
        }

        // Sans ce témoin, un corpus qui ne se lierait jamais ne prouverait rien.
        liees.Should().BeGreaterThan(0);
        refusees.Should().BeGreaterThan(liees);
    }

    // En boucle et non en InlineData : un attribut ne transporte pas un surrogat isolé intact.
    [Fact]
    public void Un_surrogat_isole_est_un_refus_pas_une_exception()
    {
        foreach (var json in new[] { "\uD800", "\uDC00", "{\"op\":0}\uD800", "{\"op\":1,\"Path\":\"\uD83D\"}" })
            Lire(json).IsOk.Should().BeFalse();
    }

    [Theory]
    [InlineData("""{"op":33}""")]                      // trou de l'énumération
    [InlineData("""{"op":39}""")]
    [InlineData("""{"op":50}""")]
    [InlineData("""{"op":-1}""")]
    [InlineData("""{"op":2147483648}""")]
    [InlineData("""{"op":1e400}""")]
    [InlineData("""{"op":0.5}""")]
    [InlineData("""{"op":"0"}""")]
    [InlineData("""{"op":"Ping"}""")]
    [InlineData("""{"op":null}""")]
    [InlineData("""{"op":[0]}""")]
    [InlineData("""{"Op":0}""")]
    [InlineData("""{"$type":"System.Diagnostics.Process","op":1}""")]
    [InlineData("""{"op":3,"op":99}""")]               // le dernier discriminant est hors liste
    [InlineData("""[{"op":0}]""")]
    [InlineData("null")]
    [InlineData("0")]
    public void Un_discriminant_hors_liste_ne_se_lie_jamais(string json)
        => Lire(json).IsOk.Should().BeFalse();

    [Fact]
    public void Deux_discriminants_ne_donnent_jamais_une_feuille_liee_avec_l_autre_type()
    {
        // Le registre sous le discriminant du ping, puis l'inverse : seule une feuille cohérente avec un discriminant passe.
        var lue = Lire("""{"op":1,"op":0,"Hive":0,"Path":"p","Name":"n","Kind":0,"Value":"1"}""");
        if (lue.IsOk) lue.Value.Should().BeOfType<PingOp>();

        var autre = Lire("""{"op":0,"op":1,"Hive":0,"Path":"p","Name":"n","Kind":0,"Value":"1"}""");
        if (autre.IsOk) autre.Value.Should().BeOfType<SetRegistryValueOp>();
    }

    [Theory]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100_000)]
    public void Une_charge_trop_profonde_est_refusee(int profondeur)
    {
        Lire($"{{\"op\":1,\"Path\":{new string('[', profondeur)}{new string(']', profondeur)}}}").IsOk.Should().BeFalse();
        Lire(string.Concat(Enumerable.Repeat("{\"a\":", profondeur)) + "0" + new string('}', profondeur)).IsOk.Should().BeFalse();
    }

    [Fact]
    public void Une_chaine_enorme_se_lit_ou_se_refuse_sans_lever()
    {
        var enorme = new string('A', 8 * 1024 * 1024);
        Lire($"{{\"op\":1,\"Hive\":0,\"Path\":\"{enorme}\",\"Name\":\"n\",\"Kind\":0,\"Value\":\"1\"}}").IsOk.Should().BeTrue();
        Lire($"{{\"op\":1,\"Hive\":0,\"Path\":\"{enorme}").IsOk.Should().BeFalse();
        Lire($"{{\"op\":0,\"{enorme}\":1}}").IsOk.Should().BeTrue("un ping sans champ ne lit aucun de ceux qu'on lui ajoute");
    }

    private static Result<PrivilegedOp> Lire(string json)
    {
        Result<PrivilegedOp>? lue = null;
        var lire = () => { lue = PrivilegedOpCodec.Deserialize(json); };
        lire.Should().NotThrow();
        return lue!;
    }

    private static string Muter(Random alea, string json)
    {
        if (alea.Next(4) == 0)
        {
            // Au niveau des caractères : un surrogat isolé n'existe pas dans un UTF-8 décodé.
            var c = new StringBuilder(json);
            c[alea.Next(c.Length)] = (char)alea.Next(0xD800, 0xE000);
            return c.ToString();
        }

        var octets = Encoding.UTF8.GetBytes(json).ToList();
        for (var n = alea.Next(1, 4); n > 0 && octets.Count > 0; n--)
        {
            var i = alea.Next(octets.Count);
            switch (alea.Next(4))
            {
                case 0: octets[i] ^= (byte)(1 << alea.Next(8)); break;
                case 1: octets[i] = (byte)alea.Next(256); break;
                case 2: octets.RemoveAt(i); break;
                default: octets.RemoveRange(i, octets.Count - i); break;
            }
        }
        return Encoding.UTF8.GetString(octets.ToArray());
    }
}
