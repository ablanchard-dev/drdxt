using System.Runtime.InteropServices;
using DrDxt.Detection.Network;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Network;

/// <summary>La qualité Wi-Fi lue par wlanapi, sans la localisation.</summary>
public sealed class LecteurWifiTests
{
    // wlanapi.h (SDK 10.0.26100.0) : WLAN_REALTIME_CONNECTION_QUALITY = 6 champs de 4 octets puis les liens ;
    // un lien = UCHAR (aligné sur 4), 3 champs de 4 octets, WLAN_RATE_SET (ULONG + USHORT[126]).
    [Fact]
    public void La_disposition_suit_l_en_tete_du_SDK()
    {
        Marshal.OffsetOf<LecteurWifi.QualiteTempsReel>(nameof(LecteurWifi.QualiteTempsReel.PremierLien)).Should().Be(24);
        Marshal.OffsetOf<LecteurWifi.LienTempsReel>(nameof(LecteurWifi.LienTempsReel.FrequenceCentrale)).Should().Be(4);
        Marshal.OffsetOf<LecteurWifi.LienTempsReel>(nameof(LecteurWifi.LienTempsReel.Rssi)).Should().Be(12);
        Marshal.SizeOf<LecteurWifi.LienTempsReel>().Should().Be(272);
    }

    [Fact]
    public void Decode_le_bloc_que_rend_le_pilote()
    {
        var bloc = new LecteurWifi.QualiteTempsReel
        {
            PhyType = 10, QualiteLien = 45, NombreDeLiens = 1,
            PremierLien = new LecteurWifi.LienTempsReel { FrequenceCentrale = 2_437_000, Rssi = -77, Debits = new ushort[126] },
        };
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<LecteurWifi.QualiteTempsReel>());
        try
        {
            Marshal.StructureToPtr(bloc, p, false);
            LecteurWifi.Decoder(p, 6).Phrase().Should().Be("signal 45 % (-77 dBm), 2,4 GHz canal 6, Wi-Fi 6");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    public void Sans_lien_rien_n_est_invente()
    {
        var p = Marshal.AllocHGlobal(Marshal.SizeOf<LecteurWifi.QualiteTempsReel>());
        try
        {
            Marshal.StructureToPtr(new LecteurWifi.QualiteTempsReel { PhyType = 8, QualiteLien = 70,
                PremierLien = new LecteurWifi.LienTempsReel { Debits = new ushort[126] } }, p, false);
            LecteurWifi.Decoder(p, null).Phrase().Should().Be("signal 70 %, Wi-Fi 5");
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    [Fact]
    [Trait("Category", "Machine")] // la vraie carte Wi-Fi de cette machine
    public void Sur_la_vraie_machine_la_lecture_ne_leve_jamais()
    {
        var lu = LecteurWifi.Lire();
        if (lu is not null) lu.QualitePct.Should().BeInRange(0, 100);
    }
}
