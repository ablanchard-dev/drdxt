using System.Security.Cryptography;
using System.Text;
using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// La lecture des réglages d'une carte AMD : la copie que le menu montre, l'identité du firmware par son magasin
/// d'usine, et rien du tout au moindre doute. Table et variables synthétiques : aucune table réelle n'est encore entrée.
/// </summary>
public class LecteurBiosAmdTests
{
    private const string GuidSetup = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";
    // GUID inventé : aucun GUID AMD n'est relevé.
    private const string GuidAod = "{0d1c2b3a-4f5e-4a6b-8c7d-9e0f1a2b3c4d}";
    private const int Taille = 8;

    private static readonly byte[] SetupUsine = [0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] AodUsine = [0, 0, 0, 0, 0, 0, 0, 0];

    private static NvramLocation Octet(string var, string guid, int offset) =>
        new(var, guid, Taille, offset, new Dictionary<byte, string> { [0] = "Auto", [1] = "Disabled", [2] = "Enabled" }, Default: 0);

    // « Power Down » en trois copies, carte, version et menus inventés : menu A (montrée quand le mode mémoire, octet 0,
    // vaut 1), menus B et C (montrées quand il vaut 0). Le joueur règle la copie du menu A.
    private static TableBiosAmd Table(string? empreinteSetup = null) => new("CARTE AMD FICTIVE", "0042", "synthétique",
        [
            new VariableAmd("Setup", GuidSetup, Taille, empreinteSetup ?? Empreinte(SetupUsine)),
            new VariableAmd("AodSetup", GuidAod, Taille, Empreinte(AodUsine)),
        ],
        [
            new ReglageAmd("Power Down Enable",
            [
                new CopieDeReglage("Menu A > Power Down Enable", Octet("Setup", GuidSetup, 4),
                    [new ConditionDeVisibilite("Setup", GuidSetup, 0, new HashSet<byte> { 1 })]),
                new CopieDeReglage("Menu B > Power Down Enable", Octet("AodSetup", GuidAod, 2),
                    [new ConditionDeVisibilite("Setup", GuidSetup, 0, new HashSet<byte> { 0 })]),
                new CopieDeReglage("Menu C > Power Down Enable", Octet("Setup", GuidSetup, 5),
                    [new ConditionDeVisibilite("Setup", GuidSetup, 0, new HashSet<byte> { 0 })]),
            ]),
        ]);

    private static string Empreinte(byte[] b) => Convert.ToHexStringLower(SHA256.HashData(b));

    private static byte[] Enregistrement(string nom, byte index, byte[] donnees)
    {
        var corps = new List<byte> { index };
        corps.AddRange(Encoding.ASCII.GetBytes(nom + "\0"));
        corps.AddRange(donnees);
        var e = new byte[10];
        "NVAR"u8.CopyTo(e);
        BitConverter.GetBytes((ushort)(10 + corps.Count)).CopyTo(e, 4);
        BitConverter.GetBytes(0xFFFFFFu | (0x83u << 24)).CopyTo(e, 6);
        return e.Concat(corps).ToArray();
    }

    private static byte[] Magasin(byte[] setup, byte[] aod) =>
        Enregistrement("Setup", 0, setup).Concat(Enregistrement("AodSetup", 1, aod)).Concat(Enumerable.Repeat((byte)0xFF, 16))
            .Concat(Guid.Parse(GuidAod).ToByteArray()).Concat(Guid.Parse(GuidSetup).ToByteArray()).ToArray();

    private static IReadOnlyList<NvarAmi.Entree> Usine() => NvarAmi.Decoder(Magasin(SetupUsine, AodUsine))!;

    private static Dictionary<(string, string), byte[]> Vivantes(byte[] setup, byte[]? aod = null) =>
        new() { [("Setup", GuidSetup)] = setup, [("AodSetup", GuidAod)] = aod ?? new byte[Taille] };

    [Fact]
    public void La_copie_lue_est_celle_que_le_menu_montre_pas_une_copie_cachee()
    {
        // Mode mémoire manuel (octet 0 = 1) : la copie du menu A est montrée et vaut Enabled ; celle du menu B, cachée, dit Auto.
        var lus = LecteurBiosAmd.Lire(Table(), Vivantes([1, 0, 0, 0, 2, 0, 0, 0]), Usine());

        lus.Should().ContainSingle().Which.Should().Be(new BiosSettingReading("Power Down Enable", 2, "Enabled", false));
    }

    [Fact]
    public void Deux_copies_visibles_a_la_fois_jettent_toute_la_lecture()
    {
        var table = Table() with
        {
            Reglages = [Table().Reglages[0] with { Copies = [.. Table().Reglages[0].Copies.Skip(1)] }],
        };

        LecteurBiosAmd.Lire(table, Vivantes(new byte[Taille]), Usine()).Should().BeEmpty("les menus B et C sont montrés ensemble : la table est fausse");
    }

    [Fact]
    public void Un_reglage_dont_aucune_copie_n_est_montree_n_est_pas_rendu()
    {
        LecteurBiosAmd.Lire(Table(), Vivantes([7, 0, 0, 0, 0, 0, 0, 0]), Usine()).Should().BeEmpty();
    }

    [Fact]
    public void Un_firmware_dont_le_magasin_d_usine_n_a_pas_l_empreinte_de_la_table_ne_rend_rien()
    {
        // Le cas qu'un test de valeurs seul laisse passer : un firmware voisin dont les octets tombent tous dans les menus.
        LecteurBiosAmd.Lire(Table(empreinteSetup: Empreinte([9, 9, 9, 9, 9, 9, 9, 9])), Vivantes([1, 0, 0, 0, 2, 0, 0, 0]), Usine())
            .Should().BeEmpty();
        LecteurBiosAmd.Lire(Table(), Vivantes([1, 0, 0, 0, 2, 0, 0, 0]), null).Should().BeEmpty("sans magasin d'usine, pas d'identité");
    }

    [Fact]
    public void Une_variable_a_la_mauvaise_taille_ou_une_copie_cachee_hors_menu_ne_rend_rien()
    {
        LecteurBiosAmd.Lire(Table(), Vivantes([1, 0, 0, 0, 2, 0, 0]), Usine()).Should().BeEmpty("7 octets au lieu de 8");
        LecteurBiosAmd.Lire(Table(), Vivantes([1, 0, 0, 0, 2, 0, 0, 0], aod: [0, 0, 9, 0, 0, 0, 0, 0]), Usine())
            .Should().BeEmpty("la copie du menu B, même cachée, sort de son menu");
    }

    [Fact]
    public async Task Le_lecteur_du_scan_lit_la_table_de_la_carte_exacte_et_son_magasin_d_usine()
    {
        var setup = new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 };
        var magasin = Magasin(SetupUsine, AodUsine);
        var lecteur = new NvramBiosSettingsReader((nom, guid, _) => Task.FromResult(Result<byte[]>.Ok(nom switch
        {
            "Setup" => setup,
            "AodSetup" => new byte[Taille],
            _ => magasin,
        })), [Table()]);

        (await lecteur.ReadAsync("CARTE AMD FICTIVE", "0042")).Should().ContainSingle().Which.Libelle.Should().Be("Enabled");
        (await lecteur.ReadAsync("CARTE AMD FICTIVE WIFI", "0042")).Should().BeEmpty();
        var ecarts = (await lecteur.LireEcartsAsync("CARTE AMD FICTIVE", "0042"))!;
        ecarts.Nommes.Should().ContainSingle().Which.Should().Be(
            new EcartAUsine("Power Down Enable (Menu A > Power Down Enable)", "Enabled", "Auto"));
        ecarts.AutresOctets.Should().Be(1, "l'octet du mode mémoire n'est nommé par aucune copie");
    }
}
