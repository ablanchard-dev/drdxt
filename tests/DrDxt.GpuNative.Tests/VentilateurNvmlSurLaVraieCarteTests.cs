using DrDxt.GpuNative.Nvml;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

/// <summary>La lecture des ventilateurs par NVML, sur la vraie carte NVIDIA de la machine. Lecture
/// seule : rien n'est posé. Relevé de référence (RTX 4060, sonde du 08/10) : 1 ventilateur, automatique, plage 30-100.</summary>
public sealed class VentilateurNvmlSurLaVraieCarteTests
{
    [Fact]
    [Trait("Category", "Machine")] // une carte NVIDIA avec un ventilateur : absente d'un runner CI
    public void Les_ventilateurs_de_la_vraie_carte_se_lisent_en_automatique()
    {
        var nvml = new NvmlNative();
        if (!nvml.IsAvailable) return;

        nvml.TryLireVentilateurs(0, out var ventilateurs).Should().BeTrue();
        ventilateurs.Should().NotBeEmpty();
        ventilateurs.Should().OnlyContain(v => v.MaxPct <= 100 && v.MinPct <= v.MaxPct && v.VitessePct <= 100);
        ventilateurs.Should().OnlyContain(v => v.Automatique, "personne n'a posé de vitesse fixe sur cette carte");
    }

    [Fact]
    public void Sans_carte_lisible_rien_n_est_lu_ni_pose()
    {
        INvmlNative absent = new SansNvml();
        absent.TryLireVentilateurs(0, out var v).Should().BeFalse();
        v.Should().BeEmpty();
        absent.TryPoserVentilateur(0, 0, 80).Should().BeFalse();
        absent.TryRendreVentilateur(0, 0).Should().BeFalse();
    }

    private sealed class SansNvml : INvmlNative
    {
        public bool IsAvailable => false;
        public bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes) { bar1TotalBytes = fbTotalBytes = 0; return false; }
        public bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts) { milliwatts = 0; return false; }
        public bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius) { celsius = 0; return false; }
        public bool TryGetGraphicsProcesses(int deviceIndex, out uint[] pids) { pids = []; return false; }
        public bool TryGetClockMhz(int deviceIndex, out uint mhz) { mhz = 0; return false; }
        public bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons) { reasons = 0; return false; }
        public bool TryGetDriverVersion(out string version) { version = ""; return false; }
    }
}
