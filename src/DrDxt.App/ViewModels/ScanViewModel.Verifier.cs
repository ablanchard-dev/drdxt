using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrDxt.App.ViewModels;

/// <summary>Ce que rend une vérification de la machine. <paramref name="Refus"/> non nul = rien n'a été écrit, et pourquoi
/// (clé de ressource). <paramref name="AVerifier"/> : les réglages pas revenus, ou revenus sans relecture qui le confirme.</summary>
public sealed record VerificationRendue(string? Refus, IReadOnlyList<string> AVerifier, string? Chemin, int Valides = 0,
    int NonTouches = 0, int Refuses = 0,
    DrDxt.Application.Autopilot.ValidationComplete.Arret ArretPar = DrDxt.Application.Autopilot.ValidationComplete.Arret.Aucun);

// 08/10/2026 (T1b, le mainteneur : « rajouter les tests pour valider certaines choses pour les users dans l'app ») : « Vérifier
// DrDXT sur mon PC ». Chaque réglage que DrDXT sait poser est posé, relu, remis d'origine, relu encore, sur la machine du
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

    /// <summary>CONTRIB1 (08/10/2026, « direct en lien ») : le rapport part par un ticket GitHub pré-rempli, sans serveur ; DrDXT
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

    /// <summary>RAPPORT-FINAL (09/10/2026) : ce que DrDXT ne peut pas écrire (BIOS, câbles, réglages du jeu) se lit étape par
    /// étape sur le dépôt. Navigateur absent (revue du 09/10) : l'adresse est copiée dans le presse-papiers, pour la coller
    /// ailleurs ; le presse-papiers lui-même peut refuser, et alors rien de plus n'est possible d'ici.</summary>
    [RelayCommand]
    private void OuvrirGuide()
    {
        if (OuvrirLien(DrDxt.App.Platform.LienDeRapport.Guide)) return;
        try { System.Windows.Clipboard.SetText(DrDxt.App.Platform.LienDeRapport.Guide); } catch (Exception) { }
    }

    [RelayCommand]
    private void DemanderVerification()
    {
        VerificationBilan = string.Empty;
        VerificationChemin = string.Empty;
        VerificationAConfirmer = true;
    }

    [RelayCommand]
    private void AnnulerVerification() => VerificationAConfirmer = false;

    [RelayCommand]
    private async Task ConfirmerVerificationAsync()
    {
        VerificationAConfirmer = false;
        if (_verifierLaMachine is not { } verifier || VerificationEnCours) return;
        // Jamais en même temps qu'un essai en alternance : un seul essai tient dans le journal de remise.
        if (!await _verrouEssai.WaitAsync(0).ConfigureAwait(true))
        {
            VerificationBilan = _localize("Verifier.EssaiEnCours");
            return;
        }
        VerificationEnCours = true;
        try
        {
            var r = await Task.Run(() => verifier(bloc =>
            {
                // « === latency.gameModeOn » : le réglage en cours, dit avec son nom.
                var id = bloc.StartsWith("=== ", StringComparison.Ordinal) ? bloc[4..].Split('\n')[0].Trim() : null;
                if (id is not null) VerificationProgres = string.Format(_localize("Verifier.EnCours"), _localize(TweakDisplayKeys.TitleKey(id)));
            }));
            VerificationChemin = r.Chemin ?? string.Empty;
            // Revue n° 10 : le bilan compte ce qui a été vérifié, et dit pourquoi la vérification s'est arrêtée ;
            // « tout est remis » ne s'affiche plus quand rien n'a été posé.
            VerificationBilan = r.Refus is { } refus ? _localize(refus)
                : r.AVerifier.Count > 0 ? string.Format(_localize("Verifier.AVerifier"), string.Join(Environment.NewLine, r.AVerifier))
                : r.ArretPar == DrDxt.Application.Autopilot.ValidationComplete.Arret.RefusDeWindows ? _localize("Verifier.RefusWindows")
                : r.ArretPar == DrDxt.Application.Autopilot.ValidationComplete.Arret.JeuOuvert ? _localize("Verifier.JeuOuvertEnCours")
                : Pluriels.Accorder(string.Format(_localize("Verifier.Compte"), r.Valides, r.NonTouches, r.Refuses));
        }
        catch (Exception)
        {
            VerificationBilan = _localize("Verifier.Echec");
        }
        finally
        {
            VerificationProgres = string.Empty;
            VerificationEnCours = false;
            _verrouEssai.Release();
        }
    }
}
