using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

/// <summary>Ce qu'une lecture VRR du pilote autorise a dire, et surtout ce qu'elle
/// n'autorise pas : avec G-Sync coupe, « possible = 0 » n'est pas « ecran incapable ».</summary>
public class Win32DisplayInfoVrrTests
{
    [Theory]
    [InlineData(false, false, TriState.Unknown, TriState.Off)]   // G-Sync coupe : capacite inconnue
    [InlineData(false, true, TriState.Off, TriState.On)]         // actif mais impossible : vraiment incapable
    [InlineData(true, false, TriState.On, TriState.Off)]
    [InlineData(true, true, TriState.On, TriState.On)]
    public void Traduire(bool possible, bool actif, TriState attenduPossible, TriState attenduActif)
        => Win32DisplayInfo.Traduire(new VrrReading(possible, actif, false, false))
            .Should().Be((attenduPossible, attenduActif));

    // ── A-FS1 : FreeSync lu par ADLX, l'écran retrouvé par le nom de modèle de son EDID ──────────
    private static readonly DrDxt.Detection.Gpu.Adlx.AdlxEcranFreeSync[] DeuxEcrans =
    [
        new("VG27AQ", true, true),
        new("MAG274QRF", true, false),
    ];

    [Fact]
    public void FreeSync_actif_sur_l_ecran_du_meme_nom()
        => Win32DisplayInfo.FreeSyncDe(DeuxEcrans, "vg27aq").Should().Be((TriState.On, TriState.On));

    [Fact]
    public void FreeSync_coupe_sur_un_ecran_capable()
        => Win32DisplayInfo.FreeSyncDe(DeuxEcrans, "MAG274QRF").Should().Be((TriState.On, TriState.Off));

    [Fact]
    public void Un_ecran_sans_FreeSync_est_incapable_et_son_etat_inconnu()
        => Win32DisplayInfo.FreeSyncDe([new("PA278", false, null)], "PA278").Should().Be((TriState.Off, TriState.Unknown));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AUTRE")]
    public void Sans_nom_qui_correspond_rien_n_est_lu(string? nom)
        => Win32DisplayInfo.FreeSyncDe(DeuxEcrans, nom).Should().Be((TriState.Unknown, TriState.Unknown));

    [Fact]
    public void Deux_ecrans_du_meme_nom_ne_sont_jamais_departages_au_hasard()
        => Win32DisplayInfo.FreeSyncDe([new("VG27AQ", true, true), new("VG27AQ", true, false)], "VG27AQ")
            .Should().Be((TriState.Unknown, TriState.Unknown));

    private static byte[] EdidAvecNom(string nom, int descripteur = 2)
    {
        var edid = new byte[128];
        var d = 54 + descripteur * 18;
        edid[d + 3] = 0xFC;
        var texte = System.Text.Encoding.ASCII.GetBytes((nom + "\n").PadRight(13));
        Array.Copy(texte, 0, edid, d + 5, 13);
        return edid;
    }

    [Fact]
    public void Le_nom_de_modele_est_lu_dans_le_descripteur_0xFC()
    {
        Win32DisplayInfo.NomDuModele(EdidAvecNom("VG27AQ")).Should().Be("VG27AQ");
        Win32DisplayInfo.NomDuModele(EdidAvecNom("MAG274QRF", descripteur: 3)).Should().Be("MAG274QRF");
        Win32DisplayInfo.NomDuModele(new byte[128]).Should().BeNull("aucun descripteur de nom");
        Win32DisplayInfo.NomDuModele(null).Should().BeNull();
        Win32DisplayInfo.NomDuModele(new byte[20]).Should().BeNull("EDID tronqué");
    }
}
