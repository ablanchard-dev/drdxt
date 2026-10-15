using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Relevé sur une vraie RTX 4060 : demander une limite de puissance à 110 % affichait
/// « limite de puissance appliquée : 110 % » alors qu'une lecture indépendante deux lignes plus bas
/// rendait 100 %. La carte n'a pas de marge de puissance, le pilote a borné en silence, et
/// TrySetPowerLimitPercent a renvoyé vrai parce qu'il dit que l'appel est accepté, pas que la valeur a pris.
///
/// Une écriture qui annonce un succès qu'elle n'a pas obtenu est pire qu'une écriture qui échoue :
/// le produit dirait au joueur « limite de puissance relevée » alors que rien n'a bougé, et
/// l'instantané de retour arrière enregistrerait une valeur que la carte n'a jamais tenue.
///
/// Règle figée ici : une action relit avant d'affirmer.
/// </summary>
public sealed class NvApiTuningWriteIsVerifiedTests
{
    [Fact]
    public void A_power_limit_the_driver_silently_clamped_is_not_reported_as_applied()
    {
        // Le profil exact relevé : 110 demandé, le pilote tient 100, le natif dit « accepté ».
        var native = new StubNative { PowerLimitPct = 100, SetPowerSucceeds = true };

        var r = new NvApiTuningActions(native).SetPowerLimitPercent(0, 110f);

        r.IsOk.Should().BeFalse("le driver a garde 100 %, l'ecriture n'a pas eu lieu");
        r.Error!.Value.Message.Should().Contain("110").And.Contain("100");
    }

    [Fact]
    public void A_power_limit_that_landed_is_reported_as_applied()
    {
        var native = new StubNative { PowerLimitPct = 95, SetPowerSucceeds = true, EchoWrites = true };

        new NvApiTuningActions(native).SetPowerLimitPercent(0, 95f).IsOk.Should().BeTrue();
    }

    [Fact]
    public void A_core_offset_the_driver_did_not_take_is_not_reported_as_applied()
    {
        var native = new StubNative { CoreOffsetMhz = 0, SetCoreSucceeds = true };

        var r = new NvApiTuningActions(native).SetCoreClockOffset(0, 100);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("100").And.Contain("0");
    }

    [Fact]
    public void A_core_offset_that_landed_is_reported_as_applied()
    {
        var native = new StubNative { SetCoreSucceeds = true, EchoWrites = true };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeTrue();
    }

    [Fact]
    public void A_write_that_cannot_be_read_back_is_not_claimed_as_a_success()
    {
        // « Appliqué mais non confirmé » n'est pas un succès. Le dire est tout l'enjeu.
        var native = new StubNative { SetCoreSucceeds = true, EchoWrites = true, ReadCoreSucceeds = false };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeFalse();
    }

    [Fact]
    public void A_write_the_native_boundary_refuses_still_fails_as_before()
    {
        var native = new StubNative { SetCoreSucceeds = false };

        new NvApiTuningActions(native).SetCoreClockOffset(0, 100).IsOk.Should().BeFalse();
    }

    // Le pilote ramène une demande sous son minimum à ce minimum, l'écrit et rend « accepté » : la relecture diffère, et
    // l'origine doit être reposée (sinon la carte reste bridée jusqu'au redémarrage, sans rien pour la rendre).
    [Fact]
    public void Une_limite_que_le_pilote_ramene_a_son_minimum_est_rendue_a_l_origine()
    {
        var native = new StubNative { PowerLimitPct = 100, SetPowerSucceeds = true, EchoWrites = true, PowerMin = 70 };

        var r = new NvApiTuningActions(native).SetPowerLimitPercent(0, 60f);

        r.IsOk.Should().BeFalse();
        native.PowerLimitPct.Should().Be(100, "l'origine est reposée");
    }

    [Fact]
    public void Un_decalage_que_le_pilote_arrondit_est_rendu_a_l_origine()
    {
        var native = new StubNative { CoreOffsetMhz = 15, SetCoreSucceeds = true, EchoWrites = true, CorePas = 15 };

        var r = new NvApiTuningActions(native).SetCoreClockOffset(0, 50);

        r.IsOk.Should().BeFalse();
        native.CoreOffsetMhz.Should().Be(15, "l'origine est reposée");
    }

    [Fact]
    public void Une_origine_qui_ne_se_repose_pas_est_dite_partielle()
    {
        var native = new StubNative { PowerLimitPct = 100, SetPowerSucceeds = true, EchoWrites = true, PowerMin = 70, PowerMinFige = true };

        var r = new NvApiTuningActions(native).SetPowerLimitPercent(0, 60f);

        r.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
        r.Error.Value.Message.Should().Contain("70").And.Contain("100");
    }

    private sealed class StubNative : INvApiNative
    {
        public bool IsAvailable => true;
        public bool EchoWrites { get; set; }          // le driver prend la valeur telle quelle
        public bool ReadCoreSucceeds { get; set; } = true;
        public bool ReadPowerSucceeds { get; set; } = true;
        public bool SetCoreSucceeds { get; set; }
        public bool SetPowerSucceeds { get; set; }
        public int CoreOffsetMhz { get; set; }
        public int PowerLimitPct { get; set; }
        public int PowerMin { get; set; }             // le pilote ramène une demande plus basse à ce minimum
        public bool PowerMinFige { get; set; }        // après une première écriture, plus rien ne bouge
        public int CorePas { get; set; } = 1;         // le pilote arrondit le décalage à ce pas
        private bool _fige;

        public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz)
        { offsetMhz = CoreOffsetMhz; return ReadCoreSucceeds; }

        public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz)
        { offsetMhz = 0; return true; }

        public bool TryReadPowerLimitPct(int gpuIndex, out int powerLimitPct)
        { powerLimitPct = PowerLimitPct; return ReadPowerSucceeds; }

        public bool TrySetCoreClockOffset(int gpuIndex, int offsetMhz)
        { if (SetCoreSucceeds && EchoWrites) CoreOffsetMhz = offsetMhz / CorePas * CorePas; return SetCoreSucceeds; }

        public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMhz) => false;

        public bool TrySetPowerLimitPercent(int gpuIndex, float pct)
        {
            if (SetPowerSucceeds && EchoWrites && !_fige) PowerLimitPct = Math.Max(PowerMin, (int)MathF.Round(pct));
            _fige = PowerMinFige;
            return SetPowerSucceeds;
        }

        public bool TrySetDrsGlobalSetting(uint settingId, uint value) => false;
        public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value) => false;
        public bool TryGetDrsGlobalSetting(uint settingId, out uint value) { value = 0; return false; }
        public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading) { reading = default; return false; }
        public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        { reading = default; return false; }
    }
}
