using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using DrDxt.Application.Support;
using DrDxt.Core;

namespace DrDxt.App.ViewModels;

/// <summary>« Quoi de neuf » : après une mise à jour de DrDXT, ce qui a changé pour le joueur, montré une fois, tiré du
/// CHANGELOG livré dans l'App. « Vu » écrit seulement la version vue dans le dossier des données du joueur.</summary>
public sealed partial class ScanViewModel
{
    public ObservableCollection<string> LignesQuoiDeNeuf { get; } = [];
    public bool AQuoiDeNeuf => LignesQuoiDeNeuf.Count > 0;
    public string LabelQuoiDeNeuf => _localize("QuoiDeNeuf.Titre");
    public string LabelQuoiDeNeufVu => _localize("QuoiDeNeuf.Vu");
    private string? _fichierQuoiDeNeuf;
    private string? _versionQuoiDeNeuf;

    /// <summary>Charge les sections non vues. <paramref name="changelog"/> null (ressource absente) : rien n'est montré.</summary>
    internal void ChargerQuoiDeNeuf(string? changelog, string fichierVu, string? version)
    {
        (_fichierQuoiDeNeuf, _versionQuoiDeNeuf) = (fichierVu, version);
        if (changelog is null || version is null) return;
        var vue = NotesDeVersion.VersionVue(new FichierJsonAtomique(fichierVu, "QUOI_DE_NEUF").Read() is { IsOk: true, Value: { } v } ? v : null);
        foreach (var s in NotesDeVersion.AMontrer(changelog, vue, version, DrDxt.Core.Docteur.Langue))
        {
            LignesQuoiDeNeuf.Add(s.Titre);
            foreach (var p in s.Points) LignesQuoiDeNeuf.Add("• " + p);
        }
        OnPropertyChanged(nameof(AQuoiDeNeuf));
    }

    [RelayCommand]
    private void MarquerQuoiDeNeufVu()
    {
        if (_fichierQuoiDeNeuf is { } f && _versionQuoiDeNeuf is { } v)
            new FichierJsonAtomique(f, "QUOI_DE_NEUF").WriteAtomic(JsonSerializer.Serialize(v));
        LignesQuoiDeNeuf.Clear();
        OnPropertyChanged(nameof(AQuoiDeNeuf));
    }
}
