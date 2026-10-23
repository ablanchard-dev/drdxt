using System.Text.RegularExpressions;
using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

/// <summary>
/// Les modules AMD de PawnIO savent écrire (ioctl_write_msr, ioctl_write_smu_register, ioctl_send_smu_command) : la lecture
/// seule vient de PawnIoAmd. Ce test lit son source : chaque ioctl qu'il nomme est dans la liste fermée, aucun de la liste
/// n'écrit ni n'envoie une commande choisie, et aucun autre fichier ne charge un module AMD.
/// </summary>
public sealed class PawnIoAmdLectureSeuleTests
{
    private static string Racine()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(d.FullName, "DrDXT.sln"))) d = d.Parent!;
        return d.FullName;
    }

    private static readonly string Source = File.ReadAllText(Path.Combine(Racine(), "src", "DrDxt.GpuNative", "PawnIo", "PawnIoAmd.cs"));

    [Fact]
    public void Chaque_ioctl_nomme_est_dans_la_liste_fermee()
        => Regex.Matches(Source, "\"(ioctl_[a-z0-9_]+)\"").Select(m => m.Groups[1].Value).Distinct()
            .Should().BeSubsetOf(PawnIoAmd.IoctlsAppeles).And.HaveCount(PawnIoAmd.IoctlsAppeles.Count);

    // Les écritures des modules AMDReset, AMDFamily17 et RyzenSMU de la publication 0.2.11, relevées dans leur source.
    [Fact]
    public void Aucun_ioctl_de_la_liste_n_ecrit_ni_n_envoie_de_commande()
        => PawnIoAmd.IoctlsAppeles.Should().NotIntersectWith(["ioctl_write_msr", "ioctl_write_smu_register", "ioctl_send_smu_command"])
            .And.NotContain(io => io.Contains("write") || io.Contains("send"));

    private const string ChargeUnModuleAmd = "\"(AMDReset|AMDFamily17|RyzenSMU)\"";

    [Fact]
    public void Seul_PawnIoAmd_charge_un_module_AMD()
    {
        var lus = Directory.EnumerateFiles(Path.Combine(Racine(), "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToDictionary(f => f, File.ReadAllText);
        bool EstPawnIoAmd(string chemin) => Path.GetFileName(chemin) == "PawnIoAmd.cs";

        lus.Should().HaveCountGreaterThan(100, "témoin : le parcours lit bien les sources");
        lus.Single(f => EstPawnIoAmd(f.Key)).Value.Should().MatchRegex(ChargeUnModuleAmd, "témoin : le motif reconnaît le chargement de PawnIoAmd");
        lus.Where(f => !EstPawnIoAmd(f.Key) && Regex.IsMatch(f.Value, ChargeUnModuleAmd)).Select(f => Path.GetFileName(f.Key)).Should().BeEmpty();
    }
}
