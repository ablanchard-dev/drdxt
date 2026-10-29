using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>L'historique : changements, parties, incidents et mises à jour de la machine, et les lignes qui les disent.</summary>
public partial class ScanViewModel
{
    // ── L'historique de la machine, une ligne du temps ─────────────────────────────
    public ObservableCollection<string> LignesHistorique { get; } = [];
    /// <summary>Les événements datés de la frise, chacun avec son genre (sa couleur).</summary>
    public ObservableCollection<EvenementDeFrise> FriseHistorique { get; } = [];
    public bool AHistorique => LignesHistorique.Count > 0 || FriseHistorique.Count > 0;
    public string LabelHistoriqueTitre => _localize("Historique.Titre");
    public string LabelHistoriqueDescription => _localize("Historique.Description");

    /// <summary>Les arrêts brutaux et les erreurs matérielles des 30 derniers jours. L'appli pose la vraie lecture au
    /// démarrage ; <c>null</c> = la frise sans eux (les tests qui ne la vérifient pas).</summary>
    internal Func<(IReadOnlyList<DateTimeOffset> Arrets, IReadOnlyList<DateTimeOffset> Erreurs)>? LireIncidents { get; set; }

    internal static string LigneBilan(Bilan b, Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var ligne = Pluriels.Accorder(string.Format(fr, localiser("Historique.Bilan"), b.Parties, b.Changements, b.Arrets, b.Erreurs));
        if (b.FpsMedian is { } f && b.FpsMedianSemainePrecedente is { } avant)
            ligne += " " + string.Format(fr, localiser("Historique.BilanFps"), avant.ToString("0", fr), f.ToString("0", fr));
        return ligne;
    }

    /// <summary>Les journaux d'événements se lisent hors du fil de l'interface ; les lignes se posent ensuite sur le
    /// contexte de l'appelant.</summary>
    internal async Task AfficherHistoriqueAsync(IReadOnlyList<PartieRelevee> parties)
    {
        var generation = Interlocked.Increment(ref _generationHistorique);
        (IReadOnlyList<DateTimeOffset> Arrets, IReadOnlyList<DateTimeOffset> Erreurs) incidents = ([], []);
        try { if (LireIncidents is { } lire) incidents = await Task.Run(lire); }
        catch (Exception) { /* journaux illisibles : l'historique s'affiche sans incidents, il n'en invente pas */ }
        IReadOnlyList<string> depuisLeScan = [];
        try { if (LireChangementsDepuisLeScan is { } lireChangements) depuisLeScan = await Task.Run(lireChangements); }
        catch (Exception) { /* changements non lus : aucun n'est attribué à une partie */ }
        IReadOnlyList<DrDxt.Detection.Repair.FaitDate> misesAJour = [];
        try { misesAJour = await MisesAJourAsync(); }
        catch (Exception) { /* mises à jour non lues : la frise s'affiche sans elles */ }
        // Sans contexte d'interface (tests), deux fins de partie rapprochées reprennent sur deux fils et toucheraient les
        // mêmes listes ensemble ; dans l'appli, le verrou ne coûte rien. Une lecture lente finie après une plus récente ne
        // réécrit pas l'historique avec des parties périmées.
        lock (_verrouHistorique)
            if (generation == Volatile.Read(ref _generationHistorique))
                AfficherHistorique(parties, incidents.Arrets, incidents.Erreurs, depuisLeScan, misesAJour);
    }

    private readonly object _verrouHistorique = new();
    private int _generationHistorique;

    /// <summary>Les pilotes (setupapi) et les mises à jour de Windows Update posés sans
    /// DrDXT, à leur heure réelle, hors du fil de l'écran. <c>null</c> = rien (tests).</summary>
    internal Func<IReadOnlyList<DrDxt.Detection.Repair.FaitDate>>? LireMisesAJourDeLaMachine { get; set; }

    /// <summary>Les journaux d'installation et l'historique de Windows Update pèsent des centaines de Mo et changent une
    /// fois par mois : une lecture par heure au plus, pas à chaque fin de partie.</summary>
    private (DateTimeOffset Lu, Task<IReadOnlyList<DrDxt.Detection.Repair.FaitDate>> Faits)? _misesAJour;

    internal Task<IReadOnlyList<DrDxt.Detection.Repair.FaitDate>> MisesAJourAsync()
    {
        if (LireMisesAJourDeLaMachine is not { } lire) return Task.FromResult<IReadOnlyList<DrDxt.Detection.Repair.FaitDate>>([]);
        if (_misesAJour is not { } m || m.Faits.IsFaulted || Horloge() - m.Lu > TimeSpan.FromHours(1))
            _misesAJour = m = (Horloge(), Task.Run(lire));
        return m.Faits;
    }

    /// <summary>Les instants des mises à jour de la machine ; <c>null</c> tant qu'elles ne sont pas lues.</summary>
    private IReadOnlyList<DateTimeOffset>? InstantsDesMisesAJour()
    {
        if (LireMisesAJourDeLaMachine is null) return [];
        var t = MisesAJourAsync();
        return t.IsCompletedSuccessfully ? t.Result.Select(f => f.Quand).ToList() : null;
    }

    /// <summary>
    /// Ce qu'une mise à jour faite toute seule a coûté en jeu, jugée comme un changement de DrDXT (mêmes parties du même
    /// jeu avant et après, même règle de séparation nette), bornée par tout autre changement. Les mises à jour d'une même
    /// minute se jugent ensemble ; sans aucune partie d'avant, elle ne se jugera jamais et ne se dit pas.
    /// </summary>
    internal static IReadOnlyList<string> LignesMisesAJour(IReadOnlyList<DrDxt.Detection.Repair.FaitDate> faits,
        IReadOnlyList<PartieRelevee> parties, IReadOnlyList<Changement> changements, Func<string, string> localiser, int combien = 3)
    {
        var instants = new List<(DateTimeOffset Quand, List<string> Libelles)>();
        foreach (var f in faits.OrderBy(f => f.Quand))
            if (instants.Count > 0 && f.Quand - instants[^1].Quand <= MemeInstant)
            {
                if (!instants[^1].Libelles.Contains(f.Libelle)) instants[^1].Libelles.Add(f.Libelle);
            }
            else instants.Add((f.Quand, [f.Libelle]));
        var bornes = instants.Select(i => i.Quand)
            .Concat(changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true).Select(c => c.Quand))
            .Order().ToList();
        var lignes = new List<string>();
        foreach (var (quand, libelles) in Enumerable.Reverse(instants))
        {
            var suivant = bornes.Where(b => b > quand + MemeInstant).Select(b => (DateTimeOffset?)b).FirstOrDefault();
            var precedent = bornes.Where(b => b < quand - MemeInstant).Select(b => (DateTimeOffset?)b).LastOrDefault();
            var bilan = ComparaisonDesParties.Bilan(parties, quand, null, suivant, precedent);
            if (bilan.Avant == 0) continue;
            lignes.Add(LigneChangement(new Changement(quand, string.Join(", ", libelles)), bilan, localiser));
            if (lignes.Count == combien) break;
        }
        return lignes;
    }

    /// <summary>Ce qui a été installé, retiré ou mis à jour depuis le dernier scan, relu à
    /// l'ouverture : registre et liste des pilotes (<c>pnputil</c>), hors du fil de l'écran, aucune mesure de performance. <c>null</c> = rien (tests).</summary>
    internal Func<IReadOnlyList<string>>? LireChangementsDepuisLeScan { get; set; }

    internal void AfficherHistorique(IReadOnlyList<PartieRelevee> parties, IReadOnlyList<DateTimeOffset> arrets, IReadOnlyList<DateTimeOffset> erreurs,
        IReadOnlyList<string>? depuisLeScan = null, IReadOnlyList<DrDxt.Detection.Repair.FaitDate>? misesAJour = null)
    {
        LignesHistorique.Clear();
        FriseHistorique.Clear();
        try
        {
            foreach (var ligne in LignesMisesAJour(misesAJour ?? [], parties, JournalChangements?.Lire() ?? [], _localize))
                LignesHistorique.Add(ligne);
        }
        catch (Exception) { /* un journal illisible ne cache pas le reste */ }
        foreach (var changement in depuisLeScan ?? [])
            LignesHistorique.Add(string.Format(CultureInfo.CurrentCulture, _localize("Historique.DepuisLeScan"), changement));
        try
        {
            var fr = CultureInfo.CurrentCulture;
            // La frise devient une liste à points (vert partie, rouge arrêt ou erreur, violet
            // changement), le jeu à gauche et ses FPS alignés à droite.
            var frise = FriseDeLaMachine.Construire(JournalChangements?.Lire() ?? [], parties, arrets, erreurs, DateTimeOffset.Now,
                c => c.Libelle,
                p => NomDuJeu(p.Jeu),
                _localize("Historique.Arret"), _localize("Historique.Erreur"),
                p => ResumeCourt(p, _localize));
            // Le bilan de la semaine, en tête.
            if (frise.Count > 0)
                LignesHistorique.Add(LigneBilan(BilanDeLaSemaine.Construire(parties, JournalChangements?.Lire() ?? [], arrets, erreurs, DateTimeOffset.Now), _localize));
            foreach (var e in frise) FriseHistorique.Add(e);
        }
        catch (Exception) { /* une lecture ratée laisse la frise vide, jamais fausse */ }
        OnPropertyChanged(nameof(AHistorique));
    }
    public string LabelPartiesDescription => _localize("Parties.Description");
    public string LabelPartiesAucune => _localize("Parties.Aucune");
    public string LabelBasculerReleve => _localize("Parties.Libelle");   // L'état est dans l'interrupteur

    // Le nom que le joueur connaît. Les jeux suivis par leur exécutable (liste de RenderQueueCapTweak),
    // puis le projet d'un jeu Unreal livré ; un inconnu garde son exécutable plutôt qu'un nom inventé.
    private static readonly Dictionary<string, string> JeuxConnus = new(StringComparer.OrdinalIgnoreCase)
    {
        ["cod"] = "Call of Duty", ["cs2"] = "Counter-Strike 2", ["r5apex"] = "Apex Legends",
        ["Valorant-Win64-Shipping"] = "Valorant", ["FortniteClient-Win64-Shipping"] = "Fortnite",
        ["Marvel-Win64-Shipping"] = "Marvel Rivals",
    };

    internal static string NomDuJeu(string exe)
        => JeuxConnus.TryGetValue(Path.GetFileNameWithoutExtension(exe), out var nom) ? nom : DrDxt.Detection.Game.ReglagesUnreal.Nom(exe);

    internal static string LigneProfil(ProfilDeJeu p, Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var goulot = localiser(CleDuGoulot(p.Goulot));
        return string.Format(fr, localiser("Parties.Profil"), NomDuJeu(p.Jeu), p.Parties,
            p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr), goulot, p.Derniere.LocalDateTime.ToString(localiser("Format.Jour"), fr));
    }

    /// <summary>Chaque verdict mesuré a ses mots ; « limite non mesurée » ne reste qu'au verdict inconnu.</summary>
    internal static string CleDuGoulot(BottleneckState goulot) => goulot switch
    {
        BottleneckState.CpuBoundOrAppBound or BottleneckState.CpuBoundStrong => "Parties.Goulot.Processeur",
        BottleneckState.GpuBound => "Parties.Goulot.Carte",
        BottleneckState.MixedGpuLeaning => "Parties.Goulot.PlutotCarte",
        BottleneckState.FramePacing => "Parties.Goulot.Cadence",
        BottleneckState.ThermalOrPowerThrottle => "Parties.Goulot.CarteBridee",
        BottleneckState.CappedOrVsyncLimited => "Parties.Goulot.Plafond",
        BottleneckState.RamCommitPressure => "Parties.Goulot.Memoire",
        BottleneckState.LatencyOnly => "Parties.Goulot.Latence",
        _ => "Parties.Goulot.Inconnu",
    };

    /// <summary>La partie dans la frise, avec ce que la
    /// veille a vraiment relevé : watts, chaleur du processeur et de la carte, minutes jouées. Rien d'affirmé sans lecture.</summary>
    internal static string ResumeCourt(PartieRelevee p, Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var ligne = p.LatenceAffichageMedianMs is { } latence
            ? string.Format(fr, localiser("Historique.PartieFpsLatence"), p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr), latence.ToString("0.#", fr))
            : string.Format(fr, localiser("Historique.PartieFps"), p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr));
        if (p.WattsMedian is { } w) ligne += " · " + string.Format(fr, localiser("Parties.Watts"), w.ToString("0", fr));
        if (p.CelsiusMax is { } t) ligne += " · " + string.Format(fr, localiser("Parties.Chaleur"), t.ToString("0", fr));
        if (p.CarteCelsiusMax is { } g) ligne += " · " + string.Format(fr, localiser("Parties.Carte"), g.ToString("0", fr));
        if (p.MinutesJouees is { } m)
            ligne += " · " + string.Format(fr, localiser("Parties.Jouees"), m.ToString("0", fr), (p.Fin - p.Debut).TotalMinutes.ToString("0", fr));
        if (p.LatencePcMedianMs is { } pc) ligne += " · " + string.Format(fr, localiser("Parties.LatencePc"), pc.ToString("0.#", fr));
        if (p.PartComposeeMediane is { } c) ligne += " · " + string.Format(fr, localiser("Parties.Composees"), (100 * c).ToString("0", fr));
        if (p.HorsCarteMoMax is { } hc) ligne += " · " + string.Format(fr, localiser("Parties.HorsCarte"), hc.ToString("0", fr));
        return ligne;
    }

    internal static string LignePartie(PartieRelevee p, Func<string, string> localiser)
    {
        var goulot = CleDuGoulot(p.Goulot);
        var fr = CultureInfo.CurrentCulture;
        var ligne = $"{p.Fin.LocalDateTime.ToString(localiser("Format.JourHeure"), fr)} · {NomDuJeu(p.Jeu)} · "
            + $"{p.FpsMedian.ToString("0", fr)} FPS, 1 % low {p.Low1Median.ToString("0", fr)} · {localiser(goulot)}";
        // Les pauses écartées du 1 % low (jeu hors premier plan) se disent : le chiffre ne les cache pas en silence.
        if (p.PausesEcartees > 0)
            ligne += " · " + string.Format(fr, localiser("Parties.PausesEcartees"), p.PausesEcartees);
        // Les minutes vraiment jouées, menus et lobby à part.
        if (p.MinutesJouees is { } jouees)
            ligne += " · " + string.Format(fr, localiser("Parties.Jouees"), jouees.ToString("0", fr), (p.Fin - p.Debut).TotalMinutes.ToString("0", fr));
        // Le processeur pendant la partie, et s'il y ralentit alors qu'il tient au banc.
        if (p.MhzMedian is { } mhz)
            ligne += " · " + string.Format(fr, localiser("Parties.Processeur"), (mhz / 1000).ToString("0.0", fr),
                p.WattsMedian is { } w ? w.ToString("0", fr) : "?");
        if (p.CelsiusMax is { } t)
            ligne += " · " + string.Format(fr, localiser("Parties.Chaleur"), t.ToString("0", fr));
        // Les secondes freinées, par raison, quand le relevé les a lues.
        if (p.Freinage is { Secondes: >= 60 } f)
            ligne += " · " + string.Format(fr, localiser("Parties.Freinage"), Math.Round(100 * f.Part(f.Chaleur)).ToString("0", fr),
                Math.Round(100 * f.Part(f.LimiteW)).ToString("0", fr), Math.Round(100 * f.Part(f.LimiteCourant)).ToString("0", fr));
        if (ReleveDesParties.RalentitEnPartie(p))
        {
            var (cle, part) = ReleveDesParties.CauseDuRalentissement(p);
            ligne += " · " + string.Format(fr, localiser(cle),
                (p.MhzMedian!.Value / 1000).ToString("0.0", fr), (p.BancMhz!.Value / 1000).ToString("0.0", fr), (part ?? 0).ToString("0", fr));
        }
        if (p.CarteCelsiusMax is { } carte)
            ligne += " · " + string.Format(fr, localiser("Parties.Carte"), carte.ToString("0", fr));
        if (ReleveDesParties.VramPleine(p))
            ligne += " · " + string.Format(fr, localiser("Parties.VramPleine"), p.VramPourcentMax!.Value.ToString("0", fr));
        else if (p.VramPourcentMax is { } vram)
            ligne += " · " + string.Format(fr, localiser("Parties.Vram"), vram.ToString("0", fr));
        if (p.Voisins is { Count: > 0 } voisins)
            ligne += " · " + string.Format(fr, localiser("Parties.Voisins"),
                string.Join(", ", voisins.Select(v => $"{v.Nom} ({v.PourcentMax.ToString("0", fr)} %)")));
        if (p.Overlays is { Count: > 0 } overlays)
            ligne += " · " + string.Format(fr, localiser("Parties.Overlays"), string.Join(", ", overlays));
        // Le jeu sur la puce intégrée alors qu'une vraie carte est là, la manipulation de Windows avec.
        if (p.CarteDedieeIgnoree is { } dediee)
            ligne += " · " + string.Format(fr, localiser("Parties.CarteIgnoree"), p.CarteDuJeu ?? "?", dediee, NomDuJeu(p.Jeu));
        if (p.PingInternetMedian is { } net)
            ligne += " · " + string.Format(fr, localiser("Parties.Ping"), net.ToString("0", fr),
                p.PingBoxMedian is { } box ? box.ToString("0", fr) : "?", (p.PertesInternetMax ?? 0).ToString("0", fr));
        return ligne;
    }

    /// <summary>La route qui se dégrade d'une partie à l'autre, et où.</summary>
    internal static string? LigneRoute(IReadOnlyList<PartieRelevee> parties, Func<string, string> localiser)
    {
        if (ReleveDesParties.RouteQuiSeDegrade(parties) is not { } r) return null;
        var fr = CultureInfo.CurrentCulture;
        return string.Format(fr, localiser(r.BoxAussi ? "Parties.Route.Box" : "Parties.Route.Loin"),
            r.Avant.ToString("0", fr), r.Apres.ToString("0", fr), ReleveDesParties.PartiesRoute);
    }

    /// <summary>Ce que la limite de puissance de la carte a fait sur les dernières parties lues par le
    /// pilote, sans 3 + 3 parties en alternance. Seuil de « sans effet » : 1 % de fréquence perdue, sous le bruit des
    /// images d'une partie à l'autre. Moins de <see cref="EchantillonsMecanisme"/> lectures : rien n'est dit.</summary>
    internal const int EchantillonsMecanisme = 60;
    internal const double PerteSansEffetPourcent = 1.0;

    internal static string? LigneMecanismeCarte(IReadOnlyList<PartieRelevee> parties, Func<string, string> localiser)
    {
        // Les mêmes conditions que la dernière partie lue : même jeu, même côté de l'essai
        // (limite posée ou non). Mêler les jeux, ou l'avant et l'après d'un changement de limite, ne jugeait rien.
        if (parties.FirstOrDefault(p => p.MecanismeCarte is not null) is not { } derniere) return null;
        var lues = parties.Where(p => p.MecanismeCarte is not null && p.Jeu == derniere.Jeu && p.Essai == derniere.Essai).ToList();
        if (MecanismeCarte.Cumuler(lues.Select(p => p.MecanismeCarte)) is not { Echantillons: >= EchantillonsMecanisme } m) return null;
        var fr = CultureInfo.CurrentCulture;
        var texte = m.PerteEstimeePourcent is not { } perte
            ? string.Format(fr, localiser("Parties.Mecanisme.Toujours"), lues.Count, NomDuJeu(derniere.Jeu), m.PourcentBride.ToString("0", fr))
            : string.Format(fr, localiser(perte < PerteSansEffetPourcent ? "Parties.Mecanisme.SansEffet" : "Parties.Mecanisme.Coute"),
                lues.Count, NomDuJeu(derniere.Jeu), m.PourcentBride.ToString("0", fr), perte.ToString("0.0", fr));
        return Pluriels.Accorder(texte, fr);
    }

    /// <summary>Ce qu'un overlay coûte chez le joueur, ses parties avec contre ses parties sans ; tant
    /// qu'il n'y en a pas assez de chaque côté, la ligne le dit au lieu d'avancer un chiffre.</summary>
    internal static IEnumerable<string> LignesOverlays(IReadOnlyList<PartieRelevee> parties, Func<string, string> localiser,
        IEnumerable<DateTimeOffset>? changements = null)
    {
        var fr = CultureInfo.CurrentCulture;
        var (ecarts, autres) = OverlaysEnPartie.Comparer(parties, changements);
        foreach (var e in ecarts)
            yield return string.Format(fr, localiser("Parties.OverlayEcart"), e.Overlay, NomDuJeu(e.Jeu),
                e.FpsAvec.ToString("0", fr), e.FpsSans.ToString("0", fr), e.PartiesAvec, e.PartiesSans,
                localiser(e.Verdict switch
                {
                    VerdictEnJeu.Pire => "Parties.OverlayEcart.Pire",
                    VerdictEnJeu.Mieux => "Parties.OverlayEcart.Mieux",
                    _ => "Parties.OverlayEcart.Pareil",
                }));
        foreach (var a in autres.Where(a => a.PartiesAvec > 0))
            yield return string.Format(fr, localiser("Parties.OverlaySansComparaison"), a.Overlay, NomDuJeu(a.Jeu),
                a.PartiesAvec, OverlaysEnPartie.PartiesParCote);
    }
}
