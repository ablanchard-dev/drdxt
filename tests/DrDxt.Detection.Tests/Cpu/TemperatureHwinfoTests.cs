using DrDxt.Core;
using System.Text;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// La température du processeur lue dans la mémoire partagée de HWiNFO, construite ici octet
// par octet selon la disposition du SDK (relevés de 316 octets, ou plus longs dans les versions récentes).
public sealed class TemperatureHwinfoTests
{
    private static readonly DateTimeOffset Maintenant = new(2026, 10, 4, 6, 0, 0, TimeSpan.Zero);

    private static byte[] Bloc(int tailleReleve, params (uint Type, string Libelle, double Valeur)[] releves)
    {
        const int debut = 64;
        var b = new byte[debut + tailleReleve * releves.Length];
        BitConverter.GetBytes(0x53695748u).CopyTo(b, 0);
        BitConverter.GetBytes(Maintenant.AddSeconds(-2).ToUnixTimeSeconds()).CopyTo(b, 12);
        BitConverter.GetBytes((uint)debut).CopyTo(b, 32);
        BitConverter.GetBytes((uint)tailleReleve).CopyTo(b, 36);
        BitConverter.GetBytes((uint)releves.Length).CopyTo(b, 40);
        for (var i = 0; i < releves.Length; i++)
        {
            var o = debut + tailleReleve * i;
            BitConverter.GetBytes(releves[i].Type).CopyTo(b, o);
            Encoding.ASCII.GetBytes(releves[i].Libelle).CopyTo(b, o + 12);
            BitConverter.GetBytes(releves[i].Valeur).CopyTo(b, o + 284);
        }
        return b;
    }

    [Fact]
    public void Le_paquet_du_processeur_est_lu_parmi_les_autres_temperatures()
    {
        var b = Bloc(316,
            (1, "GPU Temperature", 61.0),
            (6, "CPU Package", 4800.0),        // une fréquence porte le même libellé ailleurs : le type compte
            (1, "Core Max", 84.0),
            (1, "CPU Package", 82.5));

        var lu = TemperatureHwinfo.Analyser(b, Maintenant);

        lu.Should().NotBeNull();
        lu!.Celsius.Should().Be(82.5, "« CPU Package » passe avant « Core Max »");
        lu.Libelle.Should().Be("CPU Package");
    }

    [Fact]
    public void Un_releve_plus_long_des_versions_recentes_se_lit_par_la_taille_de_l_en_tete()
        => TemperatureHwinfo.Analyser(Bloc(460, (1, "CPU (Tctl/Tdie)", 71.0)), Maintenant)!.Celsius.Should().Be(71.0);

    // HWiNFO figé garde son bloc ; une valeur de plus de 15 s ne se dit pas « au moment du scan ».
    [Fact]
    public void Une_publication_perimee_ne_se_lit_pas()
    {
        var b = Bloc(316, (1, "CPU Package", 70.0));
        TemperatureHwinfo.Analyser(b, Maintenant).Should().NotBeNull("témoin : publiée il y a 2 s");
        TemperatureHwinfo.Analyser(b, Maintenant.AddMinutes(1)).Should().BeNull();
    }

    [Fact]
    public void Sans_signature_ou_sans_capteur_du_processeur_rien_n_est_affirme()
    {
        var mort = Bloc(316, (1, "CPU Package", 80.0));
        Encoding.ASCII.GetBytes("DEAD").CopyTo(mort, 0);
        TemperatureHwinfo.Analyser(mort, Maintenant).Should().BeNull("HWiNFO arrêté marque son bloc");
        TemperatureHwinfo.Analyser(Bloc(316, (1, "GPU Temperature", 60.0)), Maintenant).Should().BeNull();
        TemperatureHwinfo.Analyser(Bloc(316, (1, "CPU Package", 400.0)), Maintenant).Should().BeNull("400 °C est une disposition mal lue");
        TemperatureHwinfo.Analyser(new byte[10], Maintenant).Should().BeNull();
    }

    // HWiNFO publie les ventilateurs de presque toutes les cartes (type 3, tr/min).
    // Gardés avec leur nom HWiNFO, numérotés à partir de 100 ; HWiNFO ne donne pas le cycle : inconnu, jamais inventé.
    [Fact]
    public void Les_ventilateurs_publies_par_HWiNFO_se_lisent_avec_leur_nom()
    {
        var b = Bloc(316, (1, "CPU Package", 70.0), (3, "CPU", 1180.4), (3, "Chassis1", 0.0), (7, "Chassis1", 45.0), (3, "Chassis2", 950.0));

        var v = TemperatureHwinfo.AnalyserVentilateurs(b, Maintenant)!;

        v.Should().Equal(new VentilateurLu(100, 1180, null, "CPU"), new VentilateurLu(101, 0, null, "Chassis1"), new VentilateurLu(102, 950, null, "Chassis2"));
        TemperatureHwinfo.AnalyserVentilateurs(Bloc(316, (1, "CPU Package", 70.0)), Maintenant).Should().BeNull("aucun ventilateur publié");
    }

    // Les ventilateurs de la carte graphique (mode 0 tr/min) ne se jugent pas comme ceux du boîtier.
    [Fact]
    public void Les_ventilateurs_de_la_carte_graphique_sont_ecartes()
    {
        var releves = Bloc(316, (3, "Chassis1", 900.0), (3, "GPU Fan1", 0.0));
        const int tailleCapteur = 264;
        var b = new byte[releves.Length + 2 * tailleCapteur];
        releves.CopyTo(b, 0);
        BitConverter.GetBytes((uint)releves.Length).CopyTo(b, 20);
        BitConverter.GetBytes((uint)tailleCapteur).CopyTo(b, 24);
        BitConverter.GetBytes(2u).CopyTo(b, 28);
        Encoding.ASCII.GetBytes("MSI MAG B760 (Nuvoton NCT6687D)").CopyTo(b, releves.Length + 8);
        Encoding.ASCII.GetBytes("GPU [#0]: NVIDIA GeForce RTX 4060").CopyTo(b, releves.Length + tailleCapteur + 8);
        BitConverter.GetBytes(1u).CopyTo(b, 64 + 316 + 4);

        TemperatureHwinfo.AnalyserVentilateurs(b, Maintenant).Should().Equal(new VentilateurLu(100, 900, null, "Chassis1"));
    }
}
