using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace DrDxt.App.ViewModels;

/// <summary>
/// Le test de la ligne sous charge, à côté de la protection du ping : rien ne part sans le clic du joueur, la description
/// nomme le serveur et le volume avant le clic, et le compte rendu reste affiché sous le bouton.
/// </summary>
public partial class ScanViewModel
{
    private Func<CancellationToken, Task<string>>? _testerLaLigne;

    /// <summary>Posé par l'App au démarrage ; null dans les tests, et le bouton n'existe pas.</summary>
    internal Func<CancellationToken, Task<string>>? TesterLaLigne
    {
        get => _testerLaLigne;
        set { _testerLaLigne = value; OnPropertyChanged(nameof(TestLigneDisponible)); }
    }

    public bool TestLigneDisponible => _testerLaLigne is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LabelTestLigne))]
    private bool _testLigneEnCours;

    [ObservableProperty] private string? _texteTestLigne;

    public string LabelTestLigne => _localize(TestLigneEnCours ? "Ligne.Test.EnCours" : "Ligne.Test.Libelle");

    public string LabelTestLigneArreter => _localize("Ligne.Test.Arreter");

    private CancellationTokenSource? _arretTestLigne;

    public string LabelTestLigneDescription => string.Format(System.Globalization.CultureInfo.CurrentUICulture,
        _localize("Ligne.Test.Description"), DrDxt.Measurement.Network.TestDeLigneSousCharge.Hote,
        DrDxt.Measurement.Network.TestDeLigneSousCharge.OctetsMaxParSens / 1_000_000);

    [RelayCommand(CanExecute = nameof(AucuneOperationLongue))]
    private async Task TesterLigne()
    {
        if (_testerLaLigne is not { } tester || TestLigneEnCours) return;
        using var arret = new CancellationTokenSource();
        _arretTestLigne = arret;
        TestLigneEnCours = true;
        try { TexteTestLigne = await tester(arret.Token); }
        catch (OperationCanceledException) when (arret.IsCancellationRequested) { TexteTestLigne = _localize("Ligne.Test.Annule"); }
        catch (Exception ex)
        {
            TexteTestLigne = string.Format(System.Globalization.CultureInfo.CurrentUICulture, _localize("Ligne.Test.Echec"), RaisonLisible.Dire(ex, _localize));
        }
        finally
        {
            _arretTestLigne = null;
            TestLigneEnCours = false;
        }
    }

    /// <summary>Arrête le test en cours : le trafic cesse sur-le-champ (TestDeLigneSousCharge annule chaque requête).</summary>
    [RelayCommand]
    private void ArreterTestLigne() => _arretTestLigne?.Cancel();
}
