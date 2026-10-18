using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Detection.Repair;

namespace DrDxt.App.ViewModels;

/// <summary>Le paquet d'aide en un clic : préparé, chaque fichier lisible tel qu'il partira (déjà anonymisé), puis
/// enregistré là où le joueur le choisit. Rien ne part sur le réseau ; le joueur l'envoie lui-même.</summary>
public sealed partial class ScanViewModel
{
    public ObservableCollection<string> LignesPaquet { get; } = [];
    public string LabelPaquetContient => _localize("Paquet.Contient");
    public string LabelApercuPaquet => _localize("Paquet.Apercu");
    public string LabelPreparerPaquet => _localize("Paquet.Preparer");
    public string LabelEnregistrerPaquet => _localize("Paquet.Enregistrer");
    public bool PaquetPret => _paquet is not null;
    private IReadOnlyList<(string Nom, string Texte)>? _paquet;

    /// <summary>Demande au joueur où écrire le zip ; <c>null</c> = annulé. Posé par l'App, absent dans les tests.</summary>
    internal Func<string, string?>? ChoisirCheminDuPaquet { get; set; }

    [ObservableProperty]
    private string _lastPaquetPath = string.Empty;

    /// <summary>Le fichier choisi dans la liste ; son texte exact, celui qui entrera dans le zip, s'affiche dessous.</summary>
    [ObservableProperty]
    private int _paquetChoisi = -1;

    [ObservableProperty]
    private string _apercuPaquet = string.Empty;

    partial void OnPaquetChoisiChanged(int value)
        => ApercuPaquet = _paquet is { } p && value >= 0 && value < p.Count ? p[value].Texte : string.Empty;

    [RelayCommand]
    private async Task PreparerPaquetAsync()
    {
        var rapport = RapportBrut(DateTimeOffset.Now);
        PoserPaquet(await Task.Run(() => PaquetDeSupport.Contenu(DrDxt.Core.DossierDonnees.Racine(), rapport, MachineExportee.ValeursDeCePc())));
    }

    /// <summary>Le contenu préparé, montré fichier par fichier ; rien n'est choisi, donc rien n'est encore affiché.</summary>
    internal void PoserPaquet(IReadOnlyList<(string Nom, string Texte)> paquet)
    {
        _paquet = paquet;
        LignesPaquet.Clear();
        foreach (var (nom, texte) in _paquet) LignesPaquet.Add(PaquetDeSupport.Ligne(nom, texte, _localize("Unite.Ko")));
        PaquetChoisi = -1;
        OnPropertyChanged(nameof(PaquetPret));
    }

    [RelayCommand]
    private void EnregistrerPaquet()
    {
        if (_paquet is null || ChoisirCheminDuPaquet?.Invoke($"drdxt-aide-{DateTimeOffset.Now:yyyy-MM-dd-HHmm}.zip") is not { } chemin) return;
        try
        {
            PaquetDeSupport.Ecrire(chemin, _paquet);
            LastPaquetPath = chemin;
        }
        catch (Exception)
        {
            LastPaquetPath = string.Empty;
            ApplyBlockedMessage = _localize("Paquet.Echec");
        }
    }
}
