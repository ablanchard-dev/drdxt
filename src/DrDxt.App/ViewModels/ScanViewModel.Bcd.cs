using System.Globalization;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Core;
using DrDxt.Detection.Hardware;
using Pose = DrDxt.Application.Tweaks.PoseRelue;

namespace DrDxt.App.ViewModels;

/// <summary>La carte Agir : l'écran de récupération coupé, rendu par le helper et annulable.</summary>
public sealed partial class ScanViewModel
{
    // ── La carte « Agir » du domaine windows : rendre l'écran de recuperation ─────────────────
    // L'element et la valeur lus par l'audit. `_bcdRendu` garde exactement ce qui a ete retire :
    // l'annulation repose cette valeur-la, pas une valeur supposee.
    private (string Element, string Valeur)? _bcdARendre;
    private (string Element, string Valeur)? _bcdRendu;

    // La carte promet « Réversible » : la valeur retirée est gardée sur disque jusqu'à l'annulation, écrite avant le
    // helper. Null (tests) : rien n'est écrit et le retrait ne vit que pour la session.
    internal DrDxt.Application.Safety.GardeDuBcdRetire? GardeBcd { get; set; }

    [ObservableProperty] private string? _erreurBcd;
    [ObservableProperty] private bool _bcdEnCours;

    public bool BcdARendre => _bcdARendre is not null && _bcdRendu is null;
    public bool BcdRendu => _bcdRendu is not null;
    public bool BcdVisible => BcdARendre || BcdRendu;
    public string LabelBcdTitre => _localize("Bcd.Rendre.Titre");
    public string LabelBcdDescription => _localize("Bcd.Rendre.Description");
    public string LabelBcdMaintenir => _localize("Bcd.Rendre.Maintenir");
    public string LabelBcdFait => _localize("Bcd.Rendre.Fait");
    public string LabelBcdAnnuler => _localize("Bcd.Rendre.Annuler");

    private void ArmerBcd(RepairTriage triage)
    {
        var f = triage.Attention.FirstOrDefault(x => x.Domaine == "BCD" && x.Nom == DrDxt.Application.Safety.GardeDuBcdRetire.Element
                                                     && !string.IsNullOrWhiteSpace(x.ValeurCourante));
        _bcdARendre = f is null ? null : (f.Nom, f.ValeurCourante!);
        // L'audit ne voit plus le reglage : s'il a ete retire par DrDXT lors d'une session precedente,
        // la carte le redit et garde son Annuler. S'il est revenu, le fichier est perime et ignore.
        if (f is null && _bcdRendu is null)
            _bcdRendu = GardeBcd?.Lire();
        SignalerBcd();
    }

    private void SignalerBcd()
    {
        OnPropertyChanged(nameof(BcdARendre));
        OnPropertyChanged(nameof(BcdRendu));
        OnPropertyChanged(nameof(BcdVisible));
        OnPropertyChanged(nameof(CarteWindowsVisible));
    }

    /// <summary>Retire l'element par le helper (UAC a la demande) ; le helper relit apres ecriture.</summary>
    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task RendreBcd()
    {
        if (BcdEnCours || _bcdARendre is not { } cible || _helper is null) return;
        BcdEnCours = true;
        ErreurBcd = null;
        try
        {
            // La garde d'abord : bcdedit écrit tout de suite, une coupure entre les deux laisserait un retrait sans annulation.
            if (GardeBcd is { } garde && !garde.Ecrire(cible.Element, cible.Valeur))
            {
                ErreurBcd = _localize("Bcd.Rendre.GardeImpossible");
                return;
            }
            var r = await _helper.ExecuteAsync(new DeleteBcdValueOp(cible.Element), CancellationToken.None);
            if (r.IsOk)
            {
                _bcdRendu = cible;
                // Le constat restait sur la page à côté de la carte « rendu » (même
                // défaut que la remesure processeur). Retiré de la page, gardé pour l'annulation.
                _constatBcdRetire = RepairFindings.FirstOrDefault(v => v.DomaineCle == "BCD" && v.NomCle == cible.Element);
                if (_constatBcdRetire is not null) { RepairFindings.Remove(_constatBcdRetire); ReconstruireCarteSurLeDomaineOuvert(); }
            }
            else
            {
                // Un refus n'a rien écrit : la garde ferait proposer un faux Annuler. Sans réponse du helper après l'envoi,
                // bcdedit a pu retirer la valeur : la garde reste, et la session suivante propose l'Annuler si l'audit ne la
                // voit plus.
                if (r.Error?.Code is not (ErrorCodes.HelperNoReply or ErrorCodes.HelperNoReplyJournalisee)) GardeBcd?.Effacer();
                ErreurBcd = RaisonLisible.Echec(r, _localize("Bcd.Rendre.Echec"), _localize);
            }
        }
        finally { BcdEnCours = false; SignalerBcd(); }
    }

    private RepairFindingViewModel? _constatBcdRetire;

    /// <summary>La carte est construite au scan : un geste qui change ce qu'elle montre la refait, sur le
    /// domaine que le joueur regardait.</summary>
    private void ReconstruireCarteSurLeDomaineOuvert()
    {
        var ouvert = DomaineSelectionne?.Cle;
        if (Domaines.Count > 0) ConstruireCarte();
        if (Domaines.FirstOrDefault(d => d.Cle == ouvert) is { } meme) DomaineSelectionne = meme;
    }

    /// <summary>Repose exactement la valeur retiree.</summary>
    [RelayCommand]
    private async Task AnnulerBcd()
    {
        if (BcdEnCours || _bcdRendu is not { } rendu || _helper is null) return;
        BcdEnCours = true;
        ErreurBcd = null;
        try
        {
            var r = await _helper.ExecuteAsync(new SetBcdValueOp(rendu.Element, rendu.Valeur), CancellationToken.None);
            if (r.IsOk)
            {
                _bcdRendu = null;
                GardeBcd?.Effacer();
                if (_constatBcdRetire is not null) { RepairFindings.Add(_constatBcdRetire); _constatBcdRetire = null; ReconstruireCarteSurLeDomaineOuvert(); }
            }
            else ErreurBcd = RaisonLisible.Echec(r, _localize("Bcd.Rendre.Echec"), _localize);
        }
        finally { BcdEnCours = false; SignalerBcd(); }
    }

    // ── La carte « Agir » du domaine processeur : remesurer a la demande ────────────────────────
    // La reference se mesure au premier scan puis se relit : sans ce bouton, elle ne se
    // referait jamais, meme devenue fausse (une reference ancienne peut ignorer la limite de puissance).
    [ObservableProperty] private bool _remesureCpuEnCours;
    [ObservableProperty] private bool _remesureCpuFaite;
    [ObservableProperty] private string? _erreurRemesureCpu;
}
