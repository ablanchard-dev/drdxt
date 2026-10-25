using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using DrDxt.GpuNative.Nvml;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

/// <summary>Le ventilateur NVIDIA en mode Expert. Un faux NVML : aucune vraie carte n'est touchée.
/// Le relevé de référence (RTX 4060) : 1 ventilateur, automatique, cible 30 %, plage 30-100.</summary>
public sealed class NvmlVentilateurActionsTests
{
    private sealed class FauxNvml : INvmlNative
    {
        public List<VentilateurNvml> Fans = [new(0, 0, 30, VentilateurNvml.PolitiqueAutomatique, 30, 100)];
        public bool PoseRefusee, RetourSansEffet, Illisible;
        public List<string> Ecritures = [];

        public bool TryLireVentilateurs(int deviceIndex, out IReadOnlyList<VentilateurNvml> ventilateurs)
        { ventilateurs = Illisible ? [] : Fans.ToList(); return !Illisible; }

        public bool TryPoserVentilateur(int deviceIndex, int ventilateur, uint pourcent)
        {
            Ecritures.Add($"poser {ventilateur} {pourcent}");
            if (PoseRefusee) return false;
            Fans[ventilateur] = Fans[ventilateur] with { CiblePct = pourcent, Politique = 1 };
            return true;
        }

        public bool TryRendreVentilateur(int deviceIndex, int ventilateur)
        {
            Ecritures.Add($"rendre {ventilateur}");
            if (!RetourSansEffet) Fans[ventilateur] = Fans[ventilateur] with { CiblePct = 30, Politique = VentilateurNvml.PolitiqueAutomatique };
            return true;
        }

        public bool IsAvailable => true;
        public bool TryGetBar1AndFbBytes(int deviceIndex, out ulong bar1TotalBytes, out ulong fbTotalBytes) { bar1TotalBytes = fbTotalBytes = 0; return false; }
        public bool TryGetPowerUsageMilliwatts(int deviceIndex, out uint milliwatts) { milliwatts = 0; return false; }
        public bool TryGetTemperatureCelsius(int deviceIndex, out uint celsius) { celsius = 0; return false; }
        public bool TryGetGraphicsProcesses(int deviceIndex, out uint[] pids) { pids = []; return false; }
        public bool TryGetClockMhz(int deviceIndex, out uint mhz) { mhz = 0; return false; }
        public bool TryGetClocksEventReasons(int deviceIndex, out ulong reasons) { reasons = 0; return false; }
        public bool TryGetDriverVersion(out string version) { version = ""; return false; }
    }

    [Fact]
    public void Une_vitesse_au_dessus_de_l_automatique_se_pose_et_se_relit()
    {
        var nvml = new FauxNvml();
        new NvmlVentilateurActions(nvml).Poser(0, 60).IsOk.Should().BeTrue();
        nvml.Fans.Single().Should().Match<VentilateurNvml>(v => v.CiblePct == 60 && !v.Automatique);
    }

    [Theory]
    [InlineData(20, "sous le minimum et sous la cible")]
    [InlineData(0, "ventilateur arrêté")]
    public void Jamais_sous_ce_que_la_carte_vise_rien_n_est_ecrit(int pourcent, string cas)
    {
        var nvml = new FauxNvml();
        nvml.Fans[0] = nvml.Fans[0] with { CiblePct = 45 };
        new NvmlVentilateurActions(nvml).Poser(0, pourcent).IsOk.Should().BeFalse(cas);
        new NvmlVentilateurActions(nvml).Poser(0, 40).IsOk.Should().BeFalse("40 % est sous les 45 % que la carte vise");
        nvml.Ecritures.Should().BeEmpty();
    }

    // Un pilote sans nvmlDeviceGetMinMaxFanSpeed ou sans cible lisible : le plancher n'est pas connu, la garde ne peut
    // pas dire qu'une vitesse est au-dessus.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Une_plage_ou_une_cible_non_lue_refuse_d_ecrire(bool minimumLu, bool cibleLue)
    {
        var nvml = new FauxNvml();
        nvml.Fans[0] = nvml.Fans[0] with { MinPct = minimumLu ? 30u : null, CiblePct = cibleLue ? 30u : null };
        var r = new NvmlVentilateurActions(nvml).Poser(0, 60);
        r.IsOk.Should().BeFalse();
        (r.Error?.Message).Should().Contain("not read");
        nvml.Ecritures.Should().BeEmpty();
    }

    [Fact]
    public void Un_ventilateur_deja_en_manuel_n_est_pas_repose()
    {
        var nvml = new FauxNvml();
        nvml.Fans[0] = nvml.Fans[0] with { Politique = 1 };
        new NvmlVentilateurActions(nvml).Poser(0, 80).IsOk.Should().BeFalse();
        nvml.Ecritures.Should().BeEmpty();
    }

    [Fact]
    public void Illisible_rien_n_est_ecrit()
    {
        var nvml = new FauxNvml { Illisible = true };
        new NvmlVentilateurActions(nvml).Poser(0, 80).IsOk.Should().BeFalse();
        nvml.Ecritures.Should().BeEmpty();
    }

    [Fact]
    public void Une_pose_refusee_rend_l_automatique()
    {
        var nvml = new FauxNvml { PoseRefusee = true };
        var r = new NvmlVentilateurActions(nvml).Poser(0, 80);
        r.IsOk.Should().BeFalse();
        nvml.Ecritures.Should().Equal("poser 0 80", "rendre 0");
        nvml.Fans.Single().Automatique.Should().BeTrue();
    }

    [Fact]
    public void Le_retour_se_prouve_par_la_politique_relue()
    {
        var nvml = new FauxNvml();
        var actions = new NvmlVentilateurActions(nvml);
        actions.Poser(0, 70).IsOk.Should().BeTrue();
        actions.Rendre(0).IsOk.Should().BeTrue();
        nvml.Fans.Single().Automatique.Should().BeTrue();

        actions.Poser(0, 70).IsOk.Should().BeTrue();
        nvml.RetourSansEffet = true;
        var rate = actions.Rendre(0);
        rate.IsOk.Should().BeFalse("le pilote a dit oui mais la politique relue est encore manuelle (bug signalé en 2022)");
        rate.Error!.Value.Code.Should().Be(ErrorCodes.TweakPartiallyApplied);
    }
}
