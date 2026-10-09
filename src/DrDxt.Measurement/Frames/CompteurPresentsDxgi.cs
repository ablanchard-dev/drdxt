using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using DrDxt.Core;

namespace DrDxt.Measurement.Frames;

/// <summary>
/// 24/09/2026 — compter les images soi-meme, quand PresentMon n'y arrive plus.
///
/// Mesure du 24/09 sur la machine de reference (Windows 26200.9457) : PresentMon 2.4.1, 2.5.1 ET
/// 2.6.0 ne voient aucune image, sur aucun processus — alors qu'une trace ETW brute de 7 s porte
/// 1 307 evenements DXGI `Present_Start` (id 42) pour la scene de mesure, soit 193,9 img/s, ce que
/// la scene compte elle-meme. Windows emet ; c'est PresentMon qui ne reconstruit plus rien.
///
/// Ce compteur lit l'evenement 42 du fournisseur `Microsoft-Windows-DXGI` pour un processus : les
/// intervalles entre deux Present sont les frametimes, et toutes les grandeurs sortent de
/// <see cref="FrametimeStatistics"/> — les memes formules que le banc PresentMon.
///
/// Ce qu'il ne sait pas : les jeux Vulkan / OpenGL (pas de DXGI), la latence d'affichage, le
/// focus. Et il demande les droits administrateur (session ETW) : sans eux, il echoue et le dit.
/// </summary>
public sealed class CompteurPresentsDxgi
{
    public const string NomSession = "DrDxtPresents";
    private const int PresentStart = 42;
    public const int MinImages = 10;

    /// <summary>26/09/2026 — borne du fichier de trace. Sans elle, une session laissee ouverte par un
    /// plantage ecrit tous les evenements DXGI du PC dans %TEMP% jusqu'au redemarrage. En mode
    /// sequentiel, la session cesse d'ecrire a la borne : une trace qui l'atteint est tronquee.</summary>
    public const int MaxMo = 1024;

    public static string ArgumentsCreation(string etl)
        => $"create trace {NomSession} -ets -o \"{etl}\" -max {MaxMo} -p Microsoft-Windows-DXGI 0xFFFFFFFFFFFFFFFF 5";

    /// <summary>26/09/2026 — les .etl laisses par un plantage en pleine capture. Une seule session
    /// a la fois (la mesure arrete d'abord la precedente) : au demarrage, tout fichier du compteur
    /// est un reste. Rend le nombre de fichiers retires.</summary>
    public static int RangerRestes(string dossier)
    {
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dossier, "drdxt-presents-*.etl"))
            try { File.Delete(f); n++; } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return n;
    }

    /// <summary>Marge d'un tampon : la session s'arrete un peu avant la borne exacte.</summary>
    public static bool TracePleine(long octets) => octets >= (MaxMo - 1) * 1024L * 1024;

    private static string Logman => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "logman.exe");

    /// <summary>Les frametimes (ms) d'une suite d'horodatages de Present, en ticks de 100 ns.</summary>
    public static IReadOnlyList<double> Intervalles(IEnumerable<long> ticks)
    {
        var t = ticks.Order().ToArray();
        var ms = new List<double>(Math.Max(0, t.Length - 1));
        for (var i = 1; i < t.Length; i++)
            if (t[i] > t[i - 1]) ms.Add((t[i] - t[i - 1]) / 10_000.0);   // deux Present au meme tick : pas une image
        return ms;
    }

    public Result<BenchmarkResult> Mesurer(int pid, TimeSpan duree, CancellationToken ct)
    {
        var etl = Path.Combine(Path.GetTempPath(), $"drdxt-presents-{Environment.ProcessId}-{Guid.NewGuid():N}.etl");
        try
        {
            Lancer($"stop {NomSession} -ets");   // la notre, restee ouverte apres une interruption
            RangerRestes(Path.GetTempPath());     // et son fichier, libere par l'arret
            if (Lancer(ArgumentsCreation(etl)) != 0)
                return Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail,
                    "La session ETW DXGI n'a pas pu demarrer (droits administrateur requis).");
            try { Task.Delay(duree, ct).GetAwaiter().GetResult(); }
            finally { Lancer($"stop {NomSession} -ets"); }
            if (TracePleine(new FileInfo(etl).Length))
                return Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail,
                    $"La trace DXGI a atteint sa borne ({MaxMo} Mo) : la fenêtre est tronquée, pas de mesure.");

            var ticks = new List<long>();
            using (var lecteur = new EventLogReader(new EventLogQuery(etl, PathType.FilePath,
                       $"*[System[EventID={PresentStart}]]")))
            {
                for (var e = lecteur.ReadEvent(); e is not null; e = lecteur.ReadEvent())
                    using (e)
                        if (e.ProcessId == pid && e.ProviderName == "Microsoft-Windows-DXGI" && e.TimeCreated is { } t)
                            ticks.Add(t.ToUniversalTime().Ticks);
            }

            var ft = Intervalles(ticks);
            return ft.Count < MinImages
                ? Result<BenchmarkResult>.Fail(ErrorCodes.NoPresentingFrames,
                    $"{ft.Count} image(s) DXGI pour le processus {pid} : pas de quoi mesurer.")
                : Result<BenchmarkResult>.Ok(FrametimeStatistics.PasseDepuisSerie(ft, runs: 1, marginOfError: double.PositiveInfinity));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Result<BenchmarkResult>.Fail(ErrorCodes.CaptureFail, $"Compteur DXGI : {ex.Message}");
        }
        finally
        {
            try { File.Delete(etl); } catch { /* fichier temporaire deja parti */ }
        }
    }

    private static int Lancer(string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(Logman, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        p.WaitForExit(15_000);
        return p.HasExited ? p.ExitCode : -1;
    }
}
