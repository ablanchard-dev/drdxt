using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrDxt.App.ViewModels;

/// <summary>Ce que rend une vérification de la machine. <paramref name="Refus"/> non nul = rien n'a été écrit, et pourquoi
/// (clé de ressource). <paramref name="AVerifier"/> : les réglages pas revenus, ou revenus sans relecture qui le confirme.</summary>
public sealed record VerificationRendue(string? Refus, IReadOnlyList<string> AVerifier, string? Chemin, int Valides = 0,
    int NonTouches = 0, int Refuses = 0,
    DrDxt.Application.Autopilot.ValidationComplete.Arret ArretPar = DrDxt.Application.Autopilot.ValidationComplete.Arret.Aucun)
{
    /// <summary>Les réglages posés, relus et rendus, par identifiant (le bilan les nomme un par un).</summary>
    public IReadOnlyList<string> ValidesIds { get; init; } = [];

    /// <summary>Les réglages que la vérification a atteints ; après un arrêt, les autres n'ont pas été essayés.</summary>
    public IReadOnlyList<string> ParcourusIds { get; init; } = [];
}

// « Vérifier DrDXT sur mon PC ». Chaque réglage que DrDXT sait poser est posé, relu, remis d'origine, relu encore, sur la machine du
// joueur, après son accord. Le bilan dit ce qui marche ici ; le rapport va dans Documents et le joueur l'envoie
// lui-même. Ça prouve que DrDXT sait écrire et rendre chaque réglage sur ce matériel, pas qu'il fait gagner des images :
// ça, c'est le relevé des parties.
public partial class ScanViewModel
{
    private Func<Action<string>, Task<VerificationRendue>>? _verifierLaMachine;

    /// <summary>Branché par l'App (leviers et canal élevé réels) ; null = pas de bouton.</summary>
    internal Func<Action<string>, Task<VerificationRendue>>? VerifierLaMachine
    {
        get => _verifierLaMachine;
        set { _verifierLaMachine = value; OnPropertyChanged(nameof(CanVerifierMachine)); }
    }

    public bool CanVerifierMachine => _verifierLaMachine is not null;

    /// <summary>Une ligne par
    /// réglage essayé, avec son nom et ce qui lui est arrivé, au lieu d'un seul compte.</summary>
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ADesLignesVerification))] private IReadOnlyList<string> _lignesVerification = [];
    public bool ADesLignesVerification => LignesVerification.Count > 0;

    // Suivre en direct : un fil des réglages essayés, et un arrêt demandé par le joueur, confirmé, lu par la validation
    // avant chaque réglage (jamais au milieu : chaque réglage est posé, relu et rendu d'un seul tenant). Aucune question
    // n'est répondue à la place du joueur.
    private volatile bool _arretVerificationDemande;
    internal bool ArretVerificationDemande => _arretVerificationDemande;
    [ObservableProperty] private bool _arretVerificationAConfirmer;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ADesEtapesVerification))] private IReadOnlyList<string> _etapesVerification = [];
    public bool ADesEtapesVerification => EtapesVerification.Count > 0;
    public string LabelVerifierArreter => _localize("Verifier.Arreter");
    public string LabelVerifierArretConsigne => _localize("Verifier.ArretConsigne");
    public string LabelVerifierArretConfirmer => _localize("Verifier.ArretConfirmer");

    [RelayCommand]
    private void DemanderArretVerification() => ArretVerificationAConfirmer = true;

    [RelayCommand]
    private void AnnulerArretVerification() => ArretVerificationAConfirmer = false;

    [RelayCommand]
    private void ConfirmerArretVerification()
    {
        ArretVerificationAConfirmer = false;
        _arretVerificationDemande = true;
        VerificationProgres = _localize("Verifier.ArretEnCours");
    }

    internal IReadOnlyList<string> Lignes(VerificationRendue r) =>
        DrDxt.Application.Autopilot.ValidationComplete.Leviers.Select(id =>
        {
            var nom = _localize(TweakDisplayKeys.TitleKey(id));
            var cle = r.AVerifier.Any(a => a.StartsWith(id, StringComparison.Ordinal)) ? "Verifier.Ligne.AVerifier"
                : r.ValidesIds.Contains(id) ? "Verifier.Ligne.Valide"
                // Arrêtée avant lui, rien de ce réglage n'a été lu : ni « déjà réglé » ni « pas sur ce PC ».
                : r.ArretPar != DrDxt.Application.Autopilot.ValidationComplete.Arret.Aucun && !r.ParcourusIds.Contains(id) ? "Verifier.Ligne.NonEssaye"
                : "Verifier.Ligne.NonTouche";
            return string.Format(_localize(cle), nom);
        }).ToList();

    [ObservableProperty] private bool _verificationAConfirmer;
    [ObservableProperty] private bool _verificationEnCours;
    [ObservableProperty] private string _verificationProgres = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVerificationBilan))] private string _verificationBilan = string.Empty;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasVerificationChemin))] private string _verificationChemin = string.Empty;

    public bool HasVerificationBilan => VerificationBilan.Length > 0;
    public bool HasVerificationChemin => VerificationChemin.Length > 0;

    public string LabelVerifierMachine => _localize("Verifier.Bouton");
    public string LabelVerifierConsentement => _localize("Verifier.Consentement");
    public string LabelVerifierConfirmer => _localize("Verifier.Confirmer");
    public string LabelVerifierAnnuler => _localize("Verifier.Annuler");
    public string LabelVerifierRapport => _localize("Verifier.RapportEcrit");

    public string LabelEnvoyerRapport => _localize("Verifier.Envoyer");

    /// <summary>CONTRIB1 : ouvrir une adresse dans le navigateur ; une couture pour les tests.</summary>
    internal Func<string, bool> OuvrirLien { get; set; } = lien =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(lien) { UseShellExecute = true })?.Dispose(); return true; }
        catch (Exception) { return false; }
    };

    /// <summary>Le rapport part par le modèle de ticket GitHub, vide, sans serveur : le joueur y joint son rapport ; DrDXT
    /// n'envoie rien, le joueur relit et soumet. Rapport illisible ou navigateur absent : dit, rien n'est inventé.</summary>
    [RelayCommand]
    private void EnvoyerRapport()
    {
        string texte;
        try { texte = System.IO.File.ReadAllText(VerificationChemin); }
        catch (Exception) { VerificationBilan = _localize("Verifier.EnvoiImpossible"); return; }
        var hw = _lastScan?.Hw;
        var lien = DrDxt.App.Platform.LienDeRapport.Construire(hw?.Cpu.Model ?? "?",
            (hw is null ? null : DrDxt.Application.Autopilot.ValidationComplete.CartePrincipale(hw))?.Model ?? "?", texte);
        if (!OuvrirLien(lien)) VerificationBilan = _localize("Verifier.EnvoiImpossible");
    }

    public string LabelGuide => _localize("Carte.Guide");

    /// <summary>Ce que DrDXT ne peut pas écrire (BIOS, câbles, réglages du jeu) se lit étape par
    /// étape sur le dépôt. Navigateur absent : l'adresse est copiée dans le presse-papiers, pour la coller
    /// ailleurs ; le presse-papiers lui-même peut refuser, et alors rien de plus n'est possible d'ici.</summary>
    [RelayCommand]
    private void OuvrirGuide() => OuvrirOuCopier(DrDxt.App.Platform.LienDeRapport.Guide);

    /// <summary>Le bouton « Ouvrir le guide (5.23) » arrive sur l'étape, pas en haut du document.</summary>
    [RelayCommand]
    private void OuvrirGuideSousTension() => OuvrirOuCopier(DrDxt.App.Platform.LienDeRapport.GuideSousTension);

    private void OuvrirOuCopier(string lien)
    {
        if (OuvrirLien(lien)) return;
        try { System.Windows.Clipboard.SetText(lien); } catch (Exception) { /* presse-papiers pris par un autre programme : le lien reste écrit à l'écran */ }
    }

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private void DemanderVerification()
    {
        VerificationBilan = string.Empty;
        VerificationChemin = string.Empty;
        VerificationAConfirmer = true;
    }

    [RelayCommand]
    private void AnnulerVerification() => VerificationAConfirmer = false;

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task ConfirmerVerificationAsync()
    {
        VerificationAConfirmer = false;
        if (_verifierLaMachine is not { } verifier || VerificationEnCours) return;
        // Elle pose puis rend chaque levier : un gros changement, qui part après un point de restauration, comme Optimiser.
        if (await _safety.EnsureRestorePointAsync().ConfigureAwait(true) is { IsOk: false } point)
        {
            VerificationBilan = MessageDuPoint(point);
            return;
        }
        // Jamais en même temps qu'un essai en alternance : un seul essai tient dans le journal de remise.
        if (!await _verrouEssai.WaitAsync(0).ConfigureAwait(true))
        {
            VerificationBilan = _localize("Verifier.EssaiEnCours");
            return;
        }
        VerificationEnCours = true;
        _arretVerificationDemande = false;
        EtapesVerification = [];
        try
        {
            var r = await Task.Run(() => verifier(bloc =>
            {
                // « === latency.gameModeOn » : le réglage en cours, dit avec son nom.
                var id = bloc.StartsWith("=== ", StringComparison.Ordinal) ? bloc[4..].Split('\n')[0].Trim() : null;
                if (id is null) return;
                var titre = _localize(TweakDisplayKeys.TitleKey(id));
                EtapesVerification = [.. EtapesVerification, string.Format(_localize("Verifier.Etape"), titre)];
                if (!_arretVerificationDemande) VerificationProgres = string.Format(_localize("Verifier.EnCours"), titre);
            }));
            VerificationChemin = r.Chemin ?? string.Empty;
            LignesVerification = r.Refus is null ? Lignes(r) : [];
            // Le bilan compte ce qui a été vérifié, et dit pourquoi la vérification s'est arrêtée ;
            // « tout est remis » ne s'affiche plus quand rien n'a été posé.
            VerificationBilan = r.Refus is { } refus ? _localize(refus)
                : r.AVerifier.Count > 0 ? string.Format(_localize("Verifier.AVerifier"), string.Join(Environment.NewLine, r.AVerifier))
                : r.ArretPar == DrDxt.Application.Autopilot.ValidationComplete.Arret.RefusDeWindows ? _localize("Verifier.RefusWindows")
                : r.ArretPar == DrDxt.Application.Autopilot.ValidationComplete.Arret.JeuOuvert ? _localize("Verifier.JeuOuvertEnCours")
                : r.ArretPar == DrDxt.Application.Autopilot.ValidationComplete.Arret.ParLeJoueur
                    ? Pluriels.Accorder(string.Format(_localize("Verifier.ArreteParToi"), r.Valides, r.NonTouches, r.Refuses))
                : Pluriels.Accorder(string.Format(_localize("Verifier.Compte"), r.Valides, r.NonTouches, r.Refuses));
        }
        catch (Exception)
        {
            VerificationBilan = _localize("Verifier.Echec");
        }
        finally
        {
            VerificationProgres = string.Empty;
            ArretVerificationAConfirmer = false;
            VerificationEnCours = false;
            _verrouEssai.Release();
        }
    }
}
