using DrDxt.Detection.Hardware;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Hardware;

/// <summary>Seuls les redémarrages demandés par Windows Update (`MoUsoCoreWorker.exe`) comptent. Les champs sont ceux
/// des événements 1074 relevés sur la machine de référence le 06/10/2026.</summary>
public sealed class RedemarragesWindowsUpdateTests
{
    [Theory]
    [InlineData(@"C:\WINDOWS\uus\AMD64\MoUsoCoreWorker.exe (MACHINE)", true)]
    [InlineData(@"C:\Windows\System32\mousocoreworker.exe (PC)", true)]
    [InlineData(@"C:\WINDOWS\servicing\TrustedInstaller.exe (MACHINE)", false)]
    [InlineData(@"C:\WINDOWS\system32\winlogon.exe (MACHINE)", false)]
    [InlineData(@"C:\WINDOWS\SystemApps\Microsoft.Windows.StartMenuExperienceHost_cw5n1h2txyewy\StartMenuExperienceHost.exe (MACHINE)", false)]
    [InlineData(null, false)]
    public void Seul_le_processus_de_Windows_Update_compte(string? processus, bool attendu)
        => RedemarragesWindowsUpdate.DuWindowsUpdate(processus).Should().Be(attendu);
}
