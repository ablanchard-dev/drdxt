using System.Windows.Controls;

namespace DrDxt.App.Views;

/// <summary>
/// M2 G2 — the before/after dashboard view. Visual sibling of <see cref="ScanView"/>:
/// same dark cyber tokens, same Panel / Eyebrow / Mono / Body / Muted styles, same
/// hand-built (Polyline/Rectangle/Path) charts — no charting NuGet. M5 wired the
/// "Revert all" action and "Back to scan" to real commands on the view-model, so the
/// code-behind now holds no logic beyond InitializeComponent.
/// </summary>
public partial class DashboardView : UserControl
{
    public DashboardView()
    {
        InitializeComponent();
    }
}
