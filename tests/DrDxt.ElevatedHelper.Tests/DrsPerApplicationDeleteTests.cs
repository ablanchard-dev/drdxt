using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>
/// Le revert au profil d'un jeu prouve par la provenance.
///
/// Ce que ces tests gardent :
///   * la preuve est la PROVENANCE relue en session neuve, pas la valeur — une reecriture de la
///     meme valeur la rendrait aussi, en laissant au jeu un reglage « pose par toi » ;
///   * une relecture ratee n'est jamais un succes ;
///   * idempotence sans deviner les codes NVAPI : appel refuse mais etat deja bon = succes ;
///   * rien ne touche le profil de base, rien n'est reecrit.
/// </summary>
public sealed class DrsPerApplicationDeleteTests
{
    private const string GamePath = @"C:\Games\Call of Duty\cod.exe";
    private const uint PreferredPstate = 0x1057EB71u;
    private const uint Current = 0, Global = 1;

    private sealed class FakeNative : INvApiNative
    {
        public bool IsAvailable { get; set; } = true;
        public bool DeleteAccepted { get; set; } = true;
        public bool ReadBackSucceeds { get; set; } = true;
        public uint LocationAfter { get; set; } = Global;
        public uint PredefinedAfter { get; set; }

        public int Deletes { get; private set; }
        public int ReadBacks { get; private set; }
        public int Writes { get; private set; }

        public bool TryDeleteDrsSettingForApplication(string applicationPath, uint settingId)
        {
            Deletes++;
            return DeleteAccepted;
        }

        public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        {
            ReadBacks++;
            reading = new DrsReading(1, LocationAfter, PredefinedAfter, PredefinedAfter);
            return ReadBackSucceeds;
        }

        public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value) { Writes++; return true; }
        public bool TrySetDrsGlobalSetting(uint settingId, uint value) { Writes++; return true; }
        public bool TryGetDrsGlobalSetting(uint settingId, out uint value) { value = 0; return false; }
        public bool TryReadDrsGlobalSetting(uint settingId, out DrsReading reading) { reading = default; return false; }
        public bool TryReadCoreOffsetMhz(int gpuIndex, out int offsetMhz) { offsetMhz = 0; return false; }
        public bool TryReadMemoryOffsetMhz(int gpuIndex, out int offsetMhz) { offsetMhz = 0; return false; }
        public bool TryReadPowerLimitPct(int gpuIndex, out int pct) { pct = 0; return false; }
        public bool TrySetCoreClockOffset(int gpuIndex, int offsetMHz) => false;
        public bool TrySetMemoryClockOffset(int gpuIndex, int offsetMHz) => false;
        public bool TrySetPowerLimitPercent(int gpuIndex, float pct) => false;
    }

    private static INvApiDrsActions Actions(FakeNative native) =>
        (INvApiDrsActions)Activator.CreateInstance(
            typeof(NvApiDrsActions),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            binder: null, args: new object[] { native }, culture: null)!;

    [Fact]
    public void Un_reglage_redevenu_HERITE_est_un_retour_prouve()
    {
        var native = new FakeNative { LocationAfter = Global };

        var r = Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeTrue();
        native.Deletes.Should().Be(1);
        native.ReadBacks.Should().Be(1, "sans relecture, la preuve ne prouve rien");
        native.Writes.Should().Be(0, "un revert par suppression ne reecrit rien, ni au jeu ni au global");
    }

    [Fact]
    public void Un_profil_qui_POSSEDE_encore_le_reglage_n_est_pas_revenu()
    {
        // La valeur peut etre la bonne ; la provenance, non. C'est le cas qu'une relecture de
        // la valeur seule declarerait « revenu ».
        var native = new FakeNative { LocationAfter = Current, PredefinedAfter = 0 };

        var r = Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("InheritedFromGlobalProfile").And.Contain("SetByUser");
    }

    [Fact]
    public void Une_relecture_ratee_n_est_jamais_un_succes()
    {
        var native = new FakeNative { ReadBackSucceeds = false };

        var r = Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.InheritedFromGlobalProfile);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("NOT confirmed");
    }

    [Fact]
    public void Un_appel_refuse_sur_un_etat_DEJA_bon_est_un_succes()
    {
        // Idempotence sans deviner les codes d'erreur : c'est l'etat relu qui decide.
        var native = new FakeNative { DeleteAccepted = false, LocationAfter = Global };

        Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.InheritedFromGlobalProfile).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Une_valeur_LIVREE_par_le_pilote_revient_comme_livree()
    {
        // `[Hypothesis to test]` sur la vraie carte : Delete remet la valeur predefinie.
        var native = new FakeNative { LocationAfter = Current, PredefinedAfter = 1 };

        Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.ShippedWithTheDriver).IsOk.Should().BeTrue();
    }

    [Fact]
    public void Sans_NVAPI_rien_n_est_tente()
    {
        var native = new FakeNative { IsAvailable = false };

        Actions(native).DeleteSettingForApplication(GamePath, PreferredPstate,
            DrsValueOrigin.InheritedFromGlobalProfile).IsOk.Should().BeFalse();
        native.Deletes.Should().Be(0);
    }
}
