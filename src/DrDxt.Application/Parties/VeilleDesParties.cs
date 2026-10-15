using DrDxt.Core;

namespace DrDxt.Application.Parties;

/// <summary>
/// La veille des parties. Tant qu'un jeu tourne, des fenêtres de mesure
/// bout à bout ; quand il se ferme (ou change), la partie est résumée et enregistrée. Hors partie, rien n'est mesuré.
/// Toutes les coutures sont injectées : la veille se teste sans jeu, sans horloge et sans disque.
/// </summary>
public sealed class VeilleDesParties
{
    /// <summary>Une fenêtre d'une minute, et les fenêtres se suivent sans trou : toute la partie est mesurée,
    /// image par image (avant : 20 s toutes les trois minutes, soit un dixième de la partie, et un pic de chaleur entre deux
    /// captures passait inaperçu). La minute est le grain auquel un menu ou un lobby se met à part.</summary>
    public static readonly TimeSpan DureeCapture = TimeSpan.FromMinutes(1);

    /// <summary>Plus d'attente entre deux fenêtres : la suivante commence dès que la précédente est relevée.</summary>
    public static readonly TimeSpan EntreDeuxCaptures = TimeSpan.Zero;

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
    private readonly Func<double?>? _temperatureProcesseur;
    private readonly Func<bool?>? _entrees;
    private readonly Func<EtatCpu?>? _etatProcesseur;

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
        // L'heure de chaque capture. Lue sur l'horloge réelle, une partie rejouée en test tombait à
        // la date du test, du mauvais côté des changements qu'elle devait juger.
        Func<DateTimeOffset>? horloge = null,
        // La température de la carte, lue par la bibliothèque de son pilote (NVML, ADLX, IGCL).
        Func<double?>? carte = null,
        // Les overlays ouverts, lus par le nom des processus (OverlaysEnPartie).
        Func<IReadOnlyList<string>?>? overlays = null,
        // Dix pings vers la box et dix vers internet, juste après la capture.
        Func<CancellationToken, Task<(double? Box, double? Internet, double? Pertes)>>? reseau = null,
        // La carte qui porte le jeu, et la vraie carte laissée de côté s'il tourne sur une puce intégrée.
        Func<int, (string Carte, string? DedieeIgnoree)?>? carteDuJeu = null,
        // La partie suivie, à chaque regard ; null à sa clôture (marque « en cours »).
        Action<PartieInterrompue?>? suivre = null,
        // La carte lue par son pilote (fréquence, raisons de bridage), une fois par seconde pendant la capture.
        Func<HardwareReading?>? lireCarte = null,
        // La température du processeur chaque seconde pendant toute la fenêtre (son vrai pic, pas une lecture
        // après coup), et « le joueur a-t-il touché quelque chose cette seconde ? » (null = pas lisible).
        Func<double?>? temperatureProcesseur = null,
        Func<bool?>? entrees = null,
        // La seconde du processeur entière (cœur le plus chaud, cœur 0, raisons de freiner).
        Func<EtatCpu?>? etatProcesseur = null)
    {
        _etatProcesseur = etatProcesseur;
        _temperatureProcesseur = temperatureProcesseur;
        _entrees = entrees;
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
        // Une sortie propre (annulation, relevé éteint) lève la marque ; seule une mort du
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

        // Une partie sans aucune seconde du processeur lue se dit une fois, avec sa raison probable ;
        // avant, elle s'enregistrait sans température ni freinage, sans un mot.
        var processeurSignale = false;

        void Clore()
        {
            processeurSignale = false;
            if (enCours is not null && ReleveDesParties.Resumer(enCours, captures) is { } partie) _enregistrer(partie);
            if (enCours is not null) Suivre(null);
            enCours = null;
            captures.Clear();
        }

        while (!ct.IsCancellationRequested)
        {
            var jeu = _jeu();
            // Le même nom d'exécutable
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
            var celsius = new List<double>();
            var entrees = new List<bool>();
            var etats = new List<EtatCpu>();
            using var finEchantillons = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var echantillonneur = Echantillonner(echantillons, celsius, entrees, etats, finEchantillons.Token);
            try { mesure = await _capturer(jeu.Value.Pid, DureeCapture, ct); }
            catch (OperationCanceledException) { return; }
            // Une capture ratée ne compte pas ; la veille continue, mais elle se dit.
            catch (Exception ex) { Signaler($"{jeu.Value.Exe} : {ex.Message}"); }
            finally
            {
                finEchantillons.Cancel();
                await echantillonneur;
            }
            if (mesure is not { AvgFps: > 0 } && mesure is not null) Signaler(Docteur.T("VP.AucuneImage", jeu.Value.Exe));
            if (_etatProcesseur is not null && etats.Count == 0 && !processeurSignale)
            {
                processeurSignale = true;
                Signaler(Docteur.T("VP.ProcesseurNonLu", jeu.Value.Exe));
            }
            if (mesure is { AvgFps: > 0 })
            {
                // Lu juste après la capture, le jeu tourne encore. Un relevé raté n'annule pas la capture.
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
                // Le pic lu seconde par seconde pendant la fenêtre ; sans échantillon, la lecture d'après la capture.
                var celsiusMax = celsius.Count > 0 ? celsius.Max() : cpu.Celsius;
                captures.Add(new CaptureEnPartie(_horloge(), mesure.AvgFps, mesure.P1LowFps, _classer(mesure), cpu.Mhz, cpu.Watts, celsiusMax, vram, voisins, carte, overlays,
                    reseau.Box, reseau.Internet, reseau.Pertes, carteDuJeu?.Carte, carteDuJeu?.DedieeIgnoree,
                    MecanismeCarte.Depuis(echantillons), LatenceAffichageMs: mesure.DisplayLatencyMsMedian,
                    ImagesMs: mesure.FrameTimesMs, PremierPlan: mesure.ForegroundFraction,
                    Entrees: entrees.Count > 0 ? entrees.Count(e => e) / (double)entrees.Count : null,
                    Freinage: FreinageCpu.Depuis(etats), Ventilateurs: VentilateursEnPartie.Depuis(etats.Select(e => e.Ventilateurs))));
            }
            await Attendre(EntreDeuxCaptures, ct);
        }
    }

    /// <summary>J-MECA : une lecture de la carte par seconde tant que la capture dure. Une vraie minuterie, pas la couture
    /// d'attente de la veille (dans les tests elle avance l'horloge des parties). Une lecture ratée n'arrête rien.
    /// Au même tic, la température du processeur et l'activité du joueur.</summary>
    private async Task Echantillonner(List<HardwareReading> echantillons, List<double> celsius, List<bool> entrees,
        List<EtatCpu> etats, CancellationToken fin)
    {
        if (_lireCarte is null && _temperatureProcesseur is null && _entrees is null && _etatProcesseur is null) return;
        while (!fin.IsCancellationRequested)
        {
            try { if (_lireCarte?.Invoke() is { } r) echantillons.Add(r); }
            catch (Exception) { }
            try { if (_temperatureProcesseur?.Invoke() is { } t) celsius.Add(t); }
            catch (Exception) { }
            try { if (_entrees?.Invoke() is { } e) entrees.Add(e); }
            catch (Exception) { }
            try { if (_etatProcesseur?.Invoke() is { } s) etats.Add(s); }
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
