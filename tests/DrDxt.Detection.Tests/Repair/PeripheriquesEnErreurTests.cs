using DrDxt.Core;
using DrDxt.Detection.Hardware;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

/// <summary>
/// Sur la machine de reference, les trois peripheriques « en erreur » sont des profils
/// Bluetooth mains libres DESACTIVES (code 22). Le code decide du statut : un choix n'est pas une panne.
/// </summary>
public class PeripheriquesEnErreurTests
{
    [Fact]
    public void Un_peripherique_desactive_est_un_choix_probable_pas_une_panne()
    {
        var c = PeripheriquesEnErreur.Juger("Casque A (BT) Hands-Free AG", 22)!;
        c.Statut.Should().Be(RepairStatus.UserChoiceProbable);
        c.Action.Should().Be(RepairAction.Aucune);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(28)]
    [InlineData(43)]
    public void Un_pilote_en_echec_est_suspect_et_recommande_une_action(int code)
    {
        var c = PeripheriquesEnErreur.Juger("Contrôleur", code)!;
        c.Statut.Should().Be(RepairStatus.Suspicious);
        c.Action.Should().Be(RepairAction.Recommandation);
        // Le geste nomme le périphérique lu, celui à chercher dans le Gestionnaire de périphériques.
        c.Remede.Should().StartWith("1) ").And.Contain("Gestionnaire de périphériques").And.Contain("« Contrôleur »");
        // L'identifiant lu suit le constat : c'est lui que le geste « relancer » vise, jamais un nom affiché.
        PeripheriquesEnErreur.Juger("Contrôleur", code, @"PCI\VEN_1\3")!.Cible.Should().Be(@"PCI\VEN_1\3");
    }

    [Theory]
    [InlineData(24)]
    [InlineData(45)]
    public void Un_appareil_absent_ne_fait_pas_de_constat(int code)
        => PeripheriquesEnErreur.Juger("Clé USB débranchée", code).Should().BeNull();

    [Fact]
    public void Un_code_inconnu_est_observe_sans_interpretation()
        => PeripheriquesEnErreur.Juger("x", 57)!.Statut.Should().Be(RepairStatus.Observed);

    private sealed class WmiPeripheriques(params WmiRow[] lignes) : IWmiQuery
    {
        public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);
        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
            => Result<IReadOnlyList<WmiRow>>.Ok(wql.Contains("Win32_PnPEntity") ? lignes : []);
    }

    private static WmiRow Ligne(string nom, int code, bool present)
        => new(new Dictionary<string, object?> { ["Name"] = nom, ["ConfigManagerErrorCode"] = (uint)code, ["Present"] = present });

    private static IReadOnlyList<RepairFinding> Famille(IWmiQuery wmi)
        => new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), wmi).Lire()
            .Where(c => c.Domaine == "PERIPHERIQUES" && c.Nom is not ("inventaire des pilotes" or "pilotes non signes" or "pilote chipset AMD")).ToList();

    [Fact]
    public void L_audit_rend_les_peripheriques_branches_et_ignore_les_absents()
    {
        var constats = Famille(new WmiPeripheriques(
            Ligne("Casque C Hands-Free AG", 22, present: true),
            Ligne("Carte réseau fantôme", 43, present: false)));

        constats.Should().ContainSingle().Which.Nom.Should().Be("Casque C Hands-Free AG");
    }

    [Fact]
    public void Sans_peripherique_en_erreur_l_audit_le_dit()
        => Famille(new WmiPeripheriques()).Should().ContainSingle()
            .Which.Statut.Should().Be(RepairStatus.ExpectedDefault);
}
