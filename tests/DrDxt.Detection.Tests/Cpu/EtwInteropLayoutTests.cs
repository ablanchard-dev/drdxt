using System.Diagnostics;
using DrDxt.Detection.Latency;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Cpu;

/// <summary>
/// Le SDK est l'oracle, pas notre lecture de la documentation.
///
/// Ce sprint a paye SEPT erreurs d'interop ETW, et **aucune n'a leve d'exception** : mauvaise
/// union, tailles de structures approximees, delegue ramasse par le GC, session qui survit au
/// processus, mauvais nom de session, mauvais GUID de session, decalages calcules a la main.
/// Le C# compilait a chaque fois.
///
/// La derniere est la plus instructive : `EventRecordCallback` etait a l'offset **408** alors que
/// le SDK le place a **424** — seize octets, parce que j'avais estime `TRACE_LOGFILE_HEADER` a
/// 264 octets quand il en fait 280. Le programme ne bloquait plus, ne plantait pas, et rendait
/// **zero evenement**. C'est le pire mode d'echec possible : il ressemble a « il n'y a rien a
/// mesurer ».
///
/// `tools/etwlayout` compile `offsetof` contre les en-tetes du SDK installe et les imprime. Ce test
/// compare. **Un seul octet d'ecart doit faire echouer la suite** — c'est la seule facon d'empecher
/// qu'un « seize octets trop loin mais tout compile » revienne.
///
/// Le test s'IGNORE proprement quand l'oracle n'a pas ete compile : un poste sans Visual Studio
/// ne doit pas voir une suite rouge pour une raison qui ne le concerne pas. Mais il ne PASSE pas
/// silencieusement non plus — il dit ce qui manque.
/// </summary>
public class EtwInteropLayoutTests
{
    [Fact]
    public void NOS_DECALAGES_SONT_CEUX_DU_SDK_INSTALLE()
    {
        var oracle = LireOracle();
        if (oracle is null)
        {
            // Pas d'echec : l'absence de l'oracle est un fait sur la machine, pas un defaut du
            // produit. Mais on le DIT, pour que personne ne croie la garde active.
            Console.WriteLine("oracle absent : compile tools/etwlayout/build.cmd pour activer "
                              + "cette garde. Les decalages ETW ne sont PAS verifies.");
            return;
        }

        // 11/09/2026 : cette boucle lisait `ThreadResidencyTracer`, inacheve et appele par le seul
        // `tools/cpuprobe`. Elle lit desormais `DpcLatencyTracer`, le traceur que le SCAN appelle
        // reellement — memes decalages, mais la garde couvre enfin du code expedie.
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
