using DrDxt.Core;
using DrDxt.Detection.Display;
using DrDxt.Detection.Scoring;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Display;

/// <summary>
/// Trou trouve par l'audit du 07/09/2026. La machine de reference tire **55,9 W au repos** pour
/// une carte qui plafonne a 115 W, avec deux ecrans a **200 et 144 Hz**. Aucun levier du produit ne
/// regardait la consommation au repos -- alors que c'est le seul effet qui joue 24 h sur 24 au lieu
/// des heures de jeu.
///
/// La cause est sourcee (`docs/LEVIERS-REPOS-ET-RESEAU.md`, R-01 a R-03) : deux ecrans a des
/// frequences DIFFERENTES empechent la memoire video de descendre dans son etat de repos, et NVIDIA
/// decrit ce comportement comme voulu, en conseillant lui-meme de baisser la frequence hors jeu.
///
/// Ce que ce conseil ne doit jamais devenir : « baisse ton ecran principal ». Ca couterait des
/// images en jeu, c'est-a-dire exactement le defaut que `Finding.Display.RefreshBelowMax` denonce.
/// </summary>
public class IdlePowerAdvisorTests
{
    [Fact]
    public void Deux_ecrans_a_des_frequences_DIFFERENTES_sont_signales()
    {
        // Le cas exact de la machine de reference : 200 Hz et 144 Hz.
        IdlePowerAdvisor.Advise(new[] { 200, 144 })
            .Should().ContainSingle(g => g.MessageKey == "Finding.Display.MixedRefreshIdlePower");
    }

    [Fact]
    public void Deux_ecrans_a_la_MEME_frequence_ne_disent_rien()
    {
        // C'est la difference qui empeche la memoire de descendre, pas le nombre d'ecrans.
        IdlePowerAdvisor.Advise(new[] { 144, 144 }).Should().BeEmpty();
    }

    [Fact]
    public void Un_seul_ecran_ne_dit_rien()
    {
        IdlePowerAdvisor.Advise(new[] { 200 }).Should().BeEmpty();
    }

    [Fact]
    public void Trois_ecrans_melanges_ne_font_qu_UN_seul_constat()
    {
        IdlePowerAdvisor.Advise(new[] { 240, 144, 60 }).Should().HaveCount(1);
    }

    [Fact]
    public void Ce_qui_n_a_PAS_ete_lu_ne_conseille_rien()
    {
        // La regle de tout ce depot. Une liste vide ou nulle veut dire « pas lu », et une frequence
        // a 0 est une lecture ratee, jamais un ecran a 0 Hz.
        IdlePowerAdvisor.Advise(null).Should().BeEmpty();
        IdlePowerAdvisor.Advise(Array.Empty<int>()).Should().BeEmpty();
        IdlePowerAdvisor.Advise(new[] { 200, 0 }).Should().BeEmpty();
    }

    [Fact]
    public void Le_conseil_porte_sa_source_avec_lui()
    {
        // Un conseil sans sa source devient une opinion des qu'il quitte ce fichier.
        IdlePowerAdvisor.Advise(new[] { 200, 144 })
            .Should().OnlyContain(g => g.SourceUrl.StartsWith("http"));
    }
}

// -----------------------------------------------------------------------
// LE CABLAGE. Sans lui, l'advisor serait un instrument de plus que personne n'appelle -- le
// defaut que ce depot a paye cinq fois en deux jours.
// -----------------------------------------------------------------------
public class IdlePowerInScanTests
{
    private static HardwareInventory Machine() => new(
        new CpuInfo("Core i7-14700F", "Intel", 20, 28, IsX3D: false, CcdCount: 1),
        Array.Empty<GpuInfo>(),
        new[] { new MemoryModule("PN", "Kingston", 3800, 4000, DeviceLocator: "Controller0-DIMMA2") },
        new MotherboardInfo("Gigabyte", "B760"),
        new BiosInfo("1.0", null, true),
        ChassisType.Desktop);

    private static SettingsState Etat(params int[] frequences) => new(
        TriState.On, TriState.On, KnownPowerPlans.Balanced.ToString(), TriState.On,
        GameMode: TriState.On, ParkedCoreIndexes: Array.Empty<int>(), Vrr: TriState.On,
        AttachedRefreshHz: frequences.Length == 0 ? null : frequences);

    [Fact]
    public void Le_scan_remonte_le_constat_pour_la_machine_de_reference()
    {
        // 200 et 144 Hz : la configuration exacte de la machine de reference, celle qui tire 55,9 W au repos.
        ScoringRules.Evaluate(Machine(), Etat(200, 144))
            .Should().ContainSingle(f => f.TitleKey == "Finding.Display.MixedRefreshIdlePower");
    }

    [Fact]
    public void Le_constat_ne_promet_AUCUN_chiffre()
    {
        // R-02 rapporte 40 W vers 21,5 W -- sur d'autres machines. L'afficher ici comme un gain
        // serait exactement l'estimation que ce produit a retiree de sa vitrine.
        ScoringRules.Evaluate(Machine(), Etat(200, 144))
            .Where(f => f.TitleKey == "Finding.Display.MixedRefreshIdlePower")
            .Should().OnlyContain(f => f.EstimatedGainPct == null);
    }

    [Fact]
    public void Des_ecrans_alignes_ou_non_lus_ne_remontent_rien()
    {
        ScoringRules.Evaluate(Machine(), Etat(144, 144))
            .Should().NotContain(f => f.TitleKey == "Finding.Display.MixedRefreshIdlePower");
        ScoringRules.Evaluate(Machine(), Etat())
            .Should().NotContain(f => f.TitleKey == "Finding.Display.MixedRefreshIdlePower");
    }
}
