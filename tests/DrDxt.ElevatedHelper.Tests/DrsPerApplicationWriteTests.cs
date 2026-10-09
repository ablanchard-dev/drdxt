using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.ElevatedHelper.Execution.NvApi;
using DrDxt.GpuNative.NvApi;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// -----------------------------------------------------------------------
// L2 — l'ecriture des reglages 3D par JEU.
//
// La hierarchie du driver est Application > Global courant > Base (PG-12072-001 p.2). Une
// ecriture globale peut donc ne jamais atteindre un jeu qui possede son propre profil, pendant
// que le produit annonce un reglage applique. C'est le trou que ce lot ferme.
//
// Ce que ces tests gardent, et qui casse en silence sinon :
//   * un `true` natif dit que le driver a ACCEPTE L'APPEL, pas que la valeur a atterri ;
//   * une relecture qui echoue n'est pas un succes -- « applique mais non confirme » est
//     exactement ce que ce code existe pour empecher ;
//   * un executable sans profil ne doit pas faire retomber l'ecriture sur le profil de base,
//     ce qui ecrirait pour toute la machine ce que l'appelant voulait pour un seul jeu.
// -----------------------------------------------------------------------
public sealed class DrsPerApplicationWriteTests
{
    private const string GamePath = @"C:\Games\Call of Duty\cod.exe";
    private const uint FrlFps = 0x10835002u;

    /// <summary>
    /// Doublure du bord natif. Elle modelise le piege central : `TrySet…` peut reussir pendant
    /// que la relecture rend autre CHOSE — c'est exactement ce qui a ete observe sur une vraie
    /// RTX 4060 pour la limite de puissance (110 % demandes, 100 % tenus).
    /// </summary>
    private sealed class FakeNative : INvApiNative
    {
        public bool IsAvailable { get; set; } = true;
        public bool WriteSucceeds { get; set; } = true;
        public bool ReadBackSucceeds { get; set; } = true;
        public uint ReadBackValue { get; set; }
        public bool FollowTheWrite { get; set; } = true;

        public string? WrittenPath { get; private set; }
        public uint? WrittenValue { get; private set; }
        public int GlobalWrites { get; private set; }
        public int ReadBackCalls { get; private set; }

        public bool TrySetDrsSettingForApplication(string applicationPath, uint settingId, uint value)
        {
            if (!WriteSucceeds) return false;
            WrittenPath = applicationPath;
            WrittenValue = value;
            if (FollowTheWrite) ReadBackValue = value;
            return true;
        }

        public bool TryReadDrsSettingForApplication(string applicationPath, uint settingId, out DrsReading reading)
        {
            ReadBackCalls++;
            reading = new DrsReading(ReadBackValue, 0, 0, 0);
            return ReadBackSucceeds;
        }

        // Doit rester a zero dans tout ce fichier : une ecriture par jeu ne doit jamais retomber
        // sur le profil de base.
        public bool TrySetDrsGlobalSetting(uint settingId, uint value) { GlobalWrites++; return true; }

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
    public void The_write_targets_the_game_executable_and_never_the_base_profile()
    {
        var native = new FakeNative();

        var r = Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        r.IsOk.Should().BeTrue();
        native.WrittenPath.Should().Be(GamePath);
        native.WrittenValue.Should().Be(197u);
        native.GlobalWrites.Should().Be(0,
            "ecrire pour toute la machine ce que l'appelant voulait pour un jeu serait le defaut "
            + "exact que cette portee existe pour corriger");
    }

    [Fact]
    public void An_accepted_call_whose_value_did_NOT_land_is_reported_as_a_failure()
    {
        // Le test du lot. Observe sur une vraie RTX 4060 pour la limite de puissance : le driver
        // accepte l'appel, garde une autre valeur, et l'ecriture rend `true`. Une ecriture qui
        // annonce un succes qu'elle n'a pas obtenu est pire qu'une ecriture qui echoue.
        var native = new FakeNative { FollowTheWrite = false, ReadBackValue = 60u };

        var r = Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("197");
        r.Error!.Value.Message.Should().Contain("60");
    }

    [Fact]
    public void The_read_back_really_happens_otherwise_the_check_checks_nothing()
    {
        // Controle positif : sans lui, les deux tests ci-dessus passeraient aussi bien si la
        // relecture n'etait jamais appelee et que la comparaison portait sur la valeur ecrite.
        var native = new FakeNative();

        Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        native.ReadBackCalls.Should().Be(1);
    }

    [Fact]
    public void A_read_back_that_fails_is_not_a_success()
    {
        // « Applique mais non confirme » est exactement l'affirmation qu'on refuse de faire.
        var native = new FakeNative { ReadBackSucceeds = false };

        var r = Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("NOT confirmed");
    }

    [Fact]
    public void A_game_without_a_profile_of_its_own_fails_and_says_no_profile_was_created()
    {
        // Le driver rend NVAPI_EXECUTABLE_NOT_FOUND. En fabriquer un serait une DECISION
        // separee : le produit ecrirait une entree permanente dans la base du driver pour un jeu
        // peut-etre jamais lance, et le revert devrait savoir SUPPRIMER un profil.
        var native = new FakeNative { WriteSucceeds = false };

        var r = Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Message.Should().Contain("No profile was created");
        native.GlobalWrites.Should().Be(0, "et surtout pas de repli silencieux sur le profil de base");
    }

    [Fact]
    public void An_empty_path_is_refused_before_anything_native_runs()
    {
        var native = new FakeNative();

        var r = Actions(native).SetSettingForApplication("   ", FrlFps, 197u);

        r.IsOk.Should().BeFalse();
        native.WrittenPath.Should().BeNull();
        native.ReadBackCalls.Should().Be(0);
    }

    [Fact]
    public void Without_NVAPI_nothing_is_attempted()
    {
        var native = new FakeNative { IsAvailable = false };

        var r = Actions(native).SetSettingForApplication(GamePath, FrlFps, 197u);

        r.IsOk.Should().BeFalse();
        native.WrittenPath.Should().BeNull();
    }
}
