using System.Text;
using DrDxt.Detection.Cpu;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Cpu;

// 04/10/2026 (plan 1.2) : la température du processeur lue dans la mémoire partagée de HWiNFO, construite ici octet
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

    // Revue du 04/10 (P2) : HWiNFO figé garde son bloc ; une valeur de plus de 15 s ne se dit pas « au moment du scan ».
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
}
