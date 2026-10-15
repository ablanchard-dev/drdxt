using System.Diagnostics;
using DrDxt.Detection.Bios;
using DrDxt.Detection.Cpu;
using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit.Abstractions;

namespace DrDxt.Detection.Tests.Repair;

// -----------------------------------------------------------------------
// Ce qui coute dans un audit — la question laissee ouverte deux tours.
//
// Rappel des faits mesures :
//   * tour 116 : cinq tests d'audit tiennent un plateau de 28,3 s **a 0,2 s pres** en
//     execution de toute la solution. Un plateau identique qui s'aggrave sous contention
//     designe une ressource systeme serialisee ;
//   * tour 118 : la source WMI, seul lecteur sans couture, en a recu une — et la mesure a
//     REFUTE l'hypothese : les cinq tests ne gagnent que 0,5 s. Cinq lecteurs restaient
//     cables, tous suspects, aucun accuse.
//
// Ce fichier ne suppose plus : il **chronometre chaque lecteur**. Un seul appel chacun, le
// temps ecrit dans la sortie du test. Ce n'est pas une garde de performance — ce serait une
// decoration, la duree depend de la machine et de la charge. C'est un INSTRUMENT, qui rend
// la question mesurable au lieu de la laisser ouverte une troisieme fois.
//
// Ce que le test AFFIRME, lui, ne depend pas du chronometre : chacun de ces lecteurs doit
// rendre une reponse sans jeter, sur n'importe quelle machine — y compris sans NVIDIA, sans
// interface BIOS constructeur, sans compteur de frequence.
// -----------------------------------------------------------------------
public sealed class CeQuiCouteDansUnAuditTests(ITestOutputHelper sortie)
{
    private double Chronometrer(string nom, Action geste)
    {
        var chrono = Stopwatch.StartNew();
        geste();
        chrono.Stop();
        sortie.WriteLine($"{nom,-28} {chrono.Elapsed.TotalMilliseconds,9:0.0} ms");
        return chrono.Elapsed.TotalMilliseconds;
    }

    [Fact]
    public void Chaque_lecteur_cable_rend_une_reponse_et_dit_ce_qu_il_coute()
    {
        // Les cinq lecteurs les plus lents. L'ordre est celui de `Lire()`.
        var mesures = new Dictionary<string, double>
        {
            ["PowerSettingsReader"] = Chronometrer("PowerSettingsReader",
                // Le GUID « equilibre » de Windows : un plan qui existe sur toute machine.
                () => new PowerSettingsReader()
                    .LireProcesseur(Guid.Parse("381b4222-f694-41f0-9685-ff5bb260df2e")).ToList()),
            ["CpuFrequencyReader"] = Chronometrer("CpuFrequencyReader",
                () => new CpuFrequencyReader().Lire()),
            ["MmcssReader"] = Chronometrer("MmcssReader",
                () => new MmcssReader().Lire().ToList()),
            ["VendorBiosInterfaceProbe"] = Chronometrer("VendorBiosInterfaceProbe",
                () => new VendorBiosInterfaceProbe().Chercher().ToList()),
            ["NvmlHardwareProbe"] = Chronometrer("NvmlHardwareProbe",
                () => new NvmlHardwareProbe().Lire()),
        };

        // Ce qui est AFFIRME : aucun de ces lecteurs ne jette, quelle que soit la machine.
        // Un audit qui explose sur un PC sans NVIDIA ne rendrait aucun constat du tout.
        mesures.Should().HaveCount(5, "les cinq lecteurs cables doivent tous repondre");

        var total = mesures.Values.Sum();
        sortie.WriteLine($"{"TOTAL",-28} {total,9:0.0} ms");
        sortie.WriteLine("(mesure indicative : la duree depend de la machine et de la charge)");
    }
}
