using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DrDxt.Detection.Settings;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Settings;

// 27/09/2026 : les reglages processeur du plan actif (minimum, maximum, boost, parking...) sont ceux que
// l'audit montre au joueur et compare au defaut. Le lecteur n'etait exerce que sur des doublures. Il doit
// rendre, pour chaque reglage, l'« index actuel en courant alternatif » que powercfg affiche.
// Lecture seule ; powercfg existe sur tout Windows, runner de CI compris.
public sealed class PowerSettingsReaderSurLeVraiSystemeTests
{
    private static readonly Guid SousGroupeProcesseur = new("54533251-82be-4824-96c1-47b60b740d00");

    private static string Powercfg(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe"), arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        })!;
        var sortie = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return sortie;
    }

    /// <summary>Les deux dernieres valeurs hexadecimales d'une fiche powercfg sont l'index actuel en
    /// courant alternatif puis en continu (les libelles sont traduits, l'ordre ne l'est pas). /qh : les
    /// reglages masques (boost, parking) n'apparaissent pas avec /q.
    /// <c>null</c> quand powercfg ne montre pas ce reglage (masque sur ce Windows).</summary>
    private static uint? IndexAlternatif(Guid plan, Guid reglage)
    {
        var sortie = Powercfg($"/qh {plan} {SousGroupeProcesseur} {reglage}");
        var hex = Regex.Matches(sortie, @":\s*0x([0-9a-fA-F]{8})\s*$", RegexOptions.Multiline);
        return hex.Count >= 2
            ? uint.Parse(hex[^2].Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : null;
    }

    [Fact]
    public void Chaque_reglage_lu_est_celui_que_powercfg_affiche()
    {
        var actif = Regex.Match(Powercfg("/getactivescheme"),
            "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        actif.Success.Should().BeTrue("temoin : powercfg a rendu le plan actif");
        var plan = Guid.Parse(actif.Value);

        var lus = new PowerSettingsReader().LireProcesseur(plan, plan);

        var compares = 0;
        foreach (var (nom, guid, _) in PowerSettingsReader.ReglagesProcesseur)
        {
            var attendu = IndexAlternatif(plan, guid);
            if (attendu is null) continue;
            lus.Single(l => l.Nom == nom).ValeurAc.Should().Be(attendu, $"« {nom} » doit valoir ce que powercfg affiche");
            compares++;
        }
        compares.Should().BeGreaterThanOrEqualTo(4, "temoin : powercfg montre au moins les reglages de base");
    }
}
