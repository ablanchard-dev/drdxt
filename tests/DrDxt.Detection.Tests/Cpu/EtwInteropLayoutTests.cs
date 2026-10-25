using System.Diagnostics;
using DrDxt.Detection.Latency;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Cpu;

/// <summary>
/// Le SDK est l'oracle, pas notre lecture de la documentation. Une erreur d'interop ETW ne lève rien : seize octets
/// d'écart sur `EventRecordCallback` (408 au lieu de 424, `TRACE_LOGFILE_HEADER` estimé à 264 octets quand il en fait
/// 280) rendaient **zéro événement**, ce qui ressemble à « il n'y a rien à mesurer ».
///
/// Les décalages lus sur la structure se comparent aux valeurs de evntrace.h (x64) dans tous les cas ; quand
/// `tools/etwlayout` a été compilé contre le SDK installé, ils se comparent aussi à ce qu'il imprime. **Un seul octet
/// d'écart fait échouer la suite.**
/// </summary>
public class EtwInteropLayoutTests
{
    [Fact]
    public void NOS_DECALAGES_SONT_CEUX_DU_SDK_INSTALLE()
    {
        // Les valeurs de evntrace.h (x64), relevées par tools/etwlayout : la garde tient sans l'oracle.
        DpcLatencyTracer.DecalagesUtilises.Should().Equal(new Dictionary<string, int>
        {
            ["EVENT_TRACE_LOGFILEW.size"] = 448, ["LoggerName"] = 8, ["ProcessTraceMode"] = 28,
            ["PerfFreq"] = 376, ["EventRecordCallback"] = 424,
        });

        // L'oracle compilé contre le SDK installé, quand il existe (il demande Visual Studio).
        var oracle = LireOracle();
        if (oracle is null) return;

        foreach (var (nom, attendu) in DpcLatencyTracer.DecalagesUtilises)
        {
            oracle.Should().ContainKey(nom, $"l'oracle doit publier le decalage de {nom}");
            oracle[nom].Should().Be(attendu,
                $"le SDK place {nom} a {oracle[nom]} et notre interop l'utilise a {attendu}. "
                + "Un ecart ici ne leve rien a l'execution : il rend simplement zero evenement.");
        }
    }

    /// <summary>Lit la sortie de l'oracle natif, ou <c>null</c> s'il n'a pas ete compile.</summary>
    private static Dictionary<string, int>? LireOracle()
    {
        var exe = Chercher("tools/etwlayout/etwlayout.exe");
        if (exe is null) return null;

        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            });
            if (p is null) return null;
            var sortie = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);

            var valeurs = new Dictionary<string, int>();
            foreach (var ligne in sortie.Split('\n'))
            {
                var l = ligne.Trim();
                // « SDK_STRUCT EVENT_TRACE_LOGFILEW size=448 » et « SDK_OFFSET LoggerName=8 ».
                if (l.StartsWith("SDK_STRUCT EVENT_TRACE_LOGFILEW size=") && int.TryParse(
                        l["SDK_STRUCT EVENT_TRACE_LOGFILEW size=".Length..], out var taille))
                    valeurs["EVENT_TRACE_LOGFILEW.size"] = taille;
                else if (l.StartsWith("SDK_OFFSET "))
                {
                    var corps = l["SDK_OFFSET ".Length..].Split('=');
                    if (corps.Length == 2 && int.TryParse(corps[1], out var v)) valeurs[corps[0]] = v;
                }
            }
            return valeurs.Count > 0 ? valeurs : null;
        }
        catch { return null; }
    }

    private static string? Chercher(string relatif)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var candidat = Path.Combine(d.FullName, relatif.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidat)) return candidat;
            d = d.Parent;
        }
        return null;
    }
}
