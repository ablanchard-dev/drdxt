using System.Text;
using DrDxt.Bios;
using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Bios.Tests;

/// <summary>
/// Le magasin des valeurs d'usine AMI se décode par ses en-têtes NVAR, jamais par une recherche de chaîne, et la
/// comparaison couvre toutes les variables que la table nomme. Magasins synthétiques, construits selon le format de
/// UEFITool (ami_nvar.ksy) ; le même décodage a été mesuré sur la carte de référence le 17/10/2026.
/// </summary>
public class NvarAmiTests
{
    private const string GuidSetup = "{ec87d643-eba4-4bb5-a1e5-3f3e36b20da9}";
    private const string GuidCpu = "{b08f97ff-e6e8-4193-a997-5e9e9b0adb32}";
    // GUID inventé : aucun GUID AMD n'est relevé.
    private const string GuidAmd = "{7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d}";

    /// <summary>Un enregistrement complet : nom ASCII, GUID par index dans la table de fin de magasin.</summary>
    private static byte[] Enregistrement(string nom, byte indexGuid, byte[] donnees, int lien = 0xFFFFFF, byte attributs = 0x83)
    {
        var corps = new List<byte> { indexGuid };
        corps.AddRange(Encoding.ASCII.GetBytes(nom));
        corps.Add(0);
        corps.AddRange(donnees);
        return EnTete(corps.Count, lien, attributs).Concat(corps).ToArray();
    }

    private static byte[] EnTete(int tailleCorps, int lien, byte attributs)
    {
        var e = new byte[10];
        "NVAR"u8.CopyTo(e);
        BitConverter.GetBytes((ushort)(10 + tailleCorps)).CopyTo(e, 4);
        BitConverter.GetBytes((uint)(lien & 0xFFFFFF) | ((uint)attributs << 24)).CopyTo(e, 6);
        return e;
    }

    /// <summary>Les enregistrements, l'espace libre, puis la table des GUID rangée depuis la fin (index 0 en dernier).</summary>
    private static byte[] Magasin(IEnumerable<byte[]> enregistrements, params string[] guids)
    {
        var m = enregistrements.SelectMany(x => x).ToList();
        m.AddRange(Enumerable.Repeat((byte)0xFF, 32));
        foreach (var g in guids.Reverse()) m.AddRange(Guid.Parse(g).ToByteArray());
        return m.ToArray();
    }

    [Fact]
    public void Chaque_enregistrement_rend_son_nom_son_GUID_tire_de_la_table_de_fin_et_ses_octets()
    {
        var m = Magasin([Enregistrement("Setup", 0, [1, 2, 3]), Enregistrement("CpuSetup", 1, [9])], GuidSetup, GuidCpu);

        var e = NvarAmi.Decoder(m)!;

        e.Select(x => (x.Nom, x.Guid)).Should().Equal(("Setup", Guid.Parse(GuidSetup)), ("CpuSetup", Guid.Parse(GuidCpu)));
        NvarAmi.Trouver(e, "Setup", GuidSetup).Should().Equal(1, 2, 3);
        NvarAmi.Trouver(e, "CpuSetup", GuidCpu).Should().Equal(9);
    }

    [Fact]
    public void AmdSetup_place_avant_Setup_ne_se_prend_jamais_pour_Setup()
    {
        // Chercher « Setup » suivi d'un octet nul trouverait la fin de « AmdSetup » : seul le décodage des en-têtes
        // sépare les deux.
        var m = Magasin([Enregistrement("AmdSetup", 1, [0xAA, 0xAA]), Enregistrement("Setup", 0, [1, 2])], GuidSetup, GuidAmd);

        var e = NvarAmi.Decoder(m)!;

        NvarAmi.Trouver(e, "Setup", GuidSetup).Should().Equal(1, 2);
        NvarAmi.Trouver(e, "Setup", GuidAmd).Should().BeNull("le même nom sous un autre GUID est une autre variable");
    }

    [Fact]
    public void Un_GUID_local_et_un_nom_UCS2_se_decodent()
    {
        var corps = new List<byte>(Guid.Parse(GuidSetup).ToByteArray());
        corps.AddRange(Encoding.Unicode.GetBytes("Setup\0"));
        corps.AddRange(new byte[] { 7 });
        var m = Magasin([EnTete(corps.Count, 0xFFFFFF, 0x80 | 0x04).Concat(corps).ToArray()]);

        NvarAmi.Trouver(NvarAmi.Decoder(m)!, "Setup", GuidSetup).Should().Equal(7);
    }

    [Fact]
    public void Un_enregistrement_lie_rend_les_donnees_du_dernier_maillon()
    {
        var premier = Enregistrement("Setup", 0, [1, 1]);
        // Le lien compte depuis le début de l'enregistrement qui le porte.
        premier = Enregistrement("Setup", 0, [1, 1], lien: premier.Length);
        var maillon = EnTete(2, 0xFFFFFF, 0x80 | 0x08).Concat(new byte[] { 5, 6 }).ToArray();

        NvarAmi.Trouver(NvarAmi.Decoder(Magasin([premier, maillon], GuidSetup))!, "Setup", GuidSetup).Should().Equal(5, 6);
    }

    [Fact]
    public void L_en_tete_etendu_ne_fait_pas_partie_des_donnees()
    {
        // Données 4, 4 puis l'en-tête étendu : attributs étendus (1 octet) et sa taille sur deux octets (3).
        var r = Enregistrement("Setup", 0, [4, 4, 0x00, 3, 0], attributs: 0x83 | 0x10);

        NvarAmi.Trouver(NvarAmi.Decoder(Magasin([r], GuidSetup))!, "Setup", GuidSetup).Should().Equal(4, 4);
    }

    [Fact]
    public void Un_magasin_casse_ne_rend_rien_plutot_qu_une_partie()
    {
        var bon = Enregistrement("Setup", 0, [1]);
        var signature = bon.ToArray();
        signature[1] = (byte)'X';
        var deborde = bon.ToArray();
        BitConverter.GetBytes((ushort)500).CopyTo(deborde, 4);

        NvarAmi.Decoder(Magasin([signature], GuidSetup)).Should().BeNull();
        NvarAmi.Decoder(Magasin([deborde], GuidSetup)).Should().BeNull();
        NvarAmi.Decoder(Magasin([Enregistrement("Setup", 5, [1])], GuidSetup)).Should().BeNull("l'index 5 sort de la table des GUID");
    }

    [Fact]
    public void Une_variable_en_double_n_est_pas_l_usine()
    {
        var m = Magasin([Enregistrement("Setup", 0, [1]), Enregistrement("Setup", 0, [2])], GuidSetup);

        NvarAmi.Trouver(NvarAmi.Decoder(m)!, "Setup", GuidSetup).Should().BeNull();
    }

    private static NvramLocation Octet(string var, string guid, int taille, int offset, params (byte, string)[] valeurs) =>
        new(var, guid, taille, offset, valeurs.ToDictionary(v => v.Item1, v => v.Item2), Default: null);

    [Fact]
    public void Chaque_variable_nommee_est_comparee_les_ecarts_nommes_sont_dits_et_les_autres_comptes()
    {
        var table = new List<(string, NvramLocation)>
        {
            ("XMP", Octet("Setup", GuidSetup, 4, 1, (0, "Disabled"), (1, "Profile 1"))),
            ("Turbo", Octet("CpuSetup", GuidCpu, 2, 0, (0, "Disabled"), (1, "Enabled"))),
        };
        var usine = NvarAmi.Decoder(Magasin([Enregistrement("Setup", 0, [0, 0, 0, 0]), Enregistrement("CpuSetup", 1, [1, 0])],
            GuidSetup, GuidCpu))!;
        var vivantes = new Dictionary<(string, string), byte[]>
        {
            [("Setup", GuidSetup)] = [0, 1, 0, 9],
            [("CpuSetup", GuidCpu)] = [0, 0],
        };

        var e = EcartsAUsine.Comparer(table, vivantes, usine)!;

        e.Nommes.Should().Equal(new EcartAUsine("XMP", "Profile 1", "Disabled"), new EcartAUsine("Turbo", "Disabled", "Enabled"));
        e.AutresOctets.Should().Be(1);
        e.OctetsCompares.Should().Be(6);
    }

    [Fact]
    public void Une_taille_qui_differe_ou_une_variable_absente_du_magasin_ne_rend_rien()
    {
        var table = new List<(string, NvramLocation)> { ("XMP", Octet("Setup", GuidSetup, 4, 1, (0, "Disabled"))) };
        var usine = NvarAmi.Decoder(Magasin([Enregistrement("Setup", 0, [0, 0, 0])], GuidSetup))!;
        var vivantes = new Dictionary<(string, string), byte[]> { [("Setup", GuidSetup)] = [0, 0, 0, 0] };

        EcartsAUsine.Comparer(table, vivantes, usine).Should().BeNull();
        EcartsAUsine.Comparer(table, vivantes, []).Should().BeNull();
    }

    // Pur et tout ou rien : un emplacement de table qui sort de sa variable rend null, jamais une exception.
    [Fact]
    public void Un_emplacement_hors_de_sa_variable_rend_null_sans_lever()
    {
        var table = new List<(string, NvramLocation)> { ("XMP", Octet("Setup", GuidSetup, 4, 3, (0, "Disabled")) with { Taille = 2 }) };
        var usine = NvarAmi.Decoder(Magasin([Enregistrement("Setup", 0, [0, 0, 0, 0])], GuidSetup))!;
        var vivantes = new Dictionary<(string, string), byte[]> { [("Setup", GuidSetup)] = [0, 0, 0, 1] };

        EcartsAUsine.Comparer(table, vivantes, usine).Should().BeNull();
        var negatif = new List<(string, NvramLocation)> { ("XMP", Octet("Setup", GuidSetup, 4, -1, (0, "Disabled"))) };
        EcartsAUsine.Comparer(negatif, vivantes, usine).Should().BeNull();
    }

    // Données hostiles : une chaîne qui boucle sur elle-même, un en-tête étendu plus grand que l'enregistrement, un nom sans
    // fin : le magasin entier est refusé, sans boucle infinie ni lecture hors bornes.
    [Fact]
    public void Un_magasin_hostile_est_refuse_en_entier()
    {
        var boucle = EnTete(2, 0, 0x80 | 0x08).Concat(new byte[] { 5, 6 }).ToArray();
        var premier = Enregistrement("Setup", 0, [1, 1], lien: 0);
        NvarAmi.Decoder(Magasin([premier], GuidSetup)).Should().BeNull("un lien vers lui-même ne mène à aucune donnée");
        var versBoucle = Enregistrement("Setup", 0, [1, 1]);
        versBoucle = Enregistrement("Setup", 0, [1, 1], lien: versBoucle.Length);
        NvarAmi.Decoder(Magasin([versBoucle, boucle], GuidSetup)).Should().BeNull("un maillon qui renvoie à lui-même");

        var etenduTropGrand = Enregistrement("Setup", 0, [4, 4, 0x00, 200, 0], attributs: 0x83 | 0x10);
        NvarAmi.Decoder(Magasin([etenduTropGrand], GuidSetup)).Should().BeNull();

        var sansFin = EnTete(6, 0xFFFFFF, 0x83).Concat(new byte[] { 0, (byte)'S', (byte)'e', (byte)'t', (byte)'u', (byte)'p' }).ToArray();
        NvarAmi.Decoder(Magasin([sansFin], GuidSetup)).Should().BeNull();
        NvarAmi.Decoder([]).Should().BeEmpty();
    }

    [Fact]
    public async Task Le_lecteur_compare_la_carte_de_la_matrice_au_magasin_de_son_firmware()
    {
        // La carte de référence MSI : XMP (0xE65) passé de Disabled à Profile 1, PL1 (CpuSetup 0x17) de Auto à 160 W,
        // comme relevé le 17/10/2026.
        var setup = new byte[0x13D9];
        var cpu = new byte[0x3C1];
        var setupUsine = setup.ToArray();
        var cpuUsine = cpu.ToArray();
        setup[0xE65] = 1;
        BitConverter.GetBytes(160u).CopyTo(cpu, 0x17);
        var magasin = Magasin([Enregistrement("Setup", 0, setupUsine), Enregistrement("CpuSetup", 1, cpuUsine)], GuidSetup, GuidCpu);
        var lus = new List<string>();
        var lecteur = new NvramBiosSettingsReader((nom, guid, _) =>
        {
            lus.Add(nom);
            return Task.FromResult(Result<byte[]>.Ok(nom switch { "StdDefaults" => magasin, "Setup" => setup, _ => cpu }));
        });

        var e = (await lecteur.LireEcartsAsync("PRO B760-P WIFI DDR4 (MS-7D98)", "1.H0"))!;

        lus.Should().Equal("StdDefaults", "Setup", "CpuSetup");
        e.Nommes.Should().Contain(new EcartAUsine("XMP", "Profile 1", "Disabled"));
        e.Nommes.Should().Contain(new EcartAUsine("Long Duration Power Limit (PL1)", "160 W", "Auto"));
        e.AutresOctets.Should().Be(0);
        (await lecteur.LireEcartsAsync("Une autre carte", "1.H0")).Should().BeNull();
    }
}
