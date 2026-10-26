using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

// ─────────────────────────────────────────────────────────────────────────────
// La capacité VRR d'un écran, lue dans son EDID.
//
// `latency.vrr_vsync_cap` et `latency.vrrFlipModelOn` dépendent de `SettingsState.VrrCapableDisplay` :
// sans cette lecture, ils se tairaient sur un écran de jeu à 200 Hz.
//
// Les deux fixtures sont de vrais EDID, relevés dans le registre de la machine de référence,
// pas des octets fabriqués pour faire passer un test : un écran qui déclare FreeSync
// et un qui n'en déclare pas, avec la réponse que la machine donne vraiment.
//
// Sources : liste amd-gfx (AMD VSDB, regle HDMI Forum de drm/edid) et CTA-861, voir `EdidVrrCapabilite`.
// ─────────────────────────────────────────────────────────────────────────────
public sealed class EdidVrrCapabiliteTests
{
    /// <summary>ASUS 200 Hz, écran de jeu principal. Porte un AMD VSDB (FreeSync) v2.</summary>
    private const string AsusVrr =
        "00FFFFFFFFFFFF0006B31324FFFFFFFF13240104B5351E783BDF3BA454519A230D4E53254A008140818081C0A9C0D1C0" +
        "010101010101023A801871382D40582C4500132B2100001E000000FD0030C8F0F032010A202020202020000000FC00" +
        "56473234395135520A20202020000000FF0057354C4D44573030353038390A0147020334F14B90010203041112131F" +
        "3F4023090707830100006D1A0000020130C80000534D5348E6060701535325E305E321E200EA866F80A07038404030" +
        "203500132B2100001A5A8780A070384D4030203500132B2100001A60B480A070381E4030404500132B2100001A0000" +
        "0000000000000000000000000000000000000054";

    /// <summary>Samsung 144 Hz, second écran. Plage 50-144 Hz. Le registre ne garde que le bloc de base, alors que son octet
    /// 126 annonce une extension : le bloc VRR, s'il existe, serait dans cette extension.</summary>
    private const string SamsungSansVrr =
        "00FFFFFFFFFFFF004C2DD97159365734121F010380351E782ACB75A656539D27125054BFEF80714F810081C081809500" +
        "A9C0B3000101B37D806A703817402C2035000F282100001E000000FD0032901E9F22000A202020202020000000FC00" +
        "4C53323441473332780A202020000000FF0048394A544330323931362020200106";

    private static byte[] Octets(string hex) => Convert.FromHexString(hex);

    /// <summary>Le même bloc de base, sans extension annoncée : un EDID complet qui ne déclare aucun bloc VRR.</summary>
    private static byte[] SansExtension(string hex)
    {
        var o = Octets(hex);
        o[126] = 0;
        return o;
    }

    [Fact]
    public void Un_ecran_qui_declare_FreeSync_est_lu_CAPABLE()
    {
        EdidVrrCapabilite.Lire(Octets(AsusVrr)).Should().Be(TriState.On);
    }

    [Fact]
    public void Un_ecran_sans_aucun_bloc_VRR_est_lu_NON_capable()
    {
        // `Off`, pas `Unknown` : « cet ecran n'est pas VRR » est une REPONSE. La confondre
        // avec « je n'ai pas su lire » ferait taire un levier pour une machine sur laquelle il
        // n'y a rien a dire — et le produit ne saurait jamais la difference.
        EdidVrrCapabilite.Lire(SansExtension(SamsungSansVrr)).Should().Be(TriState.Off);
    }

    [Fact]
    public void La_plage_de_rafraichissement_seule_ne_prouve_RIEN()
    {
        // Le piege de cette lecture, et la raison pour laquelle elle a l'air facile. Le
        // descripteur « Monitor Range Limits » (0xFD) existe sur tous les ecrans : le Samsung en
        // porte un, 50-144 Hz, et n'est pas VRR pour autant. S'en servir ferait dire « capable »
        // a n'importe quel 144 Hz — exactement le genre de deduction que ce produit s'interdit.
        var samsung = SansExtension(SamsungSansVrr);
        samsung.Should().Contain((byte)0xFD, "la fixture doit bien contenir un descripteur de plage");
        EdidVrrCapabilite.Lire(samsung).Should().Be(TriState.Off);
    }

    [Theory]
    [InlineData("")]                                   // rien
    [InlineData("00FFFFFFFFFFFF00")]                   // en-tete seul, tronque
    [InlineData("DEADBEEFDEADBEEFDEADBEEFDEADBEEF")]   // pas un EDID
    public void Un_EDID_illisible_rend_UNKNOWN_et_jamais_Off(string hex)
    {
        // Ne pas savoir n'est pas « pas de VRR ». Rendre Off ici fermerait un levier sur la foi
        // d'une lecture ratee — le defaut que ce depot traque partout.
        EdidVrrCapabilite.Lire(Octets(hex)).Should().Be(TriState.Unknown);
    }

    [Fact]
    public void Un_bloc_HDMI_Forum_hors_plage_valide_ne_compte_pas()
    {
        // Regle HDMI 2.1 : le VRR compte quand VRRmin est dans 1-48 et VRRmax vaut 0 ou >= 100.
        // Un bloc present mais incoherent ne doit pas suffire — sinon n'importe quel ecran HDMI
        // 2.1 passerait capable.
        var bidon = EdidFabrique.AvecHdmiForum(vrrMin: 0, vrrMax: 0);
        EdidVrrCapabilite.Lire(bidon).Should().Be(TriState.Off);

        var bon = EdidFabrique.AvecHdmiForum(vrrMin: 48, vrrMax: 120);
        EdidVrrCapabilite.Lire(bon).Should().Be(TriState.On);
    }

    // Une extension annoncée mais absente : on ne sait pas, ce n'est pas « pas de VRR ». Le Samsung relevé est ce cas : son
    // extension n'est pas dans le registre.
    [Fact]
    public void Une_extension_annoncee_mais_absente_rend_UNKNOWN()
    {
        EdidVrrCapabilite.Lire(Octets(SamsungSansVrr)).Should().Be(TriState.Unknown);
        EdidVrrCapabilite.Lire(EdidFabrique.AvecHdmiForum(48, 144)[..128]).Should().Be(TriState.Unknown);
    }

    /// <summary>L'extension CTA-861 du même Samsung, relevée le 25/10/2026 par WMI (WmiMonitorDescriptorMethods, bloc 1) sur la
    /// machine de référence : vidéo, audio, haut-parleurs et un VSDB HDMI 1.4 (OUI 000C03), aucun bloc VRR.</summary>
    private const string SamsungBloc1 =
        "02031CF147903F1F04130312230907078301000067030C001000B8440474801871382D40582C45000F282100001E023A" +
        "801871382D40582C45000F282100001E0000000000000000000000000000000000000000000000000000000000000000" +
        "00000000000000000000000000000000000000000000000000000000000000BF";

    // Le registre ne garde que le bloc de base : l'extension annoncée se lit par WMI, et l'écran se juge sur l'EDID complet.
    [Fact]
    public void L_extension_absente_du_registre_se_complete_par_le_bloc_lu_ailleurs()
    {
        var complet = DrDxt.Detection.Display.EdidDuMoniteur.Completer(Octets(SamsungSansVrr), i => i == 1 ? Octets(SamsungBloc1) : null);

        complet.Should().HaveCount(256);
        EdidVrrCapabilite.Lire(complet).Should().Be(TriState.Off, "l'extension réelle ne déclare aucun bloc VRR");
        EdidVrrCapabilite.Lire(DrDxt.Detection.Display.EdidDuMoniteur.Completer(
            EdidFabrique.AvecHdmiForum(48, 144)[..128], i => EdidFabrique.AvecHdmiForum(48, 144)[128..])).Should().Be(TriState.On);
    }

    // Une extension DisplayID (tag 0x70) peut déclarer l'Adaptive-Sync d'un écran DisplayPort ; le lecteur ne la parcourt pas :
    // « non lu », jamais « pas de VRR ». Une table de blocs (0xF0) ne porte rien : elle ne change pas la réponse.
    [Theory]
    [InlineData(0x70, TriState.Unknown)]
    [InlineData(0xF0, TriState.Off)]
    public void Une_extension_d_un_autre_type_que_CTA(int tag, TriState attendu)
    {
        var edid = new byte[256];
        Octets(SamsungSansVrr).CopyTo(edid, 0);
        edid[128] = (byte)tag;

        EdidVrrCapabilite.Lire(edid).Should().Be(attendu);
    }

    // Le bloc illisible (WMI refusé, taille inattendue) : l'EDID reste tel quel, et la réponse reste « non lu ».
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Un_bloc_illisible_laisse_l_edid_tel_quel(bool tropLong)
    {
        var complet = DrDxt.Detection.Display.EdidDuMoniteur.Completer(Octets(SamsungSansVrr),
            _ => tropLong ? [.. Octets(SamsungBloc1), .. new byte[64]] : null);

        EdidVrrCapabilite.Lire(complet).Should().Be(TriState.Unknown);
    }

    // Un EDID déjà complet n'interroge rien d'autre.
    [Fact]
    public void Un_edid_complet_ne_demande_aucun_bloc()
        => DrDxt.Detection.Display.EdidDuMoniteur.Completer(Octets(AsusVrr), _ => throw new InvalidOperationException("demandé"))
            .Should().Equal(Octets(AsusVrr));
}
