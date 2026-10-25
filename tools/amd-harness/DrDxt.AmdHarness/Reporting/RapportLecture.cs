using System.Diagnostics;
using System.Text;
using DrDxt.Core;
using DrDxt.Detection.Gpu;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.AmdHarness.Reporting;

/// <summary>
/// Tout ce que DrDXT lit sur la
/// machine, étape par étape, en LECTURE SEULE. Règles : aucune écriture (la règle de la carte interdit tension, fréquence et
/// ventilateur sur la carte ; ce rapport n'écrit même pas la limite de puissance) ; chaque étape est isolée et bornée dans
/// le temps, une étape qui plante ou qui pend est notée et la suivante part ; le rapport s'écrit toujours, même si tout
/// échoue, dans Téléchargements, puis la fenêtre attend une touche pour que le joueur sache quoi envoyer.
/// </summary>
public static class RapportLecture
{
    public static readonly TimeSpan DelaiParEtape = TimeSpan.FromSeconds(90);

    /// <summary>Une étape : son nom et ce qu'elle rend en texte. Pure côté orchestration : testable sans machine.</summary>
    public sealed record Etape(string Nom, Func<CancellationToken, Task<string>> Lire);

    /// <summary>Déroule toutes les étapes ; ne lève jamais. Chaque résultat, réussi ou non, va dans le texte.</summary>
    /// <param name="ecrireBloc">Chaque bloc est écrit dès que l'étape finit ; un plantage plus tard ne
    /// perd pas ce qui est déjà lu.</param>
    public static async Task<string> DeroulerAsync(IReadOnlyList<Etape> etapes, TimeSpan delai, Action<string>? progres = null,
        Action<string>? ecrireBloc = null)
    {
        var sb = new StringBuilder();
        var ok = 0;
        foreach (var e in etapes)
        {
            progres?.Invoke(e.Nom);
            var chrono = Stopwatch.StartNew();
            string statut, texte;
            using var cts = new CancellationTokenSource(delai);
            // Le délai annule aussi l'étape ; une étape en processus enfant est alors tuée, pas abandonnée.
            try
            {
                var tache = Task.Run(() => e.Lire(cts.Token));
                var fin = await Task.WhenAny(tache, Task.Delay(delai)).ConfigureAwait(false);
                if (fin != tache) { statut = "DÉLAI DÉPASSÉ"; texte = $"pas de réponse en {delai.TotalSeconds:0} s : étape abandonnée, la suite continue."; }
                else { texte = await tache.ConfigureAwait(false); statut = "OK"; ok++; }
            }
            catch (Exception ex) { statut = "ERREUR"; texte = $"{ex.GetType().Name} : {ex.Message}"; }
            var bloc = $"=== {e.Nom} — {statut} ({chrono.Elapsed.TotalSeconds:0.0} s){Environment.NewLine}{texte.TrimEnd()}{Environment.NewLine}{Environment.NewLine}";
            sb.Append(bloc);
            try { ecrireBloc?.Invoke(bloc); }
            catch (Exception) { /* le texte reste dans le rapport final */ }
        }
        return $"Étapes réussies : {ok} sur {etapes.Count}{Environment.NewLine}{Environment.NewLine}{sb}";
    }

    /// <summary>Les étapes réelles du banc AMD, toutes en lecture.</summary>
    public static IReadOnlyList<Etape> EtapesReelles(IHardwareDetector detecteur, ISettingsStateReader etat)
    {
        HardwareInventory? materiel = null;
        return
        [
            new("Machine (Windows, processeur, carte mère)", _ => Task.FromResult(Machine())),
            new("Cartes graphiques (DXGI)", _ =>
            {
                var r = new VorticeDxgiProvider().EnumerateAdapters();
                return Task.FromResult(r.IsOk
                    ? string.Join(Environment.NewLine, r.Value!.Select(a => $"{a.Description} | fabricant 0x{a.VendorId:x4} | mémoire dédiée {a.DedicatedVramBytes / 1048576} Mo | {a.Luid}"))
                    : $"non lu : {r.Error?.Message}");
            }),
            new("Détection DrDXT", async _ =>
            {
                var r = await detecteur.DetectAsync().ConfigureAwait(false);
                if (!r.IsOk) return $"non lu : {r.Error?.Message}";
                materiel = r.Value!;
                return $"Processeur : {materiel.Cpu}{Environment.NewLine}Châssis : {materiel.Chassis}{Environment.NewLine}"
                       + $"Carte mère : {materiel.Board}{Environment.NewLine}BIOS : {materiel.Bios}{Environment.NewLine}"
                       + $"Mémoire : {string.Join(" ; ", materiel.Memory.Select(m => m.ToString()))}";
            }),
            new("État des réglages (XMP, ReBAR, HAGS, mode Jeu…)", async _ =>
            {
                // Chaque étape tourne seule dans son processus : elle détecte elle-même la machine.
                if (materiel is null && await detecteur.DetectAsync().ConfigureAwait(false) is { IsOk: true } lu) materiel = lu.Value!;
                if (materiel is null) return "sauté : la détection n'a rien rendu.";
                var r = await etat.ReadAsync(materiel).ConfigureAwait(false);
                // Sur un APU + carte, dire quelle carte porte ReBAR (la première de la liste).
                var cartes = string.Join(Environment.NewLine, materiel.Gpus.Select((g, i) => $"carte {i} : {g}"));
                return (r.IsOk ? r.Value!.ToString() : $"non lu : {r.Error?.Message}")
                       + $"{Environment.NewLine}ReBAR lu sur la carte 0 de cette liste :{Environment.NewLine}{cartes}";
            }),
            new("AMD : réglages lus par ADLX", _ =>
            {
                static string E(bool? v) => v switch { true => "activé", false => "désactivé", null => "non lu" };
                var l = AdlxReglages.Lire();
                return Task.FromResult(
                    $"amdadlx64.dll : {(AdlxReglages.DllPresente() ? "présente" : "absente")}{Environment.NewLine}"
                    + (l.Raison is null ? "carte AMD dédiée trouvée" : $"rien lu : {l.Raison}") + Environment.NewLine
                    + $"SAM : {E(l.Sam)} | Anti-Lag : {E(l.AntiLag)} | FRTC : {E(l.FrtcActif)} {l.FrtcFps} | Chill : {E(l.ChillActif)} {l.ChillMinFps}-{l.ChillMaxFps}"
                    + $" | limite d'images : {(l.LimiteImages is { } li ? li.ToString() : "non lue")} | température : {(l.TemperatureC is { } t ? $"{t:0.0} °C" : "non lue")}");
            }),
            new("AMD : chemin ADLX carte par carte (diagnostic, lecture)", _ =>
            {
                if (!AdlxReglages.DllPresente() || AdlxReglages.Partage is not { } natif) return Task.FromResult("sauté : pas de pilote AMD.");
                var sb = new StringBuilder();
                for (var i = 0; i < 4; i++)
                {
                    var etapes = natif.ProbeGfxPath(i);
                    sb.AppendLine($"carte {i} : " + string.Join(" ; ", etapes.Select(s => $"[{(s.Ok ? "OK" : "!!")}] {s.Name}{(s.Detail is null ? "" : $" — {s.Detail}")}")));
                    if (etapes.Any(s => s.Name == "gpu-count" && !s.Ok)) break;
                }
                return Task.FromResult(sb.ToString());
            }),
            // L'unité de la limite d'ADLX (% du TDP ou décalage) se prouve en mettant cette plage à côté d'AMD Software.
            new("AMD : limite de puissance et sa plage (lecture)", _ =>
                Task.FromResult(string.Join(Environment.NewLine, LectureAdlx.LignesPuissance(LectureAdlx.LirePuissances())))),
            new("Température de la carte (NVML, ADLX ou IGCL)", _ => Task.FromResult(TemperatureCarte.Lire().ToString())),
            new("Carte qui porte l'affichage (dwm)", _ =>
            {
                var dwm = Process.GetProcessesByName("dwm").FirstOrDefault();
                return Task.FromResult(dwm is null ? "dwm introuvable" : CarteDuJeu.Lire(dwm.Id)?.ToString() ?? "non lu");
            }),
            new("Docteur : tous les constats", _ =>
            {
                var constats = new DrDxt.Detection.Repair.RepairAuditService().Lire();
                return Task.FromResult(string.Join(Environment.NewLine,
                    constats.Select(c => $"[{c.Domaine}] {c.Nom} | {c.Statut} | {c.ValeurCourante} || {c.Explication}")));
            }),
        ];
    }

    private static string Machine()
    {
        string? R(string cle, string nom) { try { return Microsoft.Win32.Registry.GetValue(cle, nom, null)?.ToString(); } catch (Exception) { return null; } }
        const string Cpu = @"HKEY_LOCAL_MACHINE\Hardware\Description\System\CentralProcessor\0";
        const string Bios = @"HKEY_LOCAL_MACHINE\Hardware\Description\System\Bios";
        return $"Windows : {Environment.OSVersion.VersionString} ({R(@"HKEY_LOCAL_MACHINE\Software\Microsoft\Windows NT\CurrentVersion", "DisplayVersion")})"
               + $"{Environment.NewLine}Processeur : {R(Cpu, "ProcessorNameString")?.Trim()}"
               + $"{Environment.NewLine}Carte mère : {R(Bios, "BaseBoardManufacturer")} {R(Bios, "BaseBoardProduct")} | BIOS {R(Bios, "BIOSVersion")} du {R(Bios, "BIOSReleaseDate")}"
               + $"{Environment.NewLine}Administrateur : {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}";
    }

    /// <summary>Chaque étape tourne dans un processus enfant (le même exe, « --etape N »). Un
    /// plantage du pilote AMD tue l'enfant, pas le rapport ; une étape bloquée est tuée au délai, pas abandonnée.</summary>
    public static IReadOnlyList<Etape> EtapesEnProcessus(IReadOnlyList<string> noms)
        => noms.Select((nom, i) => new Etape(nom, ct => EnfantAsync(i, ct))).ToList();

    private static async Task<string> EnfantAsync(int indice, CancellationToken ct)
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("chemin de l'exe inconnu");
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        // Lancé par « dotnet banc.dll » (développement) : l'enfant reprend la même dll.
        if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            psi.ArgumentList.Add(typeof(RapportLecture).Assembly.Location);
        psi.ArgumentList.Add("--etape");
        psi.ArgumentList.Add(indice.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("le processus de l'étape n'a pas démarré");
        var sortie = p.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var erreurs = p.StandardError.ReadToEndAsync(CancellationToken.None);
        try { await p.WaitForExitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception) { /* déjà parti */ }
            throw;
        }
        var texte = await sortie.ConfigureAwait(false);
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"le processus de l'étape s'est arrêté avec le code 0x{p.ExitCode:X8}"
                + (p.ExitCode == unchecked((int)0xC0000005) ? " (violation d'accès dans du code natif, probablement le pilote)" : "")
                + $". Sortie : {texte.Trim()} {(await erreurs.ConfigureAwait(false)).Trim()}");
        return texte;
    }

    /// <summary>Le dossier Téléchargements du joueur, sinon le Bureau, sinon le dossier de l'exe.</summary>
    public static string Dossier()
    {
        foreach (var d in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                     Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                     AppContext.BaseDirectory,
                 })
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return d;
        return AppContext.BaseDirectory;
    }
}
