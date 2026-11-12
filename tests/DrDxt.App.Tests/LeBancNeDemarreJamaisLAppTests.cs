using System.Linq;
using System.Windows.Threading;
using FluentAssertions;
using Xunit;

namespace DrDxt.App.Tests;

/// <summary>
/// Le banc construit l'App pour ses ressources XAML, jamais pour la démarrer. Le constructeur d'Application poste
/// pourtant OnStartup sur le dispatcher : le premier test qui le pompait jouait le vrai démarrage (services réels,
/// fenêtre, helper), et sur une machine en anglais tous les tests suivants lisaient le docteur en anglais.
/// </summary>
public sealed class LeBancNeDemarreJamaisLAppTests
{
    [Fact]
    public void Le_demarrage_que_poste_le_constructeur_de_l_App_est_ecarte()
    {
        StaApplication.Run(() => 0);

        var postees = StaApplication.DemarragesPostes;
        postees.Should().NotBeNullOrEmpty("témoin : le constructeur d'Application poste bien son démarrage");
        postees!.Select(o => o.Status).Should().OnlyContain(s => s == DispatcherOperationStatus.Aborted,
            "un démarrage resté en file part au premier test qui pompe le dispatcher");
    }
}
