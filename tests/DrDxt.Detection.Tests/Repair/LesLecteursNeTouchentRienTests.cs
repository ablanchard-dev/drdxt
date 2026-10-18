using System.Text.RegularExpressions;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// Les lecteurs de stabilité, de pilotes, de Defender et de BitLocker ne font que lire : aucun ne supprime un vidage ou un
// journal, n'écrit le registre, ne pose une stratégie ou une exclusion, n'appelle une méthode WMI qui change quelque chose.
public sealed class LesLecteursNeTouchentRienTests
{
    private static readonly string[] Lecteurs =
    [
        "Hardware/DumpCreationLog.cs", "Hardware/UnexpectedShutdownLog.cs", "Hardware/SanteNvme.cs", "Hardware/ChaineAudio.cs",
        "Hardware/PressionMemoire.cs", "Repair/EcransBleus.cs", "Repair/PannesDuJournal.cs", "Repair/ErreursMaterielles.cs",
        "Repair/PreferencesDefender.cs", "Repair/InstallationDePilote.cs", "Repair/InventaireDesPilotes.cs",
        "Repair/ChiffrementDuDisque.cs", "Repair/GardeDeStabilite.cs", "Gpu/PiloteExigeParLeJeu.cs", "Gpu/MagasinDePilotes.cs",
        "Cpu/Cpuid.cs", "Cpu/EtatsDeVeille.cs", "Cpu/HorlogeQpc.cs", "Repair/AttenuationsProcesseur.cs", "Latency/RecensementParCoeur.cs", "Gpu/FenetreReBar.cs", "Hardware/CouchesDEntree.cs", "Hardware/ArbreDesPeripheriques.cs", "Hardware/PeripheriquesDEntree.cs", "Hardware/ReveilDesPeripheriques.cs", "Hardware/CadenceDesEntrees.cs", "Hardware/JeuxProuves.cs", "Repair/RapportsDePlantageCod.cs", "Repair/TemoinDIncident.cs", "Display/SuperpositionsMaterielles.cs", "Network/VitesseDeLaCarte.cs", "Gpu/PiloteGraphique32Bits.cs", "Display/CheminsDAffichage.cs", "Display/EcransDupliques.cs", "Display/CouleurDeSortie.cs", "Gpu/PiloteRTCore.cs", "Diffusion/ObsStudio.cs", "Network/EnvoisParProgramme.cs", "Network/TypeDeNat.cs",
    ];

    private static readonly Regex Ecriture = new(
        @"File\.(Delete|Move|Write\w*|AppendAll\w*)|Directory\.Delete|\.SetValue\(|DeleteValue|DeleteSubKey|CreateSubKey|writable:\s*true"
        + @"|\.Put\(|FileMode\.(Create|Append|OpenOrCreate|Truncate)|Add-MpPreference|Set-MpPreference|/delete-driver|-protectors|ClearLog|EventLog\.Delete");

    [Fact]
    public void Aucun_lecteur_ne_modifie_le_systeme()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent;
        // Témoin : le motif voit bien une suppression de vidage et une écriture de registre.
        Ecriture.IsMatch(@"File.Delete(@""C:\Windows\Minidump\a.dmp"")").Should().BeTrue();
        Ecriture.IsMatch("cle.SetValue(\"DenyDeviceIDs\", 0)").Should().BeTrue();
        foreach (var f in Lecteurs)
        {
            var lignes = File.ReadAllLines(Path.Combine(d!.FullName, "src", "DrDxt.Detection", f))
                .Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToList();
            lignes.Should().NotBeEmpty($"témoin : {f} a été lu");
            lignes.Where(l => Ecriture.IsMatch(l) && !l.Contains("pnputil /delete-driver {p.Nom}", StringComparison.Ordinal))
                .Should().BeEmpty($"{f} est un lecteur");
            lignes.Where(l => l.Contains("InvokeMethod(\"", StringComparison.Ordinal) && !l.Contains("InvokeMethod(\"Get", StringComparison.Ordinal))
                .Should().BeEmpty($"{f} n'appelle que des méthodes WMI de lecture");
        }
    }
}
