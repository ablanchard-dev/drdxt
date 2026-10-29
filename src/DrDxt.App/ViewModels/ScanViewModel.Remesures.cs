using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>La carte Agir : remesurer le processeur et la mémoire, à la main ou au repos, et la limite de puissance.</summary>
public sealed partial class ScanViewModel
{
    public bool RemesureCpuVisible => _cpuBaseline is not null && _machineId is not null
        && (CpuSustainedLabel is not null || CpuSauteJeu);

    // Le scan capture le jeu, donc le parcours normal le lance avant de scanner. Sans mesure
    // jeu lancé, ce joueur n'avait ni ligne, ni raison, ni bouton : la carte s'affiche et dit pourquoi.
    [ObservableProperty] private bool _cpuSauteJeu;
    partial void OnCpuSauteJeuChanged(bool value) => OnPropertyChanged(nameof(RemesureCpuVisible));
    // Une information, pas une erreur : rien n'est à corriger sur la machine, elle a sa ligne neutre.
    public string LabelCpuSauteJeu => _localize("Cpu.Remesure.SauteJeu");
    public string LabelRemesureCpuTitre => _localize("Cpu.Remesure.Titre");
    public string LabelRemesureCpuDescription => _localize("Cpu.Remesure.Description");
    // La ligne qui reste à l'écran ; le texte entier passe en info-bulle.
    public string LabelRemesureCpuDescriptionCourte => _localize("Cpu.Remesure.Description.Court");
    public string LabelRemesureCpuMaintenir => _localize("Cpu.Remesure.Maintenir");
    public string LabelRemesureCpuEnCours => _localize("Cpu.Remesure.EnCours");
    public string LabelRemesureCpuFaite => _localize("Cpu.Remesure.Faite");

    // La phrase change avec chaque mesure : le guide de la limite de puissance la suit.
    partial void OnCpuSustainedLabelChanged(string? value)
    {
        OnPropertyChanged(nameof(RemesureCpuVisible));
        OnPropertyChanged(nameof(LignesLimiteDePuissance));
        OnPropertyChanged(nameof(ALimiteDePuissance));
        OnPropertyChanged(nameof(LimitePremiereLigne));
        OnPropertyChanged(nameof(LignesLimiteSuite));
    }

    /// <summary>La phrase du processeur, reliee au reglage BIOS lu. La composition vit
    /// dans <see cref="DrDxt.Measurement.Cpu.CpuDeficitSoutenu.PhraseAvec"/>, partagee avec le rapport.</summary>
    internal static string? PhraseProcesseur(DrDxt.Measurement.Cpu.CpuDeficitSoutenu? deficit, IReadOnlyList<BiosSettingReading>? bios)
        => deficit?.PhraseAvec(bios);

    /// <summary>En partie, le jeu est limité par le processeur, bridé
    /// par sa limite de puissance soutenue ; cachée sous « Pourquoi ? », l'explication passerait inaperçue. Quatre lignes en clair :
    /// ce qui bride (mesuré), le réglage lu, le risque, le retour. <c>null</c> sans limite démontrée ou sans BIOS lu.
    /// DrDXT n'écrit pas le BIOS et ne conseille pas de changer ce réglage.</summary>
    internal static IReadOnlyList<string>? GuideLimiteDePuissance(
        DrDxt.Measurement.Cpu.CpuDeficitSoutenu? deficit, IReadOnlyList<BiosSettingReading>? bios, Func<string, string> localiser)
    {
        var reglage = bios?.FirstOrDefault(r => r.Setting == "CPU Cooler Tuning");
        // La bride se mesure sur toute machine ; seul le réglage n'est lu que sur les cartes que
        // DrDXT connaît. Ailleurs, le guide reste, avec le nom générique du réglage, au lieu de se taire.
        if (deficit is not ({ Demontrable: true, LimiteDePuissance: true } or { BrideDesLeDepart: true })) return null;
        var watts = deficit.Reference?.PuissanceSoutenueW;
        string F(string cle, params object[] a) => string.Format(System.Globalization.CultureInfo.CurrentCulture, localiser(cle), a);
        return
        [
            deficit.BrideDesLeDepart && deficit.Reference is { PuissanceSoutenueW: { } pw, FrequenceSoutenueMhz: { } fs, FrequenceUnCoeurMhz: { } f1 }
                ? F("Cpu.Limite.DesLeDepart", Math.Round(pw), Math.Round(fs), Math.Round(f1))
            : watts is { } w
                ? F("Cpu.Limite.Mesure", Math.Round(w), Math.Round(deficit.DeficitPercent))
                : F("Cpu.Limite.MesureSansWatts", Math.Round(deficit.DeficitPercent)),
            reglage is null ? localiser("Cpu.Limite.ReglageInconnu") : F("Cpu.Limite.Reglage", reglage.Libelle),
            localiser(reglage is null ? "Cpu.Limite.RisqueGenerique" : "Cpu.Limite.Risque"),
            localiser(reglage is null ? "Cpu.Limite.RetourGenerique" : "Cpu.Limite.Retour"),
        ];
    }

    /// <summary>Les réglages du BIOS qui fixent la puissance du processeur, tels que lus, en une
    /// chaîne notée avec chaque mesure. <c>null</c> sans aucun d'eux lu : rien à comparer, aucune remesure.</summary>
    internal static string? ReglageDePuissanceLu(IReadOnlyList<BiosSettingReading>? bios)
    {
        if (bios is null) return null;
        string? L(string reglage) => bios.FirstOrDefault(r => r.Setting == reglage)?.Libelle;
        var parties = new[]
        {
            L("CPU Cooler Tuning"),
            L("Long Duration Power Limit (PL1)") is { } pl1 ? $"PL1 {pl1}" : null,
            L("Short Duration Power Limit (PL2)") is { } pl2 ? $"PL2 {pl2}" : null,
        }.OfType<string>().ToList();
        return parties.Count == 0 ? null : string.Join(", ", parties);
    }

    /// <summary>Le guide, pour la vue.</summary>
    public IReadOnlyList<string>? LignesLimiteDePuissance => GuideLimiteDePuissance(_dernierDeficitCpu, _reglagesBiosLus, _localize);
    public bool ALimiteDePuissance => LignesLimiteDePuissance is not null;
    /// <summary>La bride en une phrase ; le réglage, le risque et le retour arrière se déplient.</summary>
    public string? LimitePremiereLigne => LignesLimiteDePuissance?.FirstOrDefault();
    public IReadOnlyList<string> LignesLimiteSuite => LignesLimiteDePuissance?.Skip(1).ToList() ?? [];
    public string LabelLimiteTitre => _localize("Cpu.Limite.Titre");

    /// <summary>Le dernier verdict processeur, typé, pour le rapport (jamais une chaine libre).</summary>
    private DrDxt.Measurement.Cpu.CpuDeficitSoutenu? _dernierDeficitCpu;

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RemesurerProcesseur()
    {
        if (RemesureCpuEnCours || _cpuBaseline is not { } service || _machineId is not { } id) return;
        if (_gameLocator.JeuOuvert())
        {
            ErreurRemesureCpu = _localize("Cpu.Remesure.JeuEnCours");
            return;
        }
        RemesureCpuEnCours = true;
        ErreurRemesureCpu = null;
        try
        {
            // La réserve du scan dit que l'ancienne référence a sans doute été prise pendant une partie.
            var suspecte = !string.IsNullOrEmpty(_dernierDeficitCpu?.Reserve);
            var reglage = ReglageDePuissanceLu(_reglagesBiosLus);
            var refAvant = service.Reference(id());
            var deficit = await Task.Run(() => service.Remesurer(id(), suspecte, reglage));
            NoterChangementBios(refAvant?.ReglageBios, service.Reference(id())?.ReglageBios, refAvant?.TakenUtc);
            if (deficit is null) ErreurRemesureCpu = _localize("Cpu.Remesure.Echec");
            else
            {
                _dernierDeficitCpu = deficit;
                CpuSustainedLabel = PhraseProcesseur(deficit, _reglagesBiosLus);
                RemesureCpuFaite = true;
                CpuSauteJeu = false; // la mesure existe maintenant : « pas mesuré » serait faux
                // La carte est construite au scan : sans reconstruction, elle garderait la phrase de l'ancienne
                // mesure (« ne peut pas dire… remesurer le dira ») sous la pastille « mesure refaite ».
                ReconstruireCarteSurLeDomaineOuvert();
            }
        }
        catch (Exception ex) { ErreurRemesureCpu = $"{_localize("Cpu.Remesure.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}"; }
        finally { RemesureCpuEnCours = false; }
    }

    // ── Remesurer sans geste ──────────────────────────────────────────────────────
    // Une reference soupconnee (prise pendant une partie) se refait seule quand le poste est au repos
    // (RemesureAuRepos). Memes commandes qu'a la main, donc memes gardes : jeu ouvert refuse, mesure trop
    // bruitee gardee. Une seule mesure par passage : deux bancs a la suite se generaient.

    /// <summary>Temps sans saisie et charge du processeur. <c>null</c> = aucune remesure seule (les
    /// tests qui ne la verifient pas ; l'appli pose le lecteur Windows au demarrage).</summary>
    internal Func<CancellationToken, Task<(TimeSpan SansSaisie, double? ChargeCpu)>>? LirePoste { get; set; }

    private DateTimeOffset? _dernierEssaiCpuAuRepos;
    private DateTimeOffset? _dernierEssaiMemoireAuRepos;
    private bool _memoireSuspecte;

    public async Task TenterRemesureAuReposAsync(DateTimeOffset maintenant, CancellationToken ct)
    {
        var cpuSuspect = !string.IsNullOrEmpty(_dernierDeficitCpu?.Reserve);
        if (LirePoste is null || (!cpuSuspect && !_memoireSuspecte)
            || OperationLongueEnCours) return;

        var (sansSaisie, charge) = await LirePoste(ct);
        var jeuOuvert = _gameLocator.JeuOuvert();
        DrDxt.Application.Support.DecisionRemesure Decider(bool suspecte, DateTimeOffset? dernier)
            => DrDxt.Application.Support.RemesureAuRepos.Decider(new(sansSaisie, jeuOuvert, charge, suspecte, dernier), maintenant);

        if (Decider(_memoireSuspecte, _dernierEssaiMemoireAuRepos) == DrDxt.Application.Support.DecisionRemesure.Mesurer)
        {
            _dernierEssaiMemoireAuRepos = maintenant;
            await RemesurerMemoireCommand.ExecuteAsync(null);
        }
        else if (Decider(cpuSuspect, _dernierEssaiCpuAuRepos) == DrDxt.Application.Support.DecisionRemesure.Mesurer)
        {
            _dernierEssaiCpuAuRepos = maintenant;
            await RemesurerProcesseurCommand.ExecuteAsync(null);
        }
    }

    // ── Remesurer la mémoire à la demande ──────────────────────────────────────────
    // La référence se mesure au premier scan puis se relit tant que la configuration ne change pas.
    // Mesuré sur la machine de référence : 12,7 Go/s et 128,5 ns le 16/09, 33,4 Go/s et 74 ns dix jours plus tard.
    // Sans ce bouton, l'écran garderait un chiffre faux que rien ne permet de refaire.
    [ObservableProperty] private bool _remesureMemoireEnCours;
    [ObservableProperty] private bool _remesureMemoireFaite;
    [ObservableProperty] private string? _erreurRemesureMemoire;

    public bool RemesureMemoireVisible => _memoryBaseline is not null && _machineId is not null
        && (MemoryMeasuredLabel is not null || MemoireSauteeJeu);

    [ObservableProperty] private bool _memoireSauteeJeu;
    partial void OnMemoireSauteeJeuChanged(bool value) => OnPropertyChanged(nameof(RemesureMemoireVisible));
    public string LabelMemoireSauteeJeu => _localize("Memoire.Remesure.SauteeJeu");
    public string LabelRemesureMemoireTitre => _localize("Memoire.Remesure.Titre");
    public string LabelRemesureMemoireDescription => _localize("Memoire.Remesure.Description");
    public string LabelRemesureMemoireEnCours => _localize("Memoire.Remesure.EnCours");
    public string LabelRemesureMemoireMaintenir => _localize("Cpu.Remesure.Maintenir");
    public string LabelRemesureMemoireFaite => _localize("Memoire.Remesure.Faite");

    partial void OnMemoryMeasuredLabelChanged(string? value) => OnPropertyChanged(nameof(RemesureMemoireVisible));

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RemesurerMemoire()
    {
        if (RemesureMemoireEnCours || _memoryBaseline is not { } service || _machineId is not { } id
            || _lastScan?.Hw.Memory is not { } modules) return;
        // Une mesure prise jeu lancé décrit le jeu, pas la mémoire (la référence du 16/09 en est l'exemple).
        if (_gameLocator.JeuOuvert())
        {
            ErreurRemesureMemoire = _localize("Memoire.Remesure.JeuEnCours");
            return;
        }
        RemesureMemoireEnCours = true;
        ErreurRemesureMemoire = null;
        try
        {
            // Une mesure refusée (machine occupée) ne remplace rien : l'ancienne reste, et c'est dit.
            if (!await Task.Run(() => service.MesurerMaintenant(id(), modules)))
                ErreurRemesureMemoire = _localize("Memoire.Remesure.Echec");
            else
            {
                MemoryMeasuredLabel = LigneMemoire(modules);
                RemesureMemoireFaite = true;
                MemoireSauteeJeu = false;
                ReconstruireCarteSurLeDomaineOuvert();
            }
        }
        catch (Exception ex) { ErreurRemesureMemoire = $"{_localize("Memoire.Remesure.Echec")}{Docteur.DeuxPoints}{RaisonLisible.Dire(ex, _localize)}"; }
        finally { RemesureMemoireEnCours = false; }
    }

    // ── La carte « Agir » du domaine windows : réparer Windows avec les outils de Microsoft ───────
    // Armee seulement quand DISM /CheckHealth a dit « reparable ». La reparation dure de
    // longues minutes : elle ne passe pas par le helper (elle bloquerait ses baux, dont celui de la
    // protection du ping). Elle s'ouvre dans une console visible, elevee par la fenetre UAC de Windows,
    // ou le joueur voit DISM puis sfc avancer et peut fermer quand il veut.
    private bool _reparationARelancer;
    [ObservableProperty] private bool _reparationLancee;
    [ObservableProperty] private string? _erreurReparation;
}
