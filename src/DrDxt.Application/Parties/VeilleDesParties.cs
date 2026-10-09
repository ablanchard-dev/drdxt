using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// 29/09/2026 (boucle « vivant et prouvé », A3) : la veille des parties. Tant qu'un jeu tourne, une capture courte à
/// intervalle ; quand il se ferme (ou change), la partie est résumée et enregistrée. Hors partie, rien n'est mesuré.
/// Toutes les coutures sont injectées : la veille se teste sans jeu, sans horloge et sans disque.
/// </summary>
public sealed class VeilleDesParties
{
    /// <summary>Une capture tient 20 s : la durée où l'A/B du produit résout le mieux par point (tailbench, 07/09).</summary>
    public static readonly TimeSpan DureeCapture = TimeSpan.FromSeconds(20);

    /// <summary>Trois minutes entre deux captures : une partie de vingt minutes en donne six, sans peser sur le jeu.
    /// Choix de cadence, pas une mesure.</summary>
    public static readonly TimeSpan EntreDeuxCaptures = TimeSpan.FromMinutes(3);

    /// <summary>Hors partie, on regarde toutes les trente secondes si un jeu a démarré.</summary>
    public static readonly TimeSpan HorsPartie = TimeSpan.FromSeconds(30);

    private readonly Func<(string Exe, int Pid)?> _jeu;
    private readonly Func<int, TimeSpan, CancellationToken, Task<BenchmarkResult?>> _capturer;
    private readonly Func<BenchmarkResult, BottleneckState> _classer;
    private readonly Action<PartieRelevee> _enregistrer;
    private readonly Func<TimeSpan, CancellationToken, Task> _patienter;
    private readonly Func<(double? Mhz, double? Watts, double? Celsius)>? _processeur;
    private readonly Func<double?>? _memoireVideo;
    private readonly Func<int, IReadOnlyList<(string Nom, double Pourcent)>?>? _voisins;
    private readonly Action<string>? _signalerEchec;
    private readonly Func<DateTimeOffset> _horloge;
    private readonly Func<double?>? _carte;
    private readonly Func<IReadOnlyList<string>?>? _overlays;
    private readonly Func<CancellationToken, Task<(double? Box, double? Internet, double? Pertes)>>? _reseau;
    private readonly Func<int, (string Carte, string? DedieeIgnoree)?>? _carteDuJeu;
    private readonly Action<PartieInterrompue?>? _suivre;
    private readonly Func<HardwareReading?>? _lireCarte;

    public VeilleDesParties(
        Func<(string Exe, int Pid)?> jeu,
        Func<int, TimeSpan, CancellationToken, Task<BenchmarkResult?>> capturer,
        Func<BenchmarkResult, BottleneckState> classer,
        Action<PartieRelevee> enregistrer,
        Func<TimeSpan, CancellationToken, Task> patienter,
        Func<(double? Mhz, double? Watts, double? Celsius)>? processeur = null,
        Func<double?>? memoireVideo = null,
        Func<int, IReadOnlyList<(string Nom, double Pourcent)>?>? voisins = null,
        Action<string>? signalerEchec = null,
        // 07/10/2026 (J5) : l'heure de chaque capture. Lue sur l'horloge réelle, une partie rejouée en test tombait à
        // la date du test, du mauvais côté des changements qu'elle devait juger.
        Func<DateTimeOffset>? horloge = null,
        // 07/10/2026 (A5) : la température de la carte, lue par la bibliothèque de son pilote (NVML, ADLX, IGCL).
        Func<double?>? carte = null,
        // 07/10/2026 (D10) : les overlays ouverts, lus par le nom des processus (OverlaysEnPartie).
        Func<IReadOnlyList<string>?>? overlays = null,
        // 07/10/2026 (D6) : dix pings vers la box et dix vers internet, juste après la capture.
        Func<CancellationToken, Task<(double? Box, double? Internet, double? Pertes)>>? reseau = null,
        // 07/10/2026 (A4) : la carte qui porte le jeu, et la vraie carte laissée de côté s'il tourne sur une puce intégrée.
        Func<int, (string Carte, string? DedieeIgnoree)?>? carteDuJeu = null,
        // 08/10/2026 (D1) : la partie suivie, à chaque regard ; null à sa clôture (marque « en cours »).
        Action<PartieInterrompue?>? suivre = null,
        // 08/10/2026 (J-MECA) : la carte lue par son pilote (fréquence, raisons de bridage), une fois par seconde pendant la capture.
        Func<HardwareReading?>? lireCarte = null)
    {
        _lireCarte = lireCarte;
        _suivre = suivre;
        _carteDuJeu = carteDuJeu;
        _reseau = reseau;
        _overlays = overlays;
        _carte = carte;
        _horloge = horloge ?? (() => DateTimeOffset.Now);
        _signalerEchec = signalerEchec;
        _voisins = voisins;
        _memoireVideo = memoireVideo;
        _processeur = processeur;
        _jeu = jeu;
        _capturer = capturer;
        _classer = classer;
        _enregistrer = enregistrer;
        _patienter = patienter;
    }

    /// <summary>Veille jusqu'à l'annulation. Une partie en cours à l'annulation n'est pas enregistrée : elle n'est pas finie.</summary>
    public async Task VeillerAsync(CancellationToken ct)
    {
        // Revue du 08/10/2026 (D1) : une sortie propre (annulation, relevé éteint) lève la marque ; seule une mort du
        // processus (arrêt du PC) la laisse ouverte.
        try { await VeillerJusquaLaFinAsync(ct); }
        finally { if (_marqueOuverte) Suivre(null); }
    }

    private bool _marqueOuverte;

    private async Task VeillerJusquaLaFinAsync(CancellationToken ct)
    {
        string? enCours = null;
        var pidEnCours = 0;
        var debutEnCours = DateTimeOffset.MinValue;
        var captures = new List<CaptureEnPartie>();

        void Clore()
        {
            if (enCours is not null && ReleveDesParties.Resumer(enCours, captures) is { } partie) _enregistrer(partie);
            if (enCours is not null) Suivre(null);
            enCours = null;
            captures.Clear();
        }

        while (!ct.IsCancellationRequested)
        {
            var jeu = _jeu();
            // 07/10/2026 (vu en vrai : Warzone fermé puis relancé entre deux regards, 17:37 puis 18:53) : le même nom d'exécutable
            // ne suffit pas, un nouveau processus est une nouvelle partie ; sinon les deux sessions n'en faisaient qu'une.
            if (jeu is null || jeu.Value.Exe != enCours || jeu.Value.Pid != pidEnCours) Clore();
            if (jeu is null)
            {
                await Attendre(HorsPartie, ct);
                continue;
            }

            if (enCours is null) debutEnCours = _horloge();
            enCours = jeu.Value.Exe;
            pidEnCours = jeu.Value.Pid;
            Suivre(new PartieInterrompue(enCours, debutEnCours, _horloge()));
            BenchmarkResult? mesure = null;
            var echantillons = new List<HardwareReading>();
            using var finEchantillons = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var echantillonneur = Echantillonner(echantillons, finEchantillons.Token);
            try { mesure = await _capturer(jeu.Value.Pid, DureeCapture, ct); }
            catch (OperationCanceledException) { return; }
            // Une capture ratée ne compte pas ; la veille continue. 07/10/2026 (revue F-10) : mais elle se dit.
            catch (Exception ex) { Signaler($"{jeu.Value.Exe} : {ex.Message}"); }
            finally
            {
                finEchantillons.Cancel();
                await echantillonneur;
            }
            if (mesure is not { AvgFps: > 0 } && mesure is not null) Signaler($"{jeu.Value.Exe} : aucune image lue");
            if (mesure is { AvgFps: > 0 })
            {
                // 04/10/2026 (plan 1.1) : lu juste après la capture, le jeu tourne encore. Un relevé raté n'annule pas la capture.
                (double? Mhz, double? Watts, double? Celsius) cpu = (null, null, null);
                try { if (_processeur is not null) cpu = _processeur(); }
                catch (Exception) { }
                double? vram = null;
                try { vram = _memoireVideo?.Invoke(); }
                catch (Exception) { }
                IReadOnlyList<(string, double)>? voisins = null;
                try { voisins = _voisins?.Invoke(jeu.Value.Pid); }
                catch (Exception) { }
                double? carte = null;
                try { carte = _carte?.Invoke(); }
                catch (Exception) { }
                IReadOnlyList<string>? overlays = null;
                try { overlays = _overlays?.Invoke(); }
                catch (Exception) { }
                (double? Box, double? Internet, double? Pertes) reseau = (null, null, null);
                try { if (_reseau is not null) reseau = await _reseau(ct); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { }
                (string Carte, string? DedieeIgnoree)? carteDuJeu = null;
                try { carteDuJeu = _carteDuJeu?.Invoke(jeu.Value.Pid); }
                catch (Exception) { }
                captures.Add(new CaptureEnPartie(_horloge(), mesure.AvgFps, mesure.P1LowFps, _classer(mesure), cpu.Mhz, cpu.Watts, cpu.Celsius, vram, voisins, carte, overlays,
                    reseau.Box, reseau.Internet, reseau.Pertes, carteDuJeu?.Carte, carteDuJeu?.DedieeIgnoree,
                    MecanismeCarte.Depuis(echantillons)));
            }
            await Attendre(EntreDeuxCaptures, ct);
        }
    }

    /// <summary>J-MECA : une lecture de la carte par seconde tant que la capture dure. Une vraie minuterie, pas la couture
    /// d'attente de la veille (dans les tests elle avance l'horloge des parties). Une lecture ratée n'arrête rien.</summary>
    private async Task Echantillonner(List<HardwareReading> echantillons, CancellationToken fin)
    {
        if (_lireCarte is null) return;
        while (!fin.IsCancellationRequested)
        {
            try { if (_lireCarte() is { } r) echantillons.Add(r); }
            catch (Exception) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), fin).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void Suivre(PartieInterrompue? partie)
    {
        _marqueOuverte = partie is not null;
        try { _suivre?.Invoke(partie); }
        catch (Exception) { /* la marque ne doit pas arrêter la veille */ }
    }

    private void Signaler(string raison)
    {
        try { _signalerEchec?.Invoke(raison); }
        catch (Exception) { /* le signalement ne doit pas arrêter la veille */ }
    }

    private async Task Attendre(TimeSpan duree, CancellationToken ct)
    {
        try { await _patienter(duree, ct); }
        catch (OperationCanceledException) { }
    }
}
