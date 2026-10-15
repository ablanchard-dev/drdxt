using DrDxt.Core;
using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>Ce que le lecteur rend au sélecteur, famille comprise, à partir des champs de l'événement.</summary>
public class ArretsClassesTests
{
    private static string Xml(int code, int veille, long bouton) =>
        "<Event xmlns='http://schemas.microsoft.com/win/2004/08/events/event'><EventData>"
        + $"<Data Name='BugcheckCode'>{code}</Data><Data Name='SleepInProgress'>{veille}</Data>"
        + $"<Data Name='PowerButtonTimestamp'>{bouton}</Data></EventData></Event>";

    private sealed class Journal(params ArretBrutal[] arrets) : IUnexpectedShutdownLog
    {
        public Result<IReadOnlyList<DateTimeOffset>> Since(DateTimeOffset earliest) => throw new NotSupportedException();
        public Result<IReadOnlyList<ArretBrutal>> Detail(DateTimeOffset earliest) => Result<IReadOnlyList<ArretBrutal>>.Ok(arrets);
    }

    [Fact]
    public void L_arret_du_05_10_BugcheckCode_0_SleepInProgress_6_est_une_coupure_en_veille()
        => ArretBrutal.DepuisXml(DateTimeOffset.UtcNow, Xml(0, 6, 0)).Famille.Should().Be(FamilleDArret.Veille);

    [Fact]
    public void Un_0x116_en_decimal_dans_l_evenement_est_un_ecran_bleu_graphique()
        // Microsoft : Event ID 41 porte le code en décimal (0x116 = 278).
        => ArretBrutal.DepuisXml(DateTimeOffset.UtcNow, Xml(278, 0, 0)).Famille.Should().Be(FamilleDArret.EcranBleuGraphique);

    [Fact]
    public void Le_selecteur_recoit_les_familles_sans_les_appuis_longs()
    {
        var maintenant = DateTimeOffset.UtcNow;
        var lus = StabiliteLue.ArretsBrutaux(new Journal(
            ArretBrutal.DepuisXml(maintenant.AddDays(-1), Xml(0, 6, 0)),
            ArretBrutal.DepuisXml(maintenant.AddDays(-2), Xml(0, 0, 131728546170882432)),
            ArretBrutal.DepuisXml(maintenant.AddDays(-3), Xml(278, 0, 0))), maintenant);

        lus!.Select(a => a.Famille).Should().Equal(FamilleDArret.Veille, FamilleDArret.EcranBleuGraphique);
        lus![1].CodeArret.Should().Be(0x116);
    }
}
