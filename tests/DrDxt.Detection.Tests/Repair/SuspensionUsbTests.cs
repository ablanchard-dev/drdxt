using DrDxt.Core;
using DrDxt.Detection.Repair;
using DrDxt.Detection.Settings;
using DrDxt.Detection.Wmi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Repair;

// La suspension sélective USB se dit, sans geste. Sources : Microsoft Learn, « USB Selective
// Suspend » (activée par défaut, ne pas la couper) et « Selective Suspend for HID Over USB Devices » (coupée par Windows
// sur les périphériques d'entrée, pour éviter le délai de reprise).
public sealed class SuspensionUsbTests
{
    private static PowerSettingValue Plan(uint? valeur, uint? defaut) => new("suspension sélective USB (secteur)", valeur, defaut, "");

    [Theory]
    [InlineData(1u, 1u, RepairStatus.ExpectedDefault)]
    [InlineData(0u, 1u, RepairStatus.NonDefault)]
    [InlineData(1u, 0u, RepairStatus.NonDefault)]
    public void Le_reglage_du_plan_se_dit_sans_jamais_proposer_de_geste(uint valeur, uint defaut, RepairStatus attendu)
    {
        var c = SuspensionUsb.JugerPlan(Plan(valeur, defaut));

        c.Statut.Should().Be(attendu);
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().NotContainAny("%", " ms", "gain de");
    }

    [Fact]
    public void Un_plan_non_lu_se_dit_non_lu_et_un_defaut_non_lu_ne_passe_pas_pour_identique()
    {
        SuspensionUsb.JugerPlan(Plan(null, 1)).Statut.Should().Be(RepairStatus.Unknown);
        var c = SuspensionUsb.JugerPlan(Plan(0, null));
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("désactivée");
    }

    [Fact]
    public void Des_entrees_sans_suspension_sont_le_defaut_de_Windows()
    {
        var c = SuspensionUsb.JugerEntrees(9, 0);

        c.Statut.Should().Be(RepairStatus.ExpectedDefault);
        c.ValeurCourante.Should().Be("activée sur 0 des 9");
        c.Action.Should().Be(RepairAction.Aucune);
    }

    [Fact]
    public void Une_entree_suspendue_par_son_constructeur_se_dit_sans_geste_ni_delai_invente()
    {
        var c = SuspensionUsb.JugerEntrees(3, 1);

        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("activée sur 1 des 3");
        c.Action.Should().Be(RepairAction.Aucune);
        c.Explication.Should().Contain("aucun délai n'est mesuré");
    }

    [Fact]
    public void Une_liste_non_lue_n_est_pas_une_liste_vide()
    {
        SuspensionUsb.JugerEntrees(null, 0).Statut.Should().Be(RepairStatus.Unknown);
        SuspensionUsb.JugerEntrees(0, 0).Statut.Should().Be(RepairStatus.Observed);
    }

    // Lu sur la machine de référence le 07/10/2026 : REG_BINARY d'un octet sur les périphériques HidUsb.
    [Fact]
    public void La_valeur_de_registre_se_lit_en_binaire_comme_en_dword_et_absente_vaut_non_activee()
    {
        SuspensionUsb.Activee(new byte[] { 1 }).Should().BeTrue();
        SuspensionUsb.Activee(new byte[] { 0 }).Should().BeFalse();
        SuspensionUsb.Activee(1).Should().BeTrue();
        SuspensionUsb.Activee(0).Should().BeFalse();
        SuspensionUsb.Activee(null).Should().BeFalse();
        SuspensionUsb.Activee(Array.Empty<byte>()).Should().BeFalse();
    }

    // Le branchement dans l'audit : un périphérique absent ne compte pas. L'identifiant inventé n'a pas de clé au registre
    // (lecture seule, rien n'est écrit) : il compte comme non activé.
    [Fact]
    public void L_audit_compte_les_entrees_branchees_et_ignore_les_absentes()
    {
        var wmi = new WmiHid(Ligne(@"USB\VID_0000&PID_0000\DRDXT-TEST-1", true), Ligne(@"USB\VID_0000&PID_0000\DRDXT-TEST-2", false));

        var c = new RepairAuditService(new ServicesMuets(), new NoyauMuet(), new ArretsMuets(), new MemoireMuette(),
                new VidagesMuets(), wmi).Lire()
            .Single(c => c.Domaine == SuspensionUsb.Domaine && c.Nom == SuspensionUsb.NomEntrees);

        c.ValeurCourante.Should().Be("activée sur 0 des 1");
    }

    private sealed class WmiHid(params WmiRow[] lignes) : IWmiQuery
    {
        public Result<IReadOnlyList<WmiRow>> Query(string wql) => QueryInNamespace("", wql);
        public Result<IReadOnlyList<WmiRow>> QueryInNamespace(string ns, string wql)
            => Result<IReadOnlyList<WmiRow>>.Ok(wql.Contains("HidUsb") ? lignes : []);
    }

    private static WmiRow Ligne(string id, bool present)
        => new(new Dictionary<string, object?> { ["PNPDeviceID"] = id, ["Present"] = present });

    [Fact]
    public void Aucun_constat_USB_ne_monte_dans_ce_qui_demande_l_attention()
    {
        RepairFinding[] tous = [SuspensionUsb.JugerPlan(Plan(0, 1)), SuspensionUsb.JugerPlan(Plan(1, 1)),
            SuspensionUsb.JugerEntrees(4, 2), SuspensionUsb.JugerEntrees(4, 0)];

        RepairTriage.Trier(tous).Attention.Should().BeEmpty();
    }
}
