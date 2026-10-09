namespace DrDxt.App.Navigation;

/// <summary>
/// Minimal in-app view switching surface (MVVM, testable). The scan → apply flow
/// drives this to show the before/after dashboard with the real <see cref="DrDxt.Core.ApplyReport"/>,
/// and the dashboard "back" drives it the other way. The shell (App / MainWindow)
/// is the only thing that knows how to map a target view-model onto a WPF control —
/// the view-models depend on this interface, never on WPF, so they stay unit-testable.
/// </summary>
public interface INavigationService
{
    /// <summary>The view-model the shell should currently host, or null at startup
    /// before the first navigation. Raises <see cref="CurrentViewModelChanged"/> on change.</summary>
    object? CurrentViewModel { get; }

    /// <summary>Raised whenever <see cref="CurrentViewModel"/> changes so the shell can
    /// swap the hosted control. Carries the new target view-model.</summary>
    event EventHandler<object?>? CurrentViewModelChanged;

    /// <summary>Show the scan view-model (the funnel entry point / "back" target).</summary>
    void NavigateToScan(object scanViewModel);

    /// <summary>Show the before/after dashboard view-model after an apply.</summary>
    void NavigateToDashboard(object dashboardViewModel);
}
