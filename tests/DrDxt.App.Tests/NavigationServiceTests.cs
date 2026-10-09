using DrDxt.App.Navigation;
using FluentAssertions;

namespace DrDxt.App.Tests;

// -----------------------------------------------------------------------
// M5 — the minimal in-app navigation service backing scan ⇄ dashboard switching.
// Pure, no WPF: holds the current target VM and raises a change event the shell binds.
// -----------------------------------------------------------------------
public sealed class NavigationServiceTests
{
    [Fact]
    public void Current_view_model_is_null_before_first_navigation()
    {
        var nav = new NavigationService();
        nav.CurrentViewModel.Should().BeNull();
    }

    [Fact]
    public void Navigate_to_dashboard_sets_current_and_raises_change()
    {
        var nav = new NavigationService();
        var dashboard = new object();
        object? raised = null;
        nav.CurrentViewModelChanged += (_, vm) => raised = vm;

        nav.NavigateToDashboard(dashboard);

        nav.CurrentViewModel.Should().BeSameAs(dashboard);
        raised.Should().BeSameAs(dashboard);
    }

    [Fact]
    public void Navigate_to_scan_sets_current_and_raises_change()
    {
        var nav = new NavigationService();
        var scan = new object();
        object? raised = null;
        nav.CurrentViewModelChanged += (_, vm) => raised = vm;

        nav.NavigateToScan(scan);

        nav.CurrentViewModel.Should().BeSameAs(scan);
        raised.Should().BeSameAs(scan);
    }
}
