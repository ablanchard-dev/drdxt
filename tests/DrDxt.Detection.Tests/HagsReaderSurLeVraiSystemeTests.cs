using System.Diagnostics;
using System.Text.RegularExpressions;
using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests;

// 26/09/2026 : l'ordonnancement GPU matériel est lu par D3DKMT, sans test sur le vrai système. Relevé à la
// main le même jour : la clé HwSchMode est absente de la machine de référence, et dxdiag dit « Enabled:True »
// comme le lecteur. Ce test refait la confrontation : le lecteur rend ce que dxdiag rapporte.
public sealed class HagsReaderSurLeVraiSystemeTests
{
    [Fact]
    [Trait("Category", "Machine")] // lance dxdiag (une dizaine de secondes) et lit la vraie carte graphique
    public void L_etat_lu_est_celui_que_dxdiag_rapporte()
    {
        var fichier = Path.Combine(Path.GetTempPath(), $"dxdiag-{Guid.NewGuid():N}.txt");
        try
        {
            using (var p = Process.Start(new ProcessStartInfo("dxdiag", $"/t \"{fichier}\"") { UseShellExecute = false, CreateNoWindow = true })!)
                p.WaitForExit(60_000).Should().BeTrue("dxdiag doit rendre son rapport");

            var rapport = File.ReadAllText(fichier);
            var ligne = Regex.Match(rapport, @"Hardware Scheduling:.*Enabled:(True|False)");
            ligne.Success.Should().BeTrue("temoin : dxdiag rapporte l'ordonnancement matériel");
            var attendu = ligne.Groups[1].Value == "True" ? TriState.On : TriState.Off;

            new D3dkmtHagsReader().Lire().Should().Be(attendu);
        }
        finally
        {
            if (File.Exists(fichier)) File.Delete(fichier);
        }
    }
}
