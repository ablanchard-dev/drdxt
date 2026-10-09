using DrDxt.Core;
using DrDxt.Detection.Gpu.Nvapi;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu.Nvapi;

/// <summary>
/// The reader returned a bare number and threw away the two fields the driver fills on every read.
/// These pin that a caller can now ask where the frame cap came from — the difference between
/// "you set this" and "NVIDIA ships this", which is the whole question on a machine carrying
/// 10218 driver-shipped application profiles.
/// </summary>
public class NvApiLatencyReaderOriginTests
{
    private const uint FrameRateLimitId = 0x10835002;

    private static NvApiLatencyReader Reader(FakeNvApiNative native) =>
        new(native, new NullReg());

    [Fact]
    public void A_user_set_cap_is_reported_as_the_users_own_change()
    {
        var native = new FakeNvApiNative
        {
            IsAvailable = true,
            DrsReadSuccess = true,
            DrsSettingValue = 141,
            DrsSettingLocation = 0,       // current profile
            DrsIsCurrentPredefined = 0,   // not shipped by NVIDIA
        };

        var r = Reader(native).GetFrameRateLimitWithOrigin();

        r.IsOk.Should().BeTrue();
        r.Value.Value.Should().Be(141);
        r.Value.Origin.Origin.Should().Be(DrsValueOrigin.SetByUser);
        r.Value.Origin.IsYourOwnChange.Should().BeTrue();
    }

    [Fact]
    public void A_driver_shipped_cap_is_not_reported_as_the_users_own_change()
    {
        var native = new FakeNvApiNative
        {
            IsAvailable = true,
            DrsReadSuccess = true,
            DrsSettingValue = 141,
            DrsSettingLocation = 0,
            DrsIsCurrentPredefined = 1,   // shipped with the driver
        };

        var r = Reader(native).GetFrameRateLimitWithOrigin();

        r.Value.Origin.Origin.Should().Be(DrsValueOrigin.ShippedWithTheDriver);
        r.Value.Origin.IsYourOwnChange.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_setting_fails_clean_and_invents_no_origin()
    {
        var native = new FakeNvApiNative { IsAvailable = true, DrsReadSuccess = false };

        Reader(native).GetFrameRateLimitWithOrigin().IsOk.Should().BeFalse();
    }

    [Fact]
    public void The_value_matches_the_plain_read_so_the_two_paths_cannot_drift()
    {
        var native = new FakeNvApiNative
        {
            IsAvailable = true, DrsReadSuccess = true, DrsSettingValue = 197,
            DrsSettingLocation = 0, DrsIsCurrentPredefined = 0,
        };
        var reader = Reader(native);

        reader.GetFrameRateLimitWithOrigin().Value.Value
            .Should().Be(reader.GetFrameRateLimit().Value);
    }

    // ---- profil par APPLICATION ------------------------------------------------------
    // Hierarchie reelle du driver : Application > Global courant > Base (PG-12072-001 p.2).
    // Le produit ne lisait que Base et presentait la valeur comme la valeur courante.

    private const string GamePath = @"C:\Program Files (x86)\Call of Duty\_retail_\cod.exe";

    [Fact]
    public void A_game_with_its_own_profile_reports_THAT_value_not_the_global_one()
    {
        var native = new FakeNvApiNative
        {
            IsAvailable = true,
            DrsReadSuccess = true, DrsSettingValue = 1000,        // la valeur GLOBALE
            AppDrsReadSuccess = true, AppDrsSettingValue = 141,   // celle du jeu
            AppDrsSettingLocation = 0, AppDrsIsCurrentPredefined = 0,
        };

        var r = Reader(native).GetFrameRateLimitForApplication(GamePath);

        r.IsOk.Should().BeTrue();
        r.Value.Value.Should().Be(141, "c'est le profil du jeu qui s'applique, pas le global");
        r.Value.Origin.Origin.Should().Be(DrsValueOrigin.SetByUser);
    }

    [Fact]
    public void A_game_without_its_own_profile_never_silently_returns_the_global_value()
    {
        // NVAPI_EXECUTABLE_NOT_FOUND : le jeu n'a aucun profil, et la recherche ne se replie pas
        // sur le profil de base. Rendre la valeur globale ici serait repondre a une question
        // qu'on n'a pas posee. Une fois le profil TROUVE, en revanche, le pilote rend la
        // valeur heritee, etiquetee GLOBAL_PROFILE : c'est le test d'heritage ci-dessous.
        var native = new FakeNvApiNative
        {
            IsAvailable = true,
            DrsReadSuccess = true, DrsSettingValue = 1000,
            AppDrsReadSuccess = false,
        };

        var r = Reader(native).GetFrameRateLimitForApplication(GamePath);

        r.IsOk.Should().BeFalse();
    }

    [Theory]
    [InlineData(true, ErrorCodes.DrsNoGameProfile)]
    [InlineData(false, ErrorCodes.TweakSdkUnavailable)]
    public void No_profile_and_unreadable_profile_are_two_different_answers(bool pasDeProfil, string code)
    {
        // 14/09/2026 : seul « pas de profil » autorise l'appelant a se rabattre sur le global.
        var native = new FakeNvApiNative { IsAvailable = true, AppDrsReadSuccess = false, AppDrsPasDeProfil = pasDeProfil };

        var r = Reader(native).GetDrsForApplication(0x1057EB71, GamePath);

        r.IsOk.Should().BeFalse();
        r.Error!.Value.Code.Should().Be(code);
    }

    [Fact]
    public void A_setting_the_game_profile_does_not_own_comes_back_INHERITED_and_says_so()
    {
        // Mesure du 10/09/2026 sur cod.exe : PREFERRED_PSTATE = 1 avec settingLocation = 1
        // (GLOBAL_PROFILE). La valeur est juste, mais le jeu ne la POSSEDE pas : ecrire « a cote »
        // dans le global ou relire le global ne dit rien de ce que le jeu recevra demain.
        var native = new FakeNvApiNative
        {
            IsAvailable = true,
            AppDrsReadSuccess = true, AppDrsSettingValue = 1, AppDrsSettingLocation = 1,
        };

        var r = Reader(native).GetDrsForApplication(0x1057EB71, GamePath);

        r.IsOk.Should().BeTrue();
        r.Value.Value.Should().Be(1);
        r.Value.Origin.Origin.Should().Be(DrsValueOrigin.InheritedFromGlobalProfile);
        r.Value.Origin.IsYourOwnChange.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("cod.exe")]   // FindApplicationByName exige un chemin COMPLET
    public void A_path_the_driver_cannot_resolve_is_refused_before_the_driver_is_touched(string path)
    {
        var native = new FakeNvApiNative { IsAvailable = true, AppDrsReadSuccess = true, AppDrsSettingValue = 7 };

        Reader(native).GetFrameRateLimitForApplication(path).IsOk.Should().BeFalse();
        native.AppDrsCalls.Should().Be(0, "un chemin non qualifie ne doit pas atteindre le driver");
    }

    private sealed class NullReg : DrDxt.Application.Tweaks.Hardware.IRegistryReader
    {
        public DrDxt.Core.Result<uint?> ReadDword(DrDxt.Core.RegistryHiveKind h, string p, string n)
            => DrDxt.Core.Result<uint?>.Ok(null);
        public DrDxt.Core.Result<string?> ReadString(DrDxt.Core.RegistryHiveKind h, string p, string n)
            => DrDxt.Core.Result<string?>.Ok(null);
        public DrDxt.Core.Result<string?> ReadMultiString(DrDxt.Core.RegistryHiveKind h, string p, string n)
            => DrDxt.Core.Result<string?>.Ok(null);
    }
}
