using DrDxt.Application.Autopilot;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Application.Tests.Autopilot;

// Le fichier d'annulation du compte est rejoué par le helper élevé au démarrage : une pose marquée ou un acquit du journal
// d'écriture anticipée n'y sont jamais une remise en état.
public sealed class PoseMarqueeAuDemarrageTests
{
    private static readonly SetRegistryValueOp Hags =
        new(RegistryHiveKind.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", RegistryValueType.DWord, "1");
    private static string Json(PrivilegedOp op) => PrivilegedOpCodec.Serialize(op).Value!;

    [Fact]
    public void Ni_une_pose_marquee_ni_un_acquit_ne_se_rejouent_au_demarrage()
    {
        RevertAuDemarrage.InverseRejouable("latency.hagsOn", Json(Hags)).Should().BeTrue("témoin : l'inverse nu se rejoue");
        RevertAuDemarrage.InverseRejouable("latency.hagsOn", Json(new PoseEnAttenteDOrigineOp(Json(Hags)))).Should().BeFalse();
        RevertAuDemarrage.InverseRejouable("latency.hagsOn", Json(new AcquitterEcritureOp(Json(Hags)))).Should().BeFalse();
    }
}
