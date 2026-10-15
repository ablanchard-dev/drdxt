using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Le décalage de tension écrit
// par le seul helper. Une fausse boîte aux lettres joue le processeur : rien ne touche la machine.
public sealed class PawnIoTensionTests
{
    /// <summary>La boîte aux lettres OC : une valeur par plan ; une écriture refusée (Undervolt Protection) ne change rien.</summary>
    private sealed class FausseBoite(bool verrouillee = false, int refuseLePlan = -1, int origineMv = 0, int ecrituresMaxCoeurs = int.MaxValue,
        int muetALaPremiereEcritureDuPlan = -1)
    {
        private bool _muetDeja;
        private int _ecrituresCoeurs;
        public readonly Dictionary<int, ulong> Plans = new()
        {
            [TensionCpu.PlanCoeurs] = TensionCpu.CommandeEcriture(TensionCpu.PlanCoeurs, origineMv, 0)!.Value & 0xFFFFFFFF,
            [TensionCpu.PlanCache] = TensionCpu.CommandeEcriture(TensionCpu.PlanCache, origineMv, 0)!.Value & 0xFFFFFFFF,
        };
        public readonly List<ulong> Ecrites = [];
        public ulong? Echanger(ulong commande)
        {
            var plan = (int)((commande >> 40) & 0xFF);
            var code = (commande >> 32) & 0xFF;
            if (code == 0x11)
            {
                Ecrites.Add(commande);
                if (plan == TensionCpu.PlanCoeurs && ++_ecrituresCoeurs > ecrituresMaxCoeurs) return 0; // écriture perdue
                if (!verrouillee && plan != refuseLePlan) Plans[plan] = commande & 0xFFFFFFFF;
                if (plan == muetALaPremiereEcritureDuPlan && !_muetDeja) { _muetDeja = true; return null; } // appliquée, réponse perdue
                return 0;
            }
            return code == 0x10 && Plans.TryGetValue(plan, out var v) ? v : 0x0000000600000000UL;
        }
    }

    private static PawnIoCpuPowerActions Actions(FausseBoite boite, bool sgx = false, Dictionary<int, ulong>? origines = null)
    {
        origines ??= [];
        return new(() => null, _ => false, boiteOc: boite.Echanger, sgx: () => sgx,
            origineTension: (plan, lu) => origines.TryGetValue(plan, out var o) ? o : origines[plan] = lu);
    }

    [Fact]
    public void Le_decalage_s_ecrit_sur_les_coeurs_et_le_cache_puis_se_relit()
    {
        var boite = new FausseBoite();
        Actions(boite).SetVoltageOffset(-50).IsOk.Should().BeTrue();
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCoeurs]).Should().Be(-50);
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCache]).Should().Be(-50);
    }

    [Fact]
    public void Un_processeur_avec_SGX_est_refuse_sans_rien_ecrire()
    {
        var boite = new FausseBoite();
        var r = Actions(boite, sgx: true).SetVoltageOffset(-50);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("SGX");
        boite.Ecrites.Should().BeEmpty();
    }

    [Fact]
    public void Undervolt_Protection_qui_ignore_l_ecriture_est_dit_jamais_un_succes()
    {
        var r = Actions(new FausseBoite(verrouillee: true)).SetVoltageOffset(-50);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("Undervolt Protection");
    }

    [Fact]
    public void Le_cache_refuse_apres_les_coeurs_les_coeurs_sont_remis()
    {
        var boite = new FausseBoite(refuseLePlan: TensionCpu.PlanCache);
        Actions(boite).SetVoltageOffset(-50).IsOk.Should().BeFalse();
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCoeurs]).Should().Be(0, "un plan à moitié posé est rendu");
    }

    [Fact]
    public void Hors_bornes_rien_n_est_ecrit()
    {
        var boite = new FausseBoite();
        Actions(boite).SetVoltageOffset(20).IsOk.Should().BeFalse("monter la tension : jamais");
        Actions(boite).SetVoltageOffset(-200).IsOk.Should().BeFalse("sous le plancher : jamais");
        boite.Ecrites.Should().BeEmpty();
    }

    // Le décalage est RELATIF à la tension que le BIOS (ou ThrottleStop) avait mise à ce
    // démarrage. Un undervolt déjà en place à -80 mV : -30 donne -110, et 0 rend exactement -80, jamais 0 (qui MONTERAIT la tension
    // et effacerait le réglage du joueur). Le total ne descend jamais sous -125 mV.
    [Fact]
    public void Le_decalage_est_relatif_a_l_origine_du_demarrage_et_0_rend_exactement_l_origine()
    {
        var boite = new FausseBoite(origineMv: -80);
        var origines = new Dictionary<int, ulong>();
        Actions(boite, origines: origines).SetVoltageOffset(-30).IsOk.Should().BeTrue();
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCoeurs]).Should().Be(-110);

        Actions(boite, origines: origines).SetVoltageOffset(0).IsOk.Should().BeTrue();
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCoeurs]).Should().Be(-80, "l'undervolt du joueur, rendu tel quel");
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCache]).Should().Be(-80);
    }

    [Fact]
    public void Le_total_ne_descend_jamais_sous_le_plancher()
    {
        var boite = new FausseBoite(origineMv: -80);
        var r = Actions(boite).SetVoltageOffset(-50);
        r.IsOk.Should().BeFalse("-80 - 50 = -130 : sous -125 mV");
        boite.Ecrites.Should().BeEmpty();
    }

    // Une origine POSITIVE (surtension posée par un autre outil) n'est jamais prise comme base : DrDXT ne
    // pose rien et le dit, au lieu de risquer un retour qui ne saurait pas réécrire une valeur positive.
    [Fact]
    public void Une_origine_positive_n_est_jamais_prise_comme_base()
    {
        var boite = new FausseBoite();
        boite.Plans[TensionCpu.PlanCoeurs] = 0x01400000UL; // +10 pas, environ +10 mV
        var r = Actions(boite).SetVoltageOffset(-20);
        r.IsOk.Should().BeFalse();
        boite.Ecrites.Should().BeEmpty();
    }

    // Le cache refusé après des cœurs acceptés, et le retour des cœurs perdu. Jamais « rien n'a changé » :
    // le retour est relu, et un retour raté se dit par son propre code (redémarrer rend la tension).
    [Fact]
    public void Un_retour_perdu_n_est_jamais_dit_rien_n_a_change()
    {
        var boite = new FausseBoite(refuseLePlan: TensionCpu.PlanCache, ecrituresMaxCoeurs: 1);
        var r = Actions(boite).SetVoltageOffset(-50);
        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(ErrorCodes.TensionNonRendue);
    }

    // L'écriture du cache est appliquée mais sa réponse se perd (bit occupé au-delà de 50 ms) : le cache
    // est rendu aussi, pas seulement les plans relus.
    [Fact]
    public void Une_ecriture_appliquee_dont_la_reponse_se_perd_est_rendue_aussi()
    {
        var boite = new FausseBoite(muetALaPremiereEcritureDuPlan: TensionCpu.PlanCache);
        Actions(boite).SetVoltageOffset(-50).IsOk.Should().BeFalse();
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCache]).Should().Be(0, "le cache rendu à son avant");
        TensionCpu.DecalageMv(boite.Plans[TensionCpu.PlanCoeurs]).Should().Be(0, "les cœurs aussi");
    }
}
