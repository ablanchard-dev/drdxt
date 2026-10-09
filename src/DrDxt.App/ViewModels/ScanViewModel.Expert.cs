using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Tweaks.Gpu;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

// GPU-OC (08/10/2026) : le mode Expert de la carte, sur la page Graphique. Décision du mainteneur du 08/10 : fréquence et
// tension deviennent des réglages Expert, sur l'accord explicite du joueur, jamais dans « Optimiser ». Le décalage part
// seulement avec l'accord coché et aucun jeu ouvert ; gardé, il vit tant que DrDXT est ouvert (le bail du helper rend
// l'origine à l'arrêt ou au plantage) ; « Rendre » remet l'origine tout de suite.
public partial class ScanViewModel
{
    /// <summary>SEC3 : l'état que le service écrit à chaque passage (lecture seule), branché par l'App.</summary>
    internal Func<DrDxt.Reconciler.ReconcilerState?>? LireEtatDuService { get; set; }

    /// <summary>ROAD1 : le catalogue des leviers, branché par l'App ; null = pas de liste « ce que DrDXT fait ici ».</summary>
    internal Func<IReadOnlyList<DrDxt.Core.ITweak>>? CatalogueDesLeviers { get; set; }

    /// <summary>ROAD1 : les leviers vérifiés sur ce PC par « Vérifier DrDXT », branché par l'App.</summary>
    internal Func<HardwareInventory, IReadOnlySet<string>>? LeviersVerifiesIci { get; set; }
    public string LabelCarteLeviers => _localize("Carte.Leviers.Titre");

    /// <summary>La pose (ReglageCarteExpert), branchée par l'App. null = section absente.</summary>
    internal Func<int, Task<Result<IEssaiCarte>>>? PoserCarteExpert
    {
        get => _poserCarteExpert;
        set { _poserCarteExpert = value; OnPropertyChanged(nameof(ExpertVisible)); }
    }
    private Func<int, Task<Result<IEssaiCarte>>>? _poserCarteExpert;

    private IEssaiCarte? _essaiCarte;

    /// <summary>GPU-OC (08/10/2026) : la vitesse fixe du ventilateur (VentilateurExpert), branchée par l'App ; null = absente.
    /// Elle partage l'accord, l'état et « Rendre » de la fréquence : un seul essai gardé à la fois, comme le journal.</summary>
    internal Func<int, Task<Result<IEssaiCarte>>>? PoserVentilateurExpert
    {
        get => _poserVentilateurExpert;
        set { _poserVentilateurExpert = value; OnPropertyChanged(nameof(ExpertVentilateurVisible)); }
    }
    private Func<int, Task<Result<IEssaiCarte>>>? _poserVentilateurExpert;
    [ObservableProperty] private int _expertVentilateurPct = 70;
    public bool ExpertVentilateurVisible => PoserVentilateurExpert is not null && _lastScan is { } s && VentilateurExpert.Possible(s.Hw);
    public int ExpertVentilateurMin => VentilateurExpert.MinPourcent;
    public int ExpertVentilateurMax => VentilateurExpert.MaxPourcent;
    public string LabelExpertVentilateur => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        _localize("Carte.Expert.Ventilateur"), ExpertVentilateurPct);
    public string LabelExpertVentilateurAppliquer => _localize("Carte.Expert.Ventilateur.Appliquer");
    partial void OnExpertVentilateurPctChanged(int value) => OnPropertyChanged(nameof(LabelExpertVentilateur));

    [RelayCommand]
    private Task PoserVentilateurExpertAsync()
        => PoserVentilateurExpert is { } poser ? PoserEssaiAsync(() => poser(ExpertVentilateurPct)) : Task.CompletedTask;

    [ObservableProperty] private bool _expertAccord;
    [ObservableProperty] private int _expertDecalageMhz;
    [ObservableProperty] private string? _expertEtat;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ExpertPeutPoser))] private bool _expertEnCours;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ExpertPeutPoser))] private bool _expertGarde;

    public bool ExpertVisible => PoserCarteExpert is not null && _lastScan is { } scan && ReglageCarteExpert.Possible(scan.Hw);
    public bool ExpertPeutPoser => !ExpertEnCours && !ExpertGarde;
    public int ExpertMinMhz => GpuSafeBounds.MinCoreOffsetMhz;
    public int ExpertMaxMhz => GpuSafeBounds.MaxCoreOffsetMhz;

    public string LabelExpertTitre => _localize("Carte.Expert.Titre");
    // AMD-TOUT (09/10/2026) : sur une carte sans validation archivée (toute Radeon à ce jour), l'Expert reste ouvert mais
    // l'accord le dit : l'écriture n'a jamais été essayée sur cette architecture.
    public string LabelExpertAccord => _lastScan is { } s && GpuTuningCoverage.For(s.Hw).Any(r => r.Support == GpuTuningSupport.ReadOnly)
        ? $"{_localize("Carte.Expert.Accord")} {_localize("Carte.Expert.NonValideIci")}"
        : _localize("Carte.Expert.Accord");
    public string LabelExpertDecalage => string.Format(System.Globalization.CultureInfo.CurrentCulture,
        _localize("Carte.Expert.Decalage"), ExpertDecalageMhz);
    public string LabelExpertAppliquer => _localize("Carte.Expert.Appliquer");
    public string LabelExpertRendre => _localize("Carte.Expert.Rendre");
    partial void OnExpertDecalageMhzChanged(int value) => OnPropertyChanged(nameof(LabelExpertDecalage));

    [RelayCommand]
    private Task PoserCarteExpertAsync()
        => PoserCarteExpert is { } poser ? PoserEssaiAsync(() => poser(ExpertDecalageMhz)) : Task.CompletedTask;

    private async Task PoserEssaiAsync(Func<Task<Result<IEssaiCarte>>> poser)
    {
        if (ExpertEnCours || ExpertGarde) return;
        if (!ExpertAccord) { ExpertEtat = _localize("Carte.Expert.AccordRequis"); return; }
        if (_gameLocator.JeuOuvert()) { ExpertEtat = _localize("Carte.Expert.JeuEnCours"); return; }

        ExpertEnCours = true;
        ExpertEtat = _localize("Carte.Expert.EnCours");
        try
        {
            var r = await poser();
            if (r.IsOk)
            {
                _essaiCarte = r.Value;
                _essaiCarte!.BailPerdu += BailDeLaCartePerdu;
                ExpertGarde = true;
                ExpertEtat = _localize("Carte.Expert.Garde");
            }
            else
            {
                ExpertEtat = r.Error?.Code switch
                {
                    ErrorCodes.TweakUnstable => _localize("Carte.Expert.Instable"),
                    // Revue n° 13 : instable ET retour raté ; jamais « rendue ».
                    ErrorCodes.TweakPartiallyApplied => $"{_localize("Carte.Expert.RenduRate")} {r.Error?.Message}",
                    _ => $"{_localize("Carte.Expert.Refuse")} {r.Error?.Message}",
                };
            }
        }
        finally { ExpertEnCours = false; }
    }

    /// <summary>Le helper a rendu l'origine de lui-même : l'écran cesse de dire « Gardé ».</summary>
    private void BailDeLaCartePerdu()
    {
        _essaiCarte = null;
        ExpertGarde = false;
        ExpertEtat = _localize("Carte.Expert.RenduParLeHelper");
    }

    /// <summary>À la fermeture de l'App : l'essai gardé est rendu tout de suite, et son journal levé.</summary>
    internal async Task FermerCarteExpertAsync()
    {
        if (_essaiCarte is not { } essai) return;
        _essaiCarte = null;
        // Revue du 08/10 : le battement est libéré même si le rendu rate ; le bail expire alors et le helper rend seul.
        try { await essai.RendreAsync().ConfigureAwait(false); }
        finally { await essai.DisposeAsync().ConfigureAwait(false); }
    }

    [RelayCommand]
    private async Task RendreCarteExpertAsync()
    {
        if (_essaiCarte is not { } essai || ExpertEnCours) return;
        ExpertEnCours = true;
        try
        {
            var r = await essai.RendreAsync();
            if (r.IsOk)
            {
                essai.BailPerdu -= BailDeLaCartePerdu;
                await essai.DisposeAsync();
                _essaiCarte = null;
                ExpertGarde = false;
                ExpertEtat = _localize("Carte.Expert.Rendu");
            }
            else
            {
                ExpertEtat = $"{_localize("Carte.Expert.RenduRate")} {r.Error?.Message}";
            }
        }
        finally { ExpertEnCours = false; }
    }
}
