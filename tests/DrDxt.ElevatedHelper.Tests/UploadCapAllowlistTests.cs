using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// 24/09/2026 — le nom d'exe finit dans une commande PowerShell executee elevee. La regex de
// l'allowlist est la seule chose qui rend cette interpolation sure : ces cas la gravent.
public sealed class UploadCapAllowlistTests
{
    private readonly OpAllowlist _allow = new();

    [Theory]
    [InlineData("cod.exe")]
    [InlineData("cs2.exe")]
    [InlineData("VALORANT-Win64-Shipping.exe")]
    // 24/09/2026 — trouve par la preuve bout-en-bout : « PING.EXE » etait refuse, la regex
    // n'acceptait l'extension qu'en minuscules. Windows ne fait pas la difference.
    [InlineData("COD.EXE")]
    public void Un_nom_d_exe_nu_et_un_plafond_borne_passent(string exe)
        => _allow.Validate(new ApplyUploadCapOp(exe, 400_000_000)).IsOk.Should().BeTrue();

    [Theory]
    [InlineData(@"cod.exe'; Remove-Item C:\ -Recurse; '.exe")]
    [InlineData(@"C:\Games\cod.exe")]
    [InlineData("cod .exe")]
    [InlineData("cod.exe\"")]
    [InlineData("$(calc).exe")]
    [InlineData("cod.bat")]
    [InlineData("")]
    // Le « $ » de .NET accepte un saut de ligne final : l'ancre doit être « \z ».
    [InlineData("cod.exe\n")]
    public void Tout_ce_qui_n_est_pas_un_nom_d_exe_nu_est_refuse(string exe)
        => _allow.Validate(new ApplyUploadCapOp(exe, 400_000_000)).IsOk.Should().BeFalse();

    [Fact]
    public void Un_nom_trop_long_est_refuse()
        => _allow.Validate(new ApplyUploadCapOp(new string('a', 65) + ".exe", 400_000_000)).IsOk.Should().BeFalse();

    [Theory]
    [InlineData(999_999L)]
    [InlineData(10_000_000_001L)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Un_plafond_hors_bornes_est_refuse(long cap)
        => _allow.Validate(new ApplyUploadCapOp("cod.exe", cap)).IsOk.Should().BeFalse();

    // 26/09/2026 : les deux bornes elles-memes sont permises. AjustementDuPlafond descend jusqu'a
    // 1 Mbit/s pile : une borne devenue exclusive ferait refuser son plancher en pleine partie.
    [Theory]
    [InlineData(1_000_000L)]
    [InlineData(10_000_000_000L)]
    public void Les_bornes_exactes_passent(long cap)
        => _allow.Validate(new ApplyUploadCapOp("cod.exe", cap)).IsOk.Should().BeTrue();

    [Fact]
    public void L_inverse_est_toujours_permis()
        => _allow.Validate(new RemoveUploadCapOp()).IsOk.Should().BeTrue();
}
