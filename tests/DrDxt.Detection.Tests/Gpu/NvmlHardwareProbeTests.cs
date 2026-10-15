using DrDxt.Core;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Gpu;

/// <summary>
/// La garde que le code nommait et que rien ne prouvait.
///
/// `nvmlClocksEventReasonNone` vaut 0x0 : un masque nul lu avec succes veut dire « rien ne bride »,
/// l'information la plus rassurante qu'une mesure GPU puisse rendre. Le commentaire de
/// `NvmlHardwareProbe` interdit de tester « != 0 » — aucun test ne l'y obligeait.
/// </summary>
public class NvmlHardwareProbeTests
{
    private sealed class Nvml(bool disponible, ulong? raisons) : INvmlNative
    {
        public bool IsAvailable => disponible;

        public bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons)
        {
            reasons = raisons ?? 0;
            return raisons is not null;
        }

        public bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes)
        {
            bar1TotalBytes = fbTotalBytes = 0;
            return false;
        }

        public bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts) { milliwatts = 0; return false; }
        public bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius) { celsius = 0; return false; }
        public bool TryGetClockMhz(int deviceIndex, out uint mhz) { mhz = 0; return false; }
        public bool TryGetDriverVersion(out string version) { version = ""; return false; }
    }

    [Fact]
    public void Un_masque_NUL_lu_avec_succes_veut_dire_rien_ne_bride_et_pas_non_lu()
    {
        var lecture = new NvmlHardwareProbe(new Nvml(disponible: true, raisons: 0)).Lire();

        lecture.Raisons.Should().NotBeNull("0 est une vraie valeur NVML : nvmlClocksEventReasonNone");
        lecture.Raisons.Should().Be(ClocksEventReason.Aucune);
    }

    [Fact]
    public void Une_lecture_qui_ECHOUE_rend_null_jamais_rien_ne_bride()
    {
        new NvmlHardwareProbe(new Nvml(disponible: true, raisons: null)).Lire()
            .Raisons.Should().BeNull();
    }

    [Fact]
    public void Un_masque_non_nul_est_rendu_tel_quel()
    {
        new NvmlHardwareProbe(new Nvml(disponible: true, raisons: 0x4)).Lire()
            .Raisons.Should().Be((ClocksEventReason)0x4);
    }

    [Fact]
    public void Sans_NVML_rien_n_est_affirme()
    {
        new NvmlHardwareProbe(new Nvml(disponible: false, raisons: 0)).Lire()
            .Raisons.Should().BeNull();
    }
}
