namespace DrDxt.App.Navigation;

/// <summary>
/// Default <see cref="INavigationService"/>: holds the current target view-model and
/// notifies the shell when it changes. No WPF dependency — the shell subscribes to
/// <see cref="CurrentViewModelChanged"/> and maps the target VM onto the right
/// <c>UserControl</c>. Single-instance (registered as a singleton in DI).
/// </summary>
public sealed class NavigationService : INavigationService
{
    private object? _currentViewModel;

    public object? CurrentViewModel => _currentViewModel;

    public event EventHandler<object?>? CurrentViewModelChanged;

    public void NavigateToScan(object scanViewModel)
    {
        ArgumentNullException.ThrowIfNull(scanViewModel);
        Set(scanViewModel);
    }

    public void NavigateToDashboard(object dashboardViewModel)
    {
        ArgumentNullException.ThrowIfNull(dashboardViewModel);
        Set(dashboardViewModel);
    }

    private void Set(object target)
    {
        _currentViewModel = target;
        CurrentViewModelChanged?.Invoke(this, target);
    }
}
