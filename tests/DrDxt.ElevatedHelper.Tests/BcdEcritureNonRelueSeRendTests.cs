using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// « /set » réussi puis relecture ratée (ou autre valeur) : la réponse était un échec, la valeur restait écrite, et l'App,
// qui ne garde rien d'un échec, ne la rendait jamais ; le démarrage suivant la portait. L'état d'avant est relu avant
// l'écriture, et reposé si l'écriture ne se confirme pas.
public sealed class BcdEcritureNonRelueSeRendTests
{
    private sealed class Bcd
    {
        public Dictionary<string, string> Elements { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int EnumRates { get; set; }      // les N prochaines relectures échouent
        public int EnumAvantPanne { get; set; } // relectures qui passent avant la panne

        public (int, string) Run(string[] a)
        {
            switch (a[0])
            {
                case "/set": Elements[a[2]] = a[3]; return (0, "");
                case "/deletevalue": return (Elements.Remove(a[2]) ? 0 : 1, "");
                default:
                    if (EnumAvantPanne-- <= 0 && EnumRates-- > 0) return (1, "");
                    return (0, "identificateur {current}\n" + string.Join("\n", Elements.Select(e => $"{e.Key} {e.Value}")));
            }
        }
    }

    [Fact]
    public void Une_ecriture_que_la_relecture_ne_confirme_pas_est_rendue()
    {
        var bcd = new Bcd { EnumAvantPanne = 1, EnumRates = 1 }; // avant : lue ; après l'écriture : ratée ; ensuite : lue
        var r = new Win32BcdActions(bcd.Run).SetValue("useplatformclock", "Yes");

        r.IsOk.Should().BeFalse();
        bcd.Elements.Should().NotContainKey("useplatformclock", "l'élément absent avant l'écriture est retiré");
    }

    [Fact]
    public void Une_valeur_d_avant_est_reposee()
    {
        var bcd = new Bcd { EnumAvantPanne = 1, EnumRates = 1 };
        bcd.Elements["disabledynamictick"] = "No";

        new Win32BcdActions(bcd.Run).SetValue("disabledynamictick", "Yes").IsOk.Should().BeFalse();

        bcd.Elements["disabledynamictick"].Should().Be("No");
    }

    [Fact]
    public void Sans_lecture_d_avant_rien_n_est_ecrit()
    {
        var bcd = new Bcd { EnumRates = 5 };

        new Win32BcdActions(bcd.Run).SetValue("useplatformclock", "Yes").IsOk.Should().BeFalse();

        bcd.Elements.Should().BeEmpty();
    }

    [Fact]
    public void Une_ecriture_relue_passe()
    {
        var bcd = new Bcd();
        new Win32BcdActions(bcd.Run).SetValue("useplatformclock", "Yes").IsOk.Should().BeTrue();
        bcd.Elements["useplatformclock"].Should().Be("Yes");
    }
}
