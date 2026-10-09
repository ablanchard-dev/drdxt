using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

// ─────────────────────────────────────────────────────────────────────────────
// La capacité VRR d'un écran — la seule lecture qui manquait, et qui ferme deux leviers.
//
// `SettingsState.VrrCapableDisplay` valait `Unknown` depuis le début, et le contrat le disait :
// « stays Unknown until display-capability detection is wired ». `latency.vrr_vsync_cap` et
// `latency.vrrFlipModelOn` en dépendent, donc se taisaient — sur un écran de jeu à 200 Hz.
//
// Les deux fixtures sont de vrais EDID, lus dans le registre de la machine de référence le
// 18/09/2026. Pas des octets fabriqués pour faire passer un test : un écran qui déclare FreeSync
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

    /// <summary>Samsung 144 Hz, second écran. Plage 50-144 Hz, et aucun bloc VRR.</summary>
    private const string SamsungSansVrr =
        "00FFFFFFFFFFFF004C2DD97159365734121F010380351E782ACB75A656539D27125054BFEF80714F810081C081809500" +
        "A9C0B3000101B37D806A703817402C2035000F282100001E000000FD0032901E9F22000A202020202020000000FC00" +
        "4C53323441473332780A202020000000FF0048394A544330323931362020200106";

    private static byte[] Octets(string hex) => Convert.FromHexString(hex);

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
        EdidVrrCapabilite.Lire(Octets(SamsungSansVrr)).Should().Be(TriState.Off);
    }

    [Fact]
    public void La_plage_de_rafraichissement_seule_ne_prouve_RIEN()
    {
        // Le piege de cette lecture, et la raison pour laquelle elle a l'air facile. Le
        // descripteur « Monitor Range Limits » (0xFD) existe sur tous les ecrans : le Samsung en
        // porte un, 50-144 Hz, et n'est pas VRR pour autant. S'en servir ferait dire « capable »
        // a n'importe quel 144 Hz — exactement le genre de deduction que ce produit s'interdit.
        var samsung = Octets(SamsungSansVrr);
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
}
