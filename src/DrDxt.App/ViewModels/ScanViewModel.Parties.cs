using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Parties;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>
/// 29/09/2026 (boucle « vivant et prouvé », A3) : « Relever mes parties ». Éteint par défaut ; allumé, DrDXT prend
/// une capture courte toutes les trois minutes pendant une partie et garde, à la fin, une ligne : FPS et 1 % low
/// médians, et ce qui limitait (processeur ou carte). 06/10/2026 (J1b) : aussi DrDXT fermé, voir ReleveAuDemarrage.
/// </summary>
public partial class ScanViewModel
{
    /// <summary>Le stockage du choix et des parties. <c>null</c> dans les tests : rien n'est écrit.</summary>
    internal PartiesMemorisees? MemoireParties { get; set; }

    [ObservableProperty] private bool _releveDesPartiesActif;
    private CancellationTokenSource? _releveDesParties;

    public ObservableCollection<string> LignesParties { get; } = [];
    public bool ADesParties => LignesParties.Count > 0;
    // 04/10/2026 : les lignes de changements ne sont pas des parties ; « aucune partie relevée » reste vrai sans elles.
    private int _partiesAffichees;
    public bool AucunePartie => _partiesAffichees == 0;

    public string LabelPartiesTitre => _localize("Parties.Titre");

    // 08/10/2026 (le mainteneur : « allège dernières parties en gardant les infos importantes ») : la carte se lit en deux temps.
    // En tête, ce qui compte (capture ratée, retraits, gains, changements, profil de chaque jeu) ; le détail (overlays,
    // route, une ligne par partie) se replie sous un lien. LignesParties garde tout, dans le même ordre.
    private int _lignesDeDetail;

    /// <summary>08/10/2026 (critique UI, défaut 8) : les changements pas encore jugés en jeu, en tableau.</summary>
    public System.Collections.ObjectModel.ObservableCollection<LigneEnAttente> ChangementsEnAttente { get; } = new();
    public bool ADesChangementsEnAttente => ChangementsEnAttente.Count > 0;
    public string LabelEnAttenteChangement => _localize("Changement.Tableau.Changement");
    public string LabelEnAttenteDate => _localize("Changement.Tableau.Date");
    public string LabelEnAttenteEnJeu => _localize("Changement.Tableau.EnJeu");
    public IReadOnlyList<string> LignesPartiesResume => LignesParties.Take(LignesParties.Count - _lignesDeDetail).ToList();
    public IReadOnlyList<string> LignesPartiesDetail => LignesParties.Skip(LignesParties.Count - _lignesDeDetail).ToList();
    public bool ADuDetailDeParties => _lignesDeDetail > 0;
    public string LabelDetailParties => _localize("Parties.Detail");
    public string LabelCommentCaMarche => _localize("Parties.CommentCaMarche");

    private void AnnoncerResumeEtDetail()
    {
        OnPropertyChanged(nameof(LignesPartiesResume));
        OnPropertyChanged(nameof(LignesPartiesDetail));
        OnPropertyChanged(nameof(ADuDetailDeParties));
    }

    // ── 04/10/2026 (plan 3b.3) : « prêt à jouer » en quelques secondes ───────────────────────────────────
    public ObservableCollection<string> LignesPretAJouer { get; } = [];
    [ObservableProperty] private bool _pretAJouerEnCours;
    public string LabelPretAJouer => _localize("PretAJouer.Bouton");

    /// <summary>La mesure réelle ; l'appli la pose au démarrage, <c>null</c> = bouton sans effet (tests).</summary>
    internal Func<CancellationToken, Task<EtatAvantPartie>>? MesurerAvantPartie { get; set; }

    [RelayCommand]
    private async Task VerifierPretAJouer()
    {
        if (MesurerAvantPartie is null || PretAJouerEnCours) return;
        PretAJouerEnCours = true;
        LignesPretAJouer.Clear();
        LignesPretAJouer.Add(_localize("PretAJouer.EnCours"));
        try
        {
            var e = await MesurerAvantPartie(CancellationToken.None);
            var genes = PretAJouer.Genes(e, _localize);
            LignesPretAJouer.Clear();
            LignesPretAJouer.Add(_localize(genes.Count == 0 ? "PretAJouer.Ok" : "PretAJouer.AGenes"));
            foreach (var g in genes) LignesPretAJouer.Add(g);
            LignesPretAJouer.Add(PretAJouer.Mesure(e, _localize));
        }
        catch (Exception)
        {
            LignesPretAJouer.Clear();
            LignesPretAJouer.Add(_localize("PretAJouer.Echec"));
        }
        finally { PretAJouerEnCours = false; }
    }

    // ── 04/10/2026 (plan 3b.2) : l'historique de la machine, une ligne du temps ─────────────────────────────
    public ObservableCollection<string> LignesHistorique { get; } = [];
    /// <summary>08/10/2026 (critique UI, défaut 12) : les événements datés de la frise, chacun avec son genre (sa couleur).</summary>
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

    /// <summary>Revue du 04/10 (P1) : les journaux d'événements se lisent hors du fil de l'interface (au démarrage, ils
    /// le bloquaient). Les lignes se posent ensuite sur le contexte de l'appelant.</summary>
    internal async Task AfficherHistoriqueAsync(IReadOnlyList<PartieRelevee> parties)
    {
        var generation = Interlocked.Increment(ref _generationHistorique);
        (IReadOnlyList<DateTimeOffset> Arrets, IReadOnlyList<DateTimeOffset> Erreurs) incidents = ([], []);
        try { if (LireIncidents is { } lire) incidents = await Task.Run(lire); }
        catch (Exception) { }
        IReadOnlyList<string> depuisLeScan = [];
        try { if (LireChangementsDepuisLeScan is { } lireChangements) depuisLeScan = await Task.Run(lireChangements); }
        catch (Exception) { }
        IReadOnlyList<DrDxt.Detection.Repair.FaitDate> misesAJour = [];
        try { misesAJour = await MisesAJourAsync(); }
        catch (Exception) { }
        // 08/10/2026 : sans contexte d'interface (tests), deux fins de partie rapprochées reprenaient sur deux fils et vidaient
        // ou remplissaient les mêmes listes en même temps (NullReference vue une fois en suite complète). Dans l'appli, tout
        // revient sur le fil de l'interface : le verrou ne coûte rien.
        // 08/10/2026 (revue) : une lecture lente finie après une plus récente ne réécrit pas l'historique avec des parties périmées.
        lock (_verrouHistorique)
            if (generation == Volatile.Read(ref _generationHistorique))
                AfficherHistorique(parties, incidents.Arrets, incidents.Erreurs, depuisLeScan, misesAJour);
    }

    private readonly object _verrouHistorique = new();
    private int _generationHistorique;

    /// <summary>07/10/2026 (D7, enquête n° 17) : les pilotes (setupapi) et les mises à jour de Windows Update posés sans
    /// DrDXT, à leur heure réelle, hors du fil de l'écran. <c>null</c> = rien (tests).</summary>
    internal Func<IReadOnlyList<DrDxt.Detection.Repair.FaitDate>>? LireMisesAJourDeLaMachine { get; set; }

    /// <summary>Revue F-47 : les journaux d'installation et l'historique de Windows Update se relisaient à chaque fin
    /// de partie (des centaines de Mo) ; ils changent une fois par mois. Une lecture par heure au plus.</summary>
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

    /// <summary>Revue F-40 : les bilans des changements de DrDXT bornés aussi par les pilotes et Windows Update ; sinon
    /// le gain d'un pilote passait pour celui d'un levier. Tant qu'elles ne sont pas lues, rien ne se fige.</summary>
    private IReadOnlyList<(Changement C, BilanDuChangement B)> BilansBornes(IReadOnlyList<Changement> journal, IReadOnlyList<PartieRelevee> parties)
    {
        var machine = InstantsDesMisesAJour();
        return BilansDesChangements(journal, parties, machine is null ? null : JournalChangements, machine ?? []);
    }

    /// <summary>
    /// 07/10/2026 (D7) : ce qu'une mise à jour faite toute seule a coûté en jeu, jugée comme un changement de DrDXT (mêmes
    /// parties du même jeu avant et après, même règle de séparation nette), bornée par tout autre changement, de DrDXT ou
    /// de la machine. Avant, l'historique disait « un nouveau scan dira ce que ça a coûté » et rien ne le disait. Les mises
    /// à jour d'une même minute se jugent ensemble ; une mise à jour sans aucune partie d'avant ne se dit pas, elle ne se
    /// jugera jamais.
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

    /// <summary>04/10/2026 (plan 3b.1) : ce qui a été installé, retiré ou mis à jour depuis le dernier scan, relu à
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
            // 08/10/2026 (critique UI, défaut 12) : la frise devient une liste à points (vert partie, rouge arrêt ou erreur, violet
            // changement), le jeu à gauche et ses FPS alignés à droite.
            var frise = FriseDeLaMachine.Construire(JournalChangements?.Lire() ?? [], parties, arrets, erreurs, DateTimeOffset.Now,
                c => c.Libelle,
                p => NomDuJeu(p.Jeu),
                _localize("Historique.Arret"), _localize("Historique.Erreur"),
                p => p.LatenceAffichageMedianMs is { } latence
                    ? string.Format(fr, _localize("Historique.PartieFpsLatence"), p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr), latence.ToString("0.#", fr))
                    : string.Format(fr, _localize("Historique.PartieFps"), p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr)));
            // 04/10/2026 (plan 3b.5) : le bilan de la semaine, en tête.
            if (frise.Count > 0)
                LignesHistorique.Add(LigneBilan(BilanDeLaSemaine.Construire(parties, JournalChangements?.Lire() ?? [], arrets, erreurs, DateTimeOffset.Now), _localize));
            foreach (var e in frise) FriseHistorique.Add(e);
        }
        catch (Exception) { /* une lecture ratée laisse la frise vide, jamais fausse */ }
        OnPropertyChanged(nameof(AHistorique));
    }
    public string LabelPartiesDescription => _localize("Parties.Description");
    public string LabelPartiesAucune => _localize("Parties.Aucune");
    public string LabelBasculerReleve => _localize("Parties.Libelle");   // 08/10/2026 (défaut 10) : l'état est dans l'interrupteur

    // UI1 (08/10/2026) : le nom que le joueur connaît. Les jeux suivis par leur exécutable (liste de RenderQueueCapTweak),
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
            p.FpsMedian.ToString("0", fr), p.Low1Median.ToString("0", fr), goulot, p.Derniere.LocalDateTime.ToString("dd/MM", fr));
    }

    /// <summary>04/10/2026 : chaque verdict mesuré a ses mots ; « limite non mesurée » ne reste qu'au verdict inconnu.</summary>
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

    internal static string LignePartie(PartieRelevee p, Func<string, string> localiser)
    {
        var goulot = CleDuGoulot(p.Goulot);
        var fr = CultureInfo.CurrentCulture;
        var ligne = $"{p.Fin.LocalDateTime.ToString("dd/MM HH:mm", fr)} · {NomDuJeu(p.Jeu)} · "
            + $"{p.FpsMedian.ToString("0", fr)} FPS, 1 % low {p.Low1Median.ToString("0", fr)} · {localiser(goulot)}";
        // 04/10/2026 (plan 1.1) : le processeur pendant la partie, et s'il y ralentit alors qu'il tient au banc.
        if (p.MhzMedian is { } mhz)
            ligne += " · " + string.Format(fr, localiser("Parties.Processeur"), (mhz / 1000).ToString("0.0", fr),
                p.WattsMedian is { } w ? w.ToString("0", fr) : "?")
                + (p.CelsiusMax is { } t ? string.Format(fr, localiser("Parties.Chaleur"), t.ToString("0", fr)) : "");
        if (ReleveDesParties.RalentitEnPartie(p))
            ligne += " · " + string.Format(fr,
                localiser(p.CelsiusMax >= ReleveDesParties.SeuilChaleurC ? "Parties.RalentitChaleur" : "Parties.Ralentit"),
                (p.MhzMedian!.Value / 1000).ToString("0.0", fr), (p.BancMhz!.Value / 1000).ToString("0.0", fr));
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
        // 07/10/2026 (A4) : le jeu sur la puce intégrée alors qu'une vraie carte est là, la manipulation de Windows avec.
        if (p.CarteDedieeIgnoree is { } dediee)
            ligne += " · " + string.Format(fr, localiser("Parties.CarteIgnoree"), p.CarteDuJeu ?? "?", dediee, NomDuJeu(p.Jeu));
        if (p.PingInternetMedian is { } net)
            ligne += " · " + string.Format(fr, localiser("Parties.Ping"), net.ToString("0", fr),
                p.PingBoxMedian is { } box ? box.ToString("0", fr) : "?", (p.PertesInternetMax ?? 0).ToString("0", fr));
        return ligne;
    }

    /// <summary>07/10/2026 (D6, enquête n° 14) : la route qui se dégrade d'une partie à l'autre, et où.</summary>
    internal static string? LigneRoute(IReadOnlyList<PartieRelevee> parties, Func<string, string> localiser)
    {
        if (ReleveDesParties.RouteQuiSeDegrade(parties) is not { } r) return null;
        var fr = CultureInfo.CurrentCulture;
        return string.Format(fr, localiser(r.BoxAussi ? "Parties.Route.Box" : "Parties.Route.Loin"),
            r.Avant.ToString("0", fr), r.Apres.ToString("0", fr), ReleveDesParties.PartiesRoute);
    }

    /// <summary>J-MECA (09/10/2026) : ce que la limite de puissance de la carte a fait sur les dernières parties lues par le
    /// pilote, sans 3 + 3 parties en alternance. Seuil de « sans effet » : 1 % de fréquence perdue, sous le bruit des
    /// images d'une partie à l'autre. Moins de <see cref="EchantillonsMecanisme"/> lectures : rien n'est dit.</summary>
    internal const int EchantillonsMecanisme = 60;
    internal const double PerteSansEffetPourcent = 1.0;

    internal static string? LigneMecanismeCarte(IReadOnlyList<PartieRelevee> parties, Func<string, string> localiser)
    {
        // MECA2 (revue du 09/10/2026) : les mêmes conditions que la dernière partie lue : même jeu, même côté de l'essai
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

    /// <summary>07/10/2026 (D10) : ce qu'un overlay coûte chez le joueur, ses parties avec contre ses parties sans ; tant
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

    /// <summary>06/10/2026 (plan 4.4) : un réglage posé par DrDXT et gardé, tel que le journal des leviers le connaît.</summary>
    public sealed record LevierPose(string Id, string Titre, string NomAccessible);

    /// <summary>06/10/2026 (plan 4.4) : ce que DrDXT a posé et garde, chacun annulable depuis l'écran. Avant, « Annuler »
    /// n'existait que juste après un réglage de carte graphique ; un levier d'« Optimiser » ne partait que par le retrait
    /// automatique.</summary>
    public ObservableCollection<LevierPose> LeviersPoses { get; } = [];

    public bool ALeviersPoses => LeviersPoses.Count > 0;

    [ObservableProperty] private string? _messageLeviers;

    public string LabelLeviersTitre => _localize("Leviers.Titre");
    public string LabelLeviersDescription => _localize("Leviers.Description");
    public string LabelLeviersAnnuler => _localize("Leviers.BoutonAnnuler");

    internal void ChargerLeviersPoses()
    {
        LeviersPoses.Clear();
        var poses = _journalLeviers?.Lire() ?? [];
        foreach (var s in poses.Where(EncorePoseParDrDxt))
        {
            var titre = _localize(TweakDisplayKeys.TitleKey(s.TweakId));
            // Revue du 06/10 (P2) : le lecteur d'écran annonce l'action ET le réglage, pas le seul réglage.
            LeviersPoses.Add(new LevierPose(s.TweakId, titre, $"{_localize("Leviers.BoutonAnnuler")} : {titre}"));
        }
        OnPropertyChanged(nameof(ALeviersPoses));
    }

    /// <summary>Revue du 06/10 : seulement ce qui porte ENCORE la valeur posée par DrDXT, la même règle que la
    /// désinstallation. Revenu à la valeur d'avant (désinstallation, mise à jour) : rien à annuler, et le scan le dit déjà.
    /// Modifié depuis par le joueur : « Annuler » réécrirait la valeur d'avant par-dessus son choix ; pas montré. Sans
    /// valeur posée connue ou non relisable : montré seulement s'il n'est pas revenu à l'origine.</summary>
    private bool EncorePoseParDrDxt(TweakSnapshot s)
    {
        // Vu sur capture (08/10/2026) : origine égale à la valeur posée = DrDXT n'a rien changé ; rien à annuler.
        if (string.Equals(s.AppliedValueJson, s.OriginalValueJson, StringComparison.Ordinal)) return false;
        if (_registreGains is not { } registre) return true;
        if (s.AppliedValueJson is { Length: > 0 } posee)
        {
            var surPosee = DrDxt.Application.Tweaks.TweakReadback.Relire(new TweakSnapshot(s.TweakId, posee), registre).Etat;
            if (surPosee == DrDxt.Application.Tweaks.EtatRelu.RevenuAvantApplication) return true; // encore celle de DrDXT
            if (surPosee == DrDxt.Application.Tweaks.EtatRelu.DifferentDeAvant) return false;     // revenue ou changée
        }
        return DrDxt.Application.Tweaks.TweakReadback.Relire(s, registre).Etat != DrDxt.Application.Tweaks.EtatRelu.RevenuAvantApplication;
    }

    /// <summary>Le même chemin que le retrait automatique : la valeur d'avant par le levier lui-même, puis le journal et le
    /// manifeste signé. Un refus ne retire rien et le dit.</summary>
    [RelayCommand]
    private async Task AnnulerLevierAsync(string id)
    {
        var titre = _localize(TweakDisplayKeys.TitleKey(id));
        // Revue du 06/10 (P2) : retiré entre-temps (retrait automatique) ou levier inconnu : dit, pas muet.
        if (_journalLeviers?.Lire().FirstOrDefault(s => s.TweakId == id) is not { } snapshot
            || LevierParId?.Invoke(id) is not { } levier)
        {
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.Introuvable"), titre);
            ChargerLeviersPoses();
            return;
        }
        var r = await levier.RevertAsync(snapshot);
        if (!r.IsOk)
        {
            MessageLeviers = string.Format(CultureInfo.CurrentCulture, _localize("Leviers.PasAnnule"), titre, r.Error?.Message);
            return;
        }
        _journalLeviers.Retirer(id);
        ArreterEssaiSurAnnulation(id);
        var sorti = await RetirerDuManifesteAsync(id);
        MessageLeviers = string.Format(CultureInfo.CurrentCulture,
                _localize(levier.TakesEffectAfterReboot ? "Leviers.AnnuleAuRedemarrage" : "Leviers.Annule"), titre)
            + (sorti.IsOk ? "" : " " + string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                titre, sorti.Error?.Message));
        ChargerLeviersPoses();
    }

    /// <summary>08/10/2026 (le mainteneur : « DrDXT n'est pas censé me dire non ») : les arrêts passés ne refusent plus Optimiser ; un
    /// réglage posé par DrDXT et suivi d'un arrêt brutal hors veille dans la semaine se rend seul (<see cref="DrDxt.Application.Autopilot.GardeDIncident"/>).
    /// Sans le helper joignable sans autorisation Windows, il se dit à rendre, jamais d'UAC sans clic.</summary>
    /// <summary>GARDE2 : les vidages à chaud du pilote graphique, branchés par l'App ; null = non lus.</summary>
    internal Func<IReadOnlyList<(DateTimeOffset Quand, int Code)>?>? LireVidagesCarte { get; set; }

    private IReadOnlyList<(DateTimeOffset Quand, int Code)>? LireVidagesCarteSur()
    {
        try { return LireVidagesCarte?.Invoke(); }
        catch (Exception) { return null; }
    }

    private async Task<IReadOnlyList<string>> RendreApresUnArretAsync()
    {
        if (JournalChangements is null || _journalLeviers is null || LevierParId is null) return [];
        var (_, arrets) = await Task.Run(LireStabiliteSure).ConfigureAwait(true);
        var vidages = await Task.Run(LireVidagesCarteSur).ConfigureAwait(true);
        var journal = JournalChangements.Lire();
        var poses = _journalLeviers.Lire().Select(s => s.TweakId).Distinct()
            .Select(id => (Id: id, Pose: journal.Where(c => c.Cle == "pose:" + id).OrderByDescending(c => c.Quand).FirstOrDefault()))
            .Where(x => x.Pose is not null).Select(x => (x.Id, x.Pose!.Quand)).ToList();
        var dits = new List<string>();
        foreach (var (id, arret, vidage) in DrDxt.Application.Autopilot.GardeDIncident.ARendre(poses, arrets, vidages))
        {
            var titre = _localize(TweakDisplayKeys.TitleKey(id));
            var quand = arret.Quand.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
            var code = string.Format(CultureInfo.InvariantCulture, "0x{0:X}", arret.CodeArret);
            if (LevierParId(id) is { RequiresElevation: true } && HelperJoignableSansUac is { } joignable && !await joignable())
            {
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Garde.ARendre"), titre, quand));
                continue;
            }
            await AnnulerLevierAsync(id);
            // GARDE1 : un rendu raté se dit sur la carte (il reste au journal et se retente au prochain affichage).
            if (_journalLeviers.Lire().Any(s => s.TweakId == id))
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Garde.Rate"), titre, quand, MessageLeviers));
            else
            {
                // GARDE1 : le rendu se note (Optimiser ne le repose pas avant une semaine) ; au redémarrage, il le dit.
                JournalChangements.Ajouter(new Changement(Horloge(), id, null, ClePoseGarde + id));
                var auRedemarrage = LevierParId(id) is { TakesEffectAfterReboot: true };
                // GARDE2 : un vidage à chaud du pilote se dit comme tel, pas comme un arrêt brutal.
                dits.Add(string.Format(CultureInfo.CurrentCulture, _localize(vidage
                        ? (auRedemarrage ? "Garde.RenduIncidentCarteAuRedemarrage" : "Garde.RenduIncidentCarte")
                        : (auRedemarrage ? "Garde.RenduAuRedemarrage" : "Garde.Rendu")), titre, quand, code));
            }
        }
        return dits;
    }

    /// <summary>Au lancement : les parties gardées, et le relevé relancé si le joueur l'avait laissé allumé.</summary>
    internal void ChargerParties()
    {
        if (MemoireParties is null) return;
        var parties = MemoireParties.Lire();
        Afficher(parties);
        // 09/10/2026 : un essai en cours se dit dès le lancement, avec son bouton d'arrêt (avant, il fallait un scan ou une fin de partie).
        DireEssai(parties);
        OnPropertyChanged(nameof(EssaiEnCours));
        if (MemoireParties.EstAllume() && !ReleveDesPartiesActif) _ = BasculerReleveDesPartiesCommand.ExecuteAsync(null);
    }

    /// <summary>04/10/2026 (plan 1.3) : les changements datés, jugés sur les parties. L'appli pose le fichier réel au
    /// démarrage ; <c>null</c> = rien n'est noté (les tests qui ne le vérifient pas).</summary>
    internal JournalDesChangements? JournalChangements { get; set; }

    /// <summary>07/10/2026 (J5) : l'heure des changements notés et des parties relevées, et celle du démarrage du PC. Les
    /// tests rejouent une histoire de plusieurs jours sur une horloge factice ; l'appli garde l'horloge réelle.</summary>
    internal Func<DateTimeOffset> Horloge { get; set; } = () => DateTimeOffset.Now;
    // 07/10/2026 (F-32) : le dernier démarrage, rapide ou complet, et pas seulement le compteur depuis le démarrage.
    internal Func<DateTimeOffset> DemarrageDuPc { get; set; } = () => DrDxt.Detection.Hardware.DemarrageDuSysteme.Valeur;

    /// <summary>Un changement de réglage BIOS vu entre deux références processeur. Il a pris effet au démarrage du PC :
    /// c'est cette heure qui sépare les parties d'avant de celles d'après, pas celle du scan.</summary>
    /// <param name="mesureAvant">Revue du 04/10 (P1) : la dernière mesure sous l'ancien réglage. Le PC a pu redémarrer
    /// plusieurs fois depuis le changement ; seule cette fenêtre est sûre.</param>
    internal void NoterChangementBios(string? avant, string? apres, DateTimeOffset? mesureAvant = null)
    {
        if (avant is null || apres is null || avant == apres || JournalChangements is null) return;
        JournalChangements.Ajouter(new Changement(DemarrageDuPc(),
            string.Format(CultureInfo.CurrentCulture, _localize("Changement.Bios"), avant, apres), mesureAvant));
        // La carte se refait tout de suite : le changement s'y lit sans attendre le prochain lancement.
        Afficher(MemoireParties?.Lire() ?? []);
    }

    /// <summary>04/10/2026 (plan 2.1) : les réglages que le joueur bascule lui-même, relevés à chaque scan. L'appli
    /// pose le fichier réel au démarrage ; <c>null</c> = aucun suivi (les tests qui ne le vérifient pas).</summary>
    internal SuiviDesReglages? SuiviReglages { get; set; }

    private SettingsState? _reglagesLus;
    private HardwareInventory? _materielLu;

    /// <summary>Les états suivis, par identifiant stable ; un réglage illisible (Unknown) n'y figure pas.
    /// 04/10/2026 (plan 2.3) : le mode Jeu et HAGS, que DrDXT peut poser (leviers au redémarrage, sans mesure immédiate)
    /// ou que le joueur bascule : leur bascule se juge désormais sur les parties, comme un réglage du BIOS. HAGS est lu
    /// dans son état EFFECTIF (ce que le pilote applique), pas dans le registre qui n'agit qu'au redémarrage.</summary>
    /// <param name="instantReplay">07/10/2026 (J5b) : lu par l'appelant (<see cref="LireInstantReplay"/>) ; il était lu ici, dans
    /// le registre du PC, même par les tests.</param>
    internal static IReadOnlyDictionary<string, string> EtatsSuivis(SettingsState? st = null, HardwareInventory? hw = null,
        bool? instantReplay = null)
    {
        var etats = new Dictionary<string, string>(StringComparer.Ordinal);
        static string? OnOff(TriState t) => t switch { TriState.On => "on", TriState.Off => "off", _ => null };
        if (st is not null && OnOff(st.GameMode) is { } gm) etats["windows.game_mode"] = gm;
        if (st is not null && OnOff(st.HagsEffective) is { } hags) etats["windows.hags"] = hags;
        // 06/10/2026 (L-PROF) : le fichier d'échange, que `fixit.sane_pagefile` remet au redémarrage ; sa bascule se juge
        // sur les parties comme HAGS, et « moins bien en jeu » le retire.
        if (st is not null && OnOff(st.PagefileConfigured) is { } pf) etats["memoire.fichier_echange"] = pf;
        // 04/10/2026 (plan 2.4) : la vraie optimisation de la mémoire se fait au BIOS (fréquence, tension, sous-timings),
        // et les timings ne se lisent pas sans pilote noyau. Ce qui se lit sans
        // pilote, la fréquence et la tension, suffit à DATER un changement : il se juge alors sur les parties, comme le
        // reste, et le banc mémoire en dit l'avant et l'après.
        // Revue du 04/10 (P2) : l'ordre de WMI n'est pas garanti ; la plus haute fréquence configurée fait foi.
        if (hw?.Memory.Where(m => m.ConfiguredMhz > 0).OrderByDescending(m => m.ConfiguredMhz).FirstOrDefault() is { } ram)
        {
            etats["memoire.frequence"] = $"{ram.ConfiguredMhz} MT/s";
            if (ram.ConfiguredVoltageMv > 0)
                etats["memoire.tension"] = (ram.ConfiguredVoltageMv / 1000.0).ToString("0.00", CultureInfo.InvariantCulture) + " V";
        }
        if (instantReplay is { } ir)
            etats["nvidia.instant_replay"] = ir ? "on" : "off";
        return etats;
    }

    /// <summary>07/10/2026 (J5b) : l'Instant Replay de NVIDIA. L'appli pose la lecture du registre ; <c>null</c> = non lu.</summary>
    internal Func<bool?>? LireInstantReplay { get; set; }

    /// <summary>07/10/2026 (J5b) : les seuls réglages suivis, lus sans scan. L'appli pose la lecture (registre, état effectif
    /// de HAGS) ; <c>null</c> = rien n'est relevé hors du scan.</summary>
    internal Func<IReadOnlyDictionary<string, string>>? LireReglagesSuivis { get; set; }

    /// <summary>Les réglages suivis lus sans scan complet : le mode Jeu et le fichier d'échange dans le registre, HAGS dans son
    /// état effectif, l'Instant Replay. Quelques lectures, aucune mesure ; la mémoire (WMI) attend le scan, et un réglage non
    /// relu garde son dernier état connu (<see cref="SuiviDesReglages"/>).</summary>
    internal static IReadOnlyDictionary<string, string> EtatsSuivisSansScan(DrDxt.Detection.Settings.IRegistryProvider registre,
        DrDxt.Detection.Settings.IHagsEffectiveReader? hags, bool? instantReplay) =>
        EtatsSuivis(new SettingsState(TriState.Unknown, TriState.Unknown, "", TriState.Unknown, registre.GetGameMode(), null,
            TriState.Unknown, PagefileConfigured: registre.GetPagefile(), HagsEffective: hags?.Lire() ?? TriState.Unknown), null, instantReplay);

    /// <summary>
    /// 07/10/2026 (J5b) : les bascules ne se relevaient qu'au scan. Un joueur qui ne rescannait pas après Optimiser n'était
    /// jamais jugé, ni protégé par le retrait. Elles se relèvent aussi à l'ouverture de l'appli et dans le relevé de fond
    /// (lancé à l'ouverture de session, juste après le redémarrage qui les a fait basculer). Le relevé de fond ne pose
    /// pas <see cref="LevierParId"/> : il note et juge, il ne retire rien. Il n'a ni fenêtre pour le dire ni helper pour
    /// défaire (aucune élévation ne surgit au démarrage de Windows) ; le retrait part à l'ouverture suivante de l'appli, sur
    /// les mêmes bilans, et la carte le dit.
    /// </summary>
    /// <param name="seulement">Les seuls réglages à relever ; <c>null</c> = tous. Un réglage non relu garde son dernier
    /// état connu, et sa bascule se relève plus tard.</param>
    internal async Task ReleverReglagesHorsScanAsync(IReadOnlySet<string>? seulement = null)
    {
        if (LireReglagesSuivis is not { } lire) return;
        IReadOnlyDictionary<string, string> etats;
        try { etats = await Task.Run(lire); }
        catch (Exception) { return; }
        if (seulement is not null) etats = etats.Where(e => seulement.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value);
        try { ReleverReglagesSuivis(etats, Horloge()); }
        catch (Exception) { /* un relevé raté ne coûte que ce relevé */ }
    }

    /// <summary>Revue F-25 : les réglages suivis qu'un levier fait basculer dès son écriture, relevés juste après la
    /// pose. Pas le fichier d'échange : le registre le dit posé tout de suite, mais il ne prend effet qu'au redémarrage
    /// (HAGS se lit dans son état effectif, qui attend aussi le redémarrage).</summary>
    internal static readonly IReadOnlySet<string> ReglagesAEffetImmediat = new HashSet<string>(StringComparer.Ordinal) { "windows.game_mode" };

    /// <summary>Ce qui a basculé depuis le scan précédent entre au journal, et la carte se refait.</summary>
    internal void ReleverReglagesSuivis(IReadOnlyDictionary<string, string> etats, DateTimeOffset maintenant)
    {
        if (SuiviReglages is null || JournalChangements is null) return;
        string Valeur(string v) => _localize(v == "on" ? "Reglage.Allume" : v == "off" ? "Reglage.Eteint" : v);
        var changes = SuiviReglages.Relever(etats, maintenant, (id, avant, apres) => string.Format(CultureInfo.CurrentCulture,
            _localize("Changement.Reglage"), _localize("Reglage." + id), Valeur(avant), Valeur(apres)));
        var journal = changes.Count > 0 ? JournalChangements.Lire() : [];
        var demarrage = DemarrageDuPc();
        foreach (var c in changes) JournalChangements.Ajouter(DaterSurLaPose(c, journal, demarrage));
        if (changes.Count > 0) Afficher(MemoireParties?.Lire() ?? []);
    }

    /// <summary>
    /// 07/10/2026 (J5, histoire rejouée) : un réglage que DrDXT pose au redémarrage bascule au démarrage qui suit, mais le
    /// scan ne le voit qu'au scan suivant, des jours plus tard parfois. Daté à ce scan et borné au scan d'avant, le
    /// changement laissait hors jeu toutes les parties jouées entre les deux : celles d'avant la pose comme celles d'après
    /// le redémarrage. Il ne se jugeait jamais, et rien ne se retirait. Quand la pose de son levier tombe dans la fenêtre,
    /// elle borne le début ; le démarrage du PC, s'il la suit, en est la fin (au pire, la fenêtre reste celle du scan).
    /// </summary>
    internal static Changement DaterSurLaPose(Changement c, IReadOnlyList<Changement> journal, DateTimeOffset demarrage)
    {
        if (c.Cle is not { } cle || !cle.EndsWith(":on", StringComparison.Ordinal)
            || !LevierQuiAllume.TryGetValue(cle[..^3], out var id)) return c;
        var pose = journal.Where(p => p.Cle == "pose:" + id && p.Quand <= c.Quand && (c.Depuis is not { } d || p.Quand >= d))
            .OrderByDescending(p => p.Quand).FirstOrDefault();
        if (pose is null) return c;
        return c with { Depuis = pose.Quand, Quand = demarrage > pose.Quand && demarrage < c.Quand ? demarrage : c.Quand };
    }

    /// <summary>Revue F-25 : un réglage que DrDXT allume, dont la bascule a un début connu (la pose, une fois daté par
    /// <see cref="DaterSurLaPose"/>).</summary>
    private static bool DateeSurLaPose(Changement c) => c.Depuis is not null && c.Cle is { } cle
        && cle.EndsWith(":on", StringComparison.Ordinal) && LevierQuiAllume.ContainsKey(cle[..^3]);

    /// <summary>Deux réglages allumés partis du même début : le mode Jeu relevé à la pose et HAGS au redémarrage, posés par
    /// le même clic. On ne sait pas lequel a joué sur quelles parties ; ils se jugent ensemble.</summary>
    private static bool DuMemeClic(Changement a, Changement b) => DateeSurLaPose(a) && DateeSurLaPose(b) && a.Depuis == b.Depuis;

    /// <summary>04/10/2026 (plan 2.3) : le levier d'un catalogue par son identifiant. L'appli le pose au démarrage ;
    /// <c>null</c> = aucun retrait automatique (les tests qui ne le vérifient pas).</summary>
    internal Func<string, ITweak?>? LevierParId { get; set; }

    /// <summary>07/10/2026 (revue F-23) : le helper répond-il déjà, sans être lancé ? Le retrait automatique part à
    /// l'ouverture de l'App, sans geste du joueur : il ne doit jamais faire surgir une invite UAC. Helper absent ⇒ rien
    /// n'est retiré, la carte dit « annule-le d'un clic » et le bouton Annuler porte l'invite. <c>null</c> (tests) =
    /// joignable.</summary>
    internal Func<Task<bool>>? HelperJoignableSansUac { get; set; }

    /// <summary>Revue F-29 : le retrait lancé par le dernier affichage, pour que les tests l'attendent au lieu de
    /// compter sur un helper factice qui rend une tâche déjà finie.</summary>
    internal Task DernierRetrait { get; private set; } = Task.CompletedTask;

    /// <summary>Le levier de DrDXT qui allume chaque réglage suivi.</summary>
    internal static readonly IReadOnlyDictionary<string, string> LevierDuReglage = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["windows.hags"] = "latency.hagsOn",
        ["windows.game_mode"] = "latency.gameModeOn",
        // 07/10/2026 (revue F-9) : plus le fichier d'échange. C'est un levier de fiabilité (plantages, vidages), sans
        // gain d'images annoncé : un « moins bien en jeu » sur trois parties ne doit pas le retirer.
    };

    /// <summary>07/10/2026 (J5) : les réglages suivis que DrDXT allume, pour dater leur bascule sur la pose. Plus large que
    /// <see cref="LevierDuReglage"/> (déclaré après lui, qu'il copie) : le fichier d'échange se date comme les autres, il
    /// ne se retire pas (F-9).</summary>
    internal static readonly IReadOnlyDictionary<string, string> LevierQuiAllume =
        new Dictionary<string, string>(LevierDuReglage, StringComparer.Ordinal) { ["memoire.fichier_echange"] = "fixit.sane_pagefile" };

    /// <summary>Revue du 04/10 (P0) : la pose d'un levier se note avec sa date (clé « pose:id »), pour que le
    /// retrait ne touche que ce que DrDXT a posé. Elle n'est pas affichée : la bascule, elle, l'est.</summary>
    internal void NoterPoses(IEnumerable<string> leviersPoses, DateTimeOffset maintenant)
    {
        if (JournalChangements is null) return;
        // 08/10/2026 (garde d'incident) : toutes les poses se datent, plus seulement celles qui allument un réglage suivi.
        foreach (var id in leviersPoses.Distinct())
            JournalChangements.Ajouter(new Changement(maintenant, id, null, "pose:" + id));
    }

    private bool _retraitEnCours;

    /// <summary>Règle 4 du plan : un levier sans gain se retire tout seul. Un changement jugé « moins bien en jeu »,
    /// posé par un levier de DrDXT encore au journal des leviers, est annulé (valeur d'origine relue au moment de la
    /// pose) ; le journal le perd, et la carte le dit. Rendu : les phrases à ajouter en tête de la carte.</summary>
    internal async Task<IReadOnlyList<string>> RetirerCeQuiCouteAsync(IReadOnlyList<PartieRelevee> parties)
    {
        var dits = new List<string>();
        // Revue F-40 : un retrait se juge avec les mises à jour de la machine lues.
        try { await MisesAJourAsync(); }
        catch (Exception) { }
        // 07/10/2026 (L-VOISINS) : le calmeur de voisins n'est pas un levier du catalogue ; il se juge pareil.
        if (JournalChangements is not null) dits.AddRange(RetirerLeCalmeurSIlCoute(JournalChangements.Lire(), parties));
        if (JournalChangements is null || LevierParId is null || _journalLeviers is null || _retraitEnCours) return dits;
        _retraitEnCours = true;
        try
        {
            dits.AddRange(await RendreApresUnArretAsync());
            var journal = JournalChangements.Lire();
            foreach (var (reglage, id) in LevierDuReglage)
            {
                // Seule la DERNIÈRE bascule de ce réglage compte : une plus ancienne a déjà été suivie d'autre chose.
                var bascules = journal.Where(c => c.Cle?.StartsWith(reglage + ":", StringComparison.Ordinal) == true)
                    .OrderByDescending(c => c.Quand).ToList();
                if (bascules.FirstOrDefault() is not { } c || c.Cle != reglage + ":on") continue;
                // Elle doit venir de la pose de DrDXT : la pose la précède, et aucune autre bascule ne les sépare.
                var pose = journal.Where(p => p.Cle == "pose:" + id).OrderByDescending(p => p.Quand).FirstOrDefault();
                if (pose is null || pose.Quand > c.Quand) continue;
                if (bascules.Skip(1).FirstOrDefault() is { } precedente && precedente.Quand >= pose.Quand) continue;
                // 07/10/2026 (revue F-7) : le même bilan que l'écran (bornes des changements voisins, bilan figé).
                if (BilansBornes(journal, parties).FirstOrDefault(x => x.C == c).B is not { Verdict: VerdictEnJeu.Pire } bilan) continue;
                if (_journalLeviers.Lire().FirstOrDefault(s => s.TweakId == id) is not { } snapshot || LevierParId(id) is not { } levier) continue;
                if (HelperJoignableSansUac is { } joignable && levier.RequiresElevation && !await joignable())
                {
                    dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Changement.ARetirer"), c.Libelle));
                    continue;
                }
                var r = await levier.RevertAsync(snapshot);
                if (!r.IsOk) continue;
                // 07/10/2026 (J5) : le verdict qui a fait retirer est figé ; sinon les parties jouées après le retrait, encore
                // dans sa fenêtre jusqu'au prochain relevé, le faisaient passer à « sans effet ».
                try { JournalChangements.Figer(c, bilan); }
                catch (Exception) { /* un journal non écrit garde le retrait ; seul le libellé peut bouger */ }
                _journalLeviers.Retirer(id);
                // Revue du 04/10 (P1) : le service de réapplication le reposerait (manifeste signé) ; il en sort aussi.
                var sorti = await RetirerDuManifesteAsync(id);
                dits.Add(string.Format(CultureInfo.CurrentCulture,
                    _localize(levier.TakesEffectAfterReboot ? "Changement.RetireAuRedemarrage" : "Changement.Retire"), c.Libelle));
                if (!sorti.IsOk)
                    dits.Add(string.Format(CultureInfo.CurrentCulture, _localize("Changement.PasSortiDuManifeste"),
                        c.Libelle, sorti.Error?.Message));
            }
        }
        finally { _retraitEnCours = false; }
        if (dits.Count > 0) ChargerLeviersPoses();
        return dits;
    }

    /// <summary>Sort un levier du manifeste que le service relit au démarrage. Échec : le retrait reste fait sur la
    /// machine, et la carte le dit (l'entrée restée pourrait être reposée après une mise à jour de Windows).</summary>
    internal Func<string, Task<Result>>? RetirerDuManifeste { get; set; }

    private Task<Result> RetirerDuManifesteAsync(string id) => RetirerDuManifeste?.Invoke(id) ?? Task.FromResult(Result.Ok());

    /// <summary>Une ligne par changement : jugé en jeu avec ses chiffres, ou combien de parties il manque.</summary>
    /// <summary>04/10/2026 (audit 5.2) : les changements sans partie d'avant tiennent en une ligne, à la place du premier ;
    /// les autres gardent leur phrase et leur ordre.</summary>
    /// <summary>
    /// 06/10/2026 (chantier J2) : `GainCumule` n'était appelé que par un outil de développement. La carte des parties
    /// commence par ce que les changements ont fait en jeu : les gains (et pertes) prouvés par séparation nette sur trois
    /// parties de chaque côté, composés par <see cref="DrDxt.Measurement.GainCumule"/> (jamais additionnés), ce qui n'a rien
    /// changé, et ce qui attend des parties. Le bruit vaut 0 : la séparation nette l'a déjà écarté. null = aucun changement.
    /// </summary>
    internal static string? LigneDesGains(IReadOnlyList<(Changement C, BilanDuChangement B)> changements, Func<string, string> localiser)
    {
        if (changements.Count == 0) return null;
        var fr = CultureInfo.CurrentCulture;
        // Revue F-3 : le bruit d'un gain = l'écart entre la médiane et le pire cas des bornes (3 contre 3 se séparent
        // par hasard une fois sur 20) ; le total en hérite, et la phrase dit « au moins ».
        var prouves = changements
            .Where(x => x.B.Verdict is VerdictEnJeu.Mieux or VerdictEnJeu.Pire && x.B.Low1Avant is > 0 && x.B.Low1Apres is not null
                        && x.B.EcartBornesPercent is not null)
            // 07/10/2026 (J5) : des changements du même instant jugés ensemble n'ont qu'une mesure, le même bilan ; composée
            // une fois par changement, elle comptait trois fois la perte d'un seul clic d'Optimiser.
            // Revue F-24 : par instant seulement. Le frère non retiré d'un même clic se rejuge après le retrait de
            // l'autre (figé) : deux bilans, une seule mesure. Le bilan figé fait foi.
            // Limite : l'instant est arrondi à la minute ; deux changements de part et d'autre d'une minute ronde, à quelques
            // ms l'un de l'autre, se comptent séparément.
            // Revue F-25 : un même clic se compte une fois, même quand le mode Jeu se date à la pose et HAGS au
            // redémarrage.
            .GroupBy(x => (DateeSurLaPose(x.C) ? x.C.Depuis!.Value : x.C.Quand).UtcTicks / MemeInstant.Ticks)
            .SelectMany(g => g.Any(y => y.C.Fige is not null) ? [g.ToList()] : g.GroupBy(y => y.B).Select(b => b.ToList()))
            .Select(g =>
            {
                var x = g.FirstOrDefault(y => y.C.Fige is not null, g.First());
                var ecart = (x.B.Low1Apres!.Value / x.B.Low1Avant!.Value - 1) * 100;
                return new DrDxt.Measurement.GainProuve(string.Join(" + ", g.Select(y => y.C.Libelle)),
                    $"1 % low médian en partie ({x.B.Jeu})", ecart, Math.Abs(ecart - x.B.EcartBornesPercent!.Value));
            })
            .ToList();
        var total = DrDxt.Measurement.GainCumule.Additionner(prouves);
        var auMoins = total.TotalPercent > 0 ? total.TotalPercent - total.BruitPercent : total.TotalPercent + total.BruitPercent;
        var gains = total.Retenus.Count == 0
            ? localiser("Parties.Gains.Aucun")
            : string.Format(fr, localiser("Parties.Gains.Total"), total.TotalPercent, total.Retenus.Count, auMoins);
        var ligne = string.Format(fr, localiser("Parties.Gains"), gains,
            changements.Count(x => x.B.Verdict == VerdictEnJeu.PasDeDifference),
            changements.Count(x => x.B.Verdict == VerdictEnJeu.PasEncoreJuge));
        // F-4 : un gain prouvé sur un autre jeu ne s'additionne pas (autre instrument) : il est compté, pas tu.
        var autreJeu = prouves.Count - total.Retenus.Count;
        return Pluriels.Accorder(autreJeu > 0 ? ligne + " " + string.Format(fr, localiser("Parties.Gains.AutreJeu"), autreJeu) : ligne);
    }

    /// <summary>
    /// 06/10/2026 (revue F-1, F-2) : chaque changement jugé sur les parties d'entre lui et le changement suivant (sinon
    /// l'après de l'un portait le gain de l'autre), et son bilan figé une fois tranché (le relevé ne garde que 20 parties).
    /// </summary>
    /// <summary>Deux changements plus proches que ça sont du même instant (le journal dédoublonne déjà à la minute).</summary>
    internal static readonly TimeSpan MemeInstant = TimeSpan.FromMinutes(1);

    internal static IReadOnlyList<(Changement C, BilanDuChangement B)> BilansDesChangements(IReadOnlyList<Changement> changements,
        IReadOnlyList<PartieRelevee> parties, JournalDesChangements? journal, IReadOnlyList<DateTimeOffset>? misesAJour = null)
    {
        var machine = misesAJour ?? [];
        var parDate = changements.Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true).OrderBy(c => c.Quand).ToList();
        var bilans = new List<(Changement, BilanDuChangement)>();
        for (var i = 0; i < parDate.Count; i++)
        {
            var c = parDate[i];
            // 07/10/2026 (J5, histoire rejouée) : les réglages d'un même clic d'Optimiser basculent au même redémarrage. Bornés
            // par leur voisin du même instant, chacun gardait zéro partie d'un côté : aucun ne se jugeait, rien ne se retirait.
            // Ils se jugent ensemble, entre les changements d'un autre instant.
            // Revue F-22 : « au démarrage du PC » se recalcule à chaque lecture (quelques ms d'écart, et un autre
            // processus) ; deux changements à moins de <see cref="MemeInstant"/> l'un de l'autre sont du même instant.
            // Revue F-25 : le mode Jeu, relevé à la pose, et HAGS, au redémarrage, viennent pourtant du même clic. Ils
            // ne se bornent pas l'un l'autre.
            var autres = parDate.Where(x => !DuMemeClic(x, c)).ToList();
            var suivant = autres.Select(x => x.Quand).Concat(machine).Where(q => q > c.Quand + MemeInstant).Order()
                .Select(q => (DateTimeOffset?)q).FirstOrDefault();
            var precedent = autres.Select(x => x.Quand).Concat(machine).Where(q => q < c.Quand - MemeInstant).Order()
                .Select(q => (DateTimeOffset?)q).LastOrDefault();
            var close = suivant is not null;
            var bilan = c.Fige ?? ComparaisonDesParties.Bilan(parties, c.Quand, c.Depuis, suivant, precedent);
            // 07/10/2026 (revue F-8) : figé seulement quand plus rien ne peut le corriger — la fenêtre est close, ou le
            // relevé est plein et va perdre des parties d'avant. Figé au premier 3 contre 3, un faux verdict restait pour toujours.
            // F-19 : « plein » est l'état permanent après 20 parties ; ce qui compte est que la prochaine partie à sortir (la
            // plus ancienne) soit une partie d'avant ce changement.
            var plusAncienne = parties.Count >= ReleveDesParties.PartiesGardees ? parties.Min(p => p.Debut) : (DateTimeOffset?)null;
            var perdAvant = plusAncienne is { } a && a < c.Quand && (precedent is not { } p || a >= p);
            if (c.Fige is null && bilan.Verdict != VerdictEnJeu.PasEncoreJuge && (close || perdAvant))
            {
                try { journal?.Figer(c, bilan); }
                catch (Exception) { /* un journal non écrit se rejuge au prochain affichage */ }
            }
            bilans.Add((c, bilan));
        }
        // L'écran lit le plus récent d'abord, comme le journal.
        bilans.Reverse();
        return bilans;
    }

    /// <summary>08/10/2026 (critique UI, défaut 8) : seuls les changements JUGÉS restent une phrase ; ceux qui ne le sont pas
    /// encore (ou ne le seront jamais) passent dans le tableau <see cref="TableauDesChangementsEnAttente"/>.</summary>
    internal static IReadOnlyList<string> LignesDesChangements(IReadOnlyList<(Changement C, BilanDuChangement B)> changements, Func<string, string> localiser) =>
        changements.Where(x => !EnAttente(x.B)).Select(x => LigneChangement(x.C, x.B, localiser)).ToList();

    private static bool EnAttente(BilanDuChangement b) => b.Avant == 0 || b.Verdict == VerdictEnJeu.PasEncoreJuge || b.Jeu is null;

    /// <summary>08/10/2026 (critique UI, défaut 8) : « Pas jugeables en jeu, faute de partie relevée avant eux : … » faisait
    /// un paragraphe de quatre lignes. Un tableau : le changement, sa date, et où il en est. Les parties d'avant ne
    /// peuvent plus augmenter : sans assez de parties du même jeu avant, il n'est pas jugé (une partie d'un autre jeu peut le juger plus tard), et le tableau le dit.</summary>
    internal static IReadOnlyList<LigneEnAttente> TableauDesChangementsEnAttente(IReadOnlyList<(Changement C, BilanDuChangement B)> changements,
        Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var min = ComparaisonDesParties.PartiesMinimum;
        return changements.Where(x => EnAttente(x.B)).Select(x =>
        {
            var b = x.B;
            var etat = b.Avant == 0 ? localiser("Changement.Tableau.SansAvant")
                : b.Avant < min ? string.Format(fr, localiser("Changement.Tableau.PeuAvant"), b.Avant, min)
                : Pluriels.Accorder(string.Format(fr, localiser("Changement.Tableau.Attend"), Math.Max(1, min - b.Apres)));
            return new LigneEnAttente(x.C.Libelle, x.C.Quand.LocalDateTime.ToString("dd/MM", fr), etat);
        }).ToList();
    }

    internal static string LigneChangement(Changement c, BilanDuChangement b, Func<string, string> localiser)
    {
        var fr = CultureInfo.CurrentCulture;
        var date = c.Quand.LocalDateTime.ToString("dd/MM", fr);
        // Aucune partie d'avant : les parties à venir n'en ajouteront jamais, ce changement ne se jugera pas en jeu.
        if (b.Avant == 0)
            return string.Format(fr, localiser("Changement.SansAvant"), c.Libelle, date);
        if (b.Verdict == VerdictEnJeu.PasEncoreJuge || b.Jeu is null)
            return Pluriels.Accorder(string.Format(fr, localiser("Changement.PasEncore"), c.Libelle, date,
                b.Apres, b.Avant, ComparaisonDesParties.PartiesMinimum));
        var verdict = localiser(b.Verdict switch
        {
            VerdictEnJeu.Mieux => "Changement.Mieux",
            VerdictEnJeu.Pire => "Changement.Pire",
            _ => "Changement.Pareil",
        });
        string N(double? v) => v is { } x ? x.ToString("0", fr) : "?";
        return string.Format(fr, localiser("Changement.Juge"), c.Libelle, date, verdict,
            NomDuJeu(b.Jeu), N(b.Low1Avant), N(b.Low1Apres), N(b.FpsAvant), N(b.FpsApres), b.Avant, b.Apres);
    }

    private async Task RetirerEtDireAsync(IReadOnlyList<PartieRelevee> parties)
    {
        try
        {
            var dits = await RetirerCeQuiCouteAsync(parties);
            foreach (var phrase in dits)
            {
                _ditsDuRetrait.Add(phrase);
                LignesParties.Insert(0, phrase);
            }
            if (dits.Count > 0) AnnoncerResumeEtDetail();
            // 07/10/2026 (suite de F-25) : un réglage à effet immédiat rendu se relève tout de suite, comme à la pose ; sinon
            // son retour n'était daté qu'au scan suivant.
            if (dits.Count > 0) await ReleverReglagesHorsScanAsync(ReglagesAEffetImmediat).ConfigureAwait(true);
        }
        catch (Exception) { /* un retrait raté laisse le verdict à l'écran ; le bouton Annuler reste */ }
    }

    /// <summary>07/10/2026 (revue F-10) : une capture ratée plus récente que la dernière partie se dit en tête de la
    /// carte ; sinon le relevé de fond pouvait rester « allumé » des semaines sans rien relever. UI1 (08/10/2026) : sans
    /// son code d'erreur, qui reste dans le fichier de l'échec.</summary>
    internal static string? LigneEchec(PartiesMemorisees.EchecDeCapture? echec, IReadOnlyList<PartieRelevee> parties,
        Func<string, string> localiser)
    {
        if (echec is null || parties.Any(p => p.Fin >= echec.Quand)) return null;
        var fr = CultureInfo.CurrentCulture;
        return string.Format(fr, localiser("Parties.CaptureRatee"), echec.Quand.LocalDateTime.ToString("dd/MM HH:mm", fr));
    }

    /// <summary>07/10/2026 (J5b) : ce que le retrait a fait pendant cette session. La carte se refait à chaque partie et à
    /// chaque relevé : sans cette liste, la phrase « retiré par DrDXT » disparaissait au premier rafraîchissement.</summary>
    private readonly List<string> _ditsDuRetrait = [];

    private void Afficher(IReadOnlyList<PartieRelevee> parties)
    {
        LignesParties.Clear();
        foreach (var dit in _ditsDuRetrait) LignesParties.Insert(0, dit);
        ChangementsEnAttente.Clear();   // 08/10/2026 (revue) : un journal illisible ne laisse pas l'ancien tableau
        try
        {
            var tous = BilansBornes(JournalChangements?.Lire() ?? [], parties);
            // J2 : ce que l'ensemble des changements a fait en jeu, en tête de la carte.
            if (LigneDesGains(tous, _localize) is { } gains) LignesParties.Add(gains);
            foreach (var ligne in LignesDesChangements(tous.Take(3).ToList(), _localize)) LignesParties.Add(ligne);
            foreach (var l in TableauDesChangementsEnAttente(tous.Take(3).ToList(), _localize)) ChangementsEnAttente.Add(l);
        }
        catch (Exception) { /* un journal illisible ne cache pas les parties */ }
        OnPropertyChanged(nameof(ADesChangementsEnAttente));
        // Un affichage pendant un retrait en cours ne le cache pas : on attend les deux.
        DernierRetrait = Task.WhenAll(DernierRetrait, RetirerEtDireAsync(parties));
        _ = AfficherHistoriqueAsync(parties);
        // 04/10/2026 (plan 3b.4) : le profil de chaque jeu assez joué, avant ses parties.
        foreach (var profil in ProfilDesJeux.Construire(parties)) LignesParties.Add(LigneProfil(profil, _localize));
        var avantLeDetail = LignesParties.Count;
        try
        {
            var changements = (JournalChangements?.Lire() ?? []).Where(c => c.Cle?.StartsWith("pose:", StringComparison.Ordinal) != true)
                .Select(c => c.Quand).Concat(InstantsDesMisesAJour() ?? []);
            foreach (var ligne in LignesOverlays(parties, _localize, changements)) LignesParties.Add(ligne);
            if (LigneRoute(parties, _localize) is { } route) LignesParties.Add(route);
            if (LigneMecanismeCarte(parties, _localize) is { } mecanisme) LignesParties.Add(mecanisme);
        }
        catch (Exception) { /* un journal illisible ne cache pas les parties */ }
        foreach (var p in parties) LignesParties.Add(LignePartie(p, _localize));
        _lignesDeDetail = LignesParties.Count - avantLeDetail;
        if (LigneEchec(MemoireParties?.DernierEchec(), parties, _localize) is { } echec) LignesParties.Insert(0, echec);
        _partiesAffichees = parties.Count;
        ProposerDepuis(parties);
        OnPropertyChanged(nameof(ADesParties));
        OnPropertyChanged(nameof(AucunePartie));
        AnnoncerResumeEtDetail();
        // 30/09/2026 (G4) : chaque partie relevée juge aussi le réglage de la carte gardé au repos, s'il y en a un.
        _ = SurveillerSiPoseAsync(parties);
    }

    private readonly DrDxt.Detection.Cpu.CpuFrequencyReader _frequenceCpu = new();
    private DrDxt.Detection.NvmlHardwareProbe? _sondeCarte;
    private IReadOnlyList<int>? _coeursRapides;

    /// <summary>06/10/2026 (J1b) : relevé allumé, DrDXT se lance aussi à l'ouverture de session pour relever DrDXT fermé ;
    /// éteint, il s'en retire. <c>null</c> dans les tests et dans le relevé de fond lui-même.</summary>
    internal Action<bool>? InscrireAuDemarrage { get; set; }

    /// <summary>06/10/2026 (J1b) : un seul relevé à la fois entre l'App ouverte et celui de fond (<see cref="VerrouDuReleve"/>).
    /// <c>null</c> = pas de verrou (tests).</summary>
    internal Func<IDisposable?>? PrendreVerrouReleve { get; set; }

    /// <summary>Une partie finie par la veille : gardée sur le disque, puis la carte se refait (jugement des changements et
    /// retrait de ce qui coûte compris).</summary>
    internal void EnregistrerPartie(PartieRelevee p, SynchronizationContext? ui = null)
    {
        // Le banc de l'époque voyage avec la partie : comparée plus tard à un banc pris après un changement de
        // BIOS, elle se dirait ralentie à tort (revue du 04/10).
        try { p = p with { BancMhz = _machineId is { } id ? _cpuBaseline?.Reference(id())?.FrequenceSoutenueMhz : null }; }
        catch (Exception) { }
        // 07/10/2026 (J4) : la partie porte l'état de l'essai en alternance qui l'a vue jouer.
        p = MarquerEssai(p);
        var liste = MemoireParties?.Ajouter(p) ?? [p];
        if (ui is null) Afficher(liste); else ui.Post(_ => Afficher(liste), null);
        // Revue F-30 : sur le fil de l'écran, comme l'affichage (poser un levier rafraîchit des listes liées à l'écran).
        if (ui is null) DerniereAvanceeEssai = AvancerEssaiAsync(liste);
        else ui.Post(_ => DerniereAvanceeEssai = AvancerEssaiAsync(liste), null);
    }

    // 07/10/2026 : la veille dure toute la session ; sans exécutions simultanées le bouton restait grisé et le relevé ne
    // s'arrêtait pas depuis l'écran.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task BasculerReleveDesParties()
    {
        // 08/10/2026 (revue) : l'interrupteur se coche tout seul au clic ; une erreur avant le changement d'état le laissait faux.
        try { await BasculerReleveCoeurAsync(); }
        finally { OnPropertyChanged(nameof(ReleveDesPartiesActif)); }
    }

    private async Task BasculerReleveCoeurAsync()
    {
        if (ReleveDesPartiesActif)
        {
            _releveDesParties?.Cancel();
            ReleveDesPartiesActif = false;
            MemoireParties?.Eteindre();
            InscrireAuDemarrage?.Invoke(false);
            return;
        }

        _releveDesParties = new CancellationTokenSource();
        ReleveDesPartiesActif = true;
        MemoireParties?.Allumer();
        InscrireAuDemarrage?.Invoke(true);
        var ui = SynchronizationContext.Current;
        var veille = new VeilleDesParties(
            // Revue F-5 : allumé par défaut, le relevé enregistrait toute fenêtre plein écran (YouTube en F11, VLC),
            // qui chassait de vraies parties de l'anneau de 20. Le même filtre que le plafond réseau : ni navigateur, ni
            // lecteur, ni programme de Windows.
            jeu: () => _gameLocator.LocateRunningGamePid() is { } pid && pid > 0 && NomExeDuProcessus(pid) is { } exe
                       && DrDxt.Core.NomExeDeJeu.EstUnJeuPossible(exe) ? (exe, pid) : null,
            capturer: async (pid, duree, ct) =>
            {
                var r = await _benchmark.RunAsync(pid, duree, 1, ct);
                // 07/10/2026 (F-10) : la raison de l'échec remonte à la veille, qui la note.
                return r.IsOk ? r.Value : throw new InvalidOperationException(r.Error?.Message ?? "capture sans résultat");
            },
            classer: r => DrDxt.Detection.Scoring.BottleneckClassifier.Classify(r).State,
            enregistrer: p => EnregistrerPartie(p, ui),
            patienter: Task.Delay,
            // 04/10/2026 (plan 1.1) : mêmes cœurs et même compteur que le banc ; le plus rapide, pas la moyenne (voir FrequenceMaxDe).
            processeur: () => (_frequenceCpu.FrequenceMaxDe(_coeursRapides ??= DrDxt.Detection.Cpu.CpuTopology.CoeursRapides(new DrDxt.Detection.Cpu.CpuTopology().Lire())),
                DrDxt.Detection.Cpu.PuissancePaquetReader.LireWatts(new DrDxt.Detection.Wmi.ManagementWmiQuery()),
                DrDxt.Detection.Cpu.TemperatureProcesseur.Lire()?.Celsius),
            memoireVideo: DrDxt.Detection.Gpu.MemoireVideoOccupee.Lire,
            // 04/10/2026 : les programmes à côté du jeu, lus sur 600 ms juste après la capture.
            voisins: pid => _voisins.LireParNom(pid, CancellationToken.None)?.Select(v => (v.Nom, v.ProcesseurPourcent)).ToList(),
            signalerEchec: raison => MemoireParties?.NoterEchec(new PartiesMemorisees.EchecDeCapture(Horloge(), raison)),
            // 08/10/2026 (D1) : la marque « partie en cours » ; restée ouverte, elle dit qu'un arrêt est tombé en partie.
            suivre: p => MemoireParties?.NoterEnCours(p),
            // 08/10/2026 (J-MECA) : la carte NVIDIA lue par NVML pendant chaque capture (bridage par la puissance, fréquence).
            lireCarte: () => (_sondeCarte ??= new DrDxt.Detection.NvmlHardwareProbe()).Lire(),
            horloge: Horloge,
            // 07/10/2026 (A5) : la carte, quelle que soit sa marque, par la bibliothèque de son pilote ; sans HWiNFO.
            carte: () => DrDxt.Detection.Gpu.TemperatureCarte.Lire().Celsius,
            overlays: () => OverlaysEnPartie.Reconnaitre(System.Diagnostics.Process.GetProcesses().Select(p => { using (p) return p.ProcessName; })),
            reseau: DrDxt.App.Platform.MesureAvantPartie.ReseauAsync,
            carteDuJeu: pid => DrDxt.Detection.Gpu.CarteDuJeu.Lire(pid) is { } c ? (c.Carte, c.Integree ? c.CarteDediee : null) : null);
        var jeton = _releveDesParties.Token;
        try
        {
            await Task.Run(() => PrendreVerrouReleve is { } prendre
                ? VerrouDuReleve.VeillerSeulAsync(prendre, Veiller, Task.Delay, jeton)
                : Veiller(jeton));
            // Revue du 08/10/2026 (D1) : la marque restée ouverte ne se relève qu'avec le verrou du relevé, jamais sous un
            // relevé de fond qui suit encore la partie.
            Task Veiller(CancellationToken ct)
            {
                MemoireParties?.ReleverInterrompue(Horloge());
                return veille.VeillerAsync(ct);
            }
        }
        catch (OperationCanceledException) { }
    }
}
