using DrDxt.GpuNative.PawnIo;
using FluentAssertions;

namespace DrDxt.GpuNative.Tests;

// Les ventilateurs de la carte mère se lisent par la puce Super I/O
// Nuvoton NCT6687D (identifiant 0xD592, port 0x4E, base EC 0xA20) : régime 16 bits gros-boutiste en 0x140 + 2i, cycle PWM en
// 0x160 + i (sur 255). Protocole : LibreHardwareMonitor (Nct677X.cs, LpcPort.cs), module PawnIO LpcIO 0.2.11. Le faux ci-dessous
// joue la puce : le protocole se prouve sans la machine.
public sealed class PawnIoSuperIoTests
{
    /// <summary>Une NCT6687D sur un emplacement, sa mémoire EC, et la trace de chaque geste.</summary>
    private sealed class FausseCarte(ushort puce = 0xD592, ulong emplacement = 1) : PawnIoSuperIo.IPorts
    {
        public readonly Dictionary<ushort, byte> Ec = [];
        public readonly List<string> Trace = [];
        private ulong _slot;
        private bool _config;
        private byte _ldn;
        private byte _page = 0xFF, _index;
        private const ulong Base = 0xA20;

        public ulong? EmplacementRefuse { get; init; }
        public void ChoisirEmplacement(ulong slot)
        {
            if (slot == EmplacementRefuse) throw new InvalidOperationException("refusé par le module");
            _slot = slot; Trace.Add($"slot {slot}");
        }
        public void Ecrire(ulong port, ulong valeur)
        {
            Trace.Add($"out {port:X} {valeur:X}");
            var registre = _slot == 0 ? 0x2EUL : 0x4EUL;
            if (port == registre && valeur == 0x87) _config = true;
            else if (port == registre && valeur == 0xAA) _config = false;
            else if (port == Base + 4) _page = (byte)valeur;
            else if (port == Base + 5) _index = (byte)valeur;
        }
        public ulong Lire(ulong port)
        {
            if (port == Base + 4) return _page;
            if (port == Base + 6) return Ec.TryGetValue((ushort)(_page << 8 | _index), out var v) ? v : (byte)0;
            return 0xFF;
        }
        public ulong LireMotConfig(ulong registre)
        {
            if (!_config || _slot != emplacement) return 0xFFFF;
            return registre switch { 0x20 => puce, 0x60 when _ldn == 0x0B => Base, _ => 0 };
        }
        public void EcrireConfig(ulong registre, ulong valeur) { if (registre == 0x07) _ldn = (byte)valeur; Trace.Add($"cfg {registre:X} {valeur:X}"); }
        public void TrouverBars() => Trace.Add("bars");
        public bool EnConfig => _config;
    }

    private static FausseCarte Reference()
    {
        var c = new FausseCarte();
        void Regime(int i, int trMin) { c.Ec[(ushort)(0x140 + 2 * i)] = (byte)(trMin >> 8); c.Ec[(ushort)(0x141 + 2 * i)] = (byte)trMin; }
        Regime(0, 1265); Regime(5, 1210); Regime(6, 1259);
        c.Ec[0x160] = 140; c.Ec[0x165] = 53; c.Ec[0x166] = 81;
        return c;
    }

    // Un emplacement refusé par le module n'empêche pas de lire l'autre (0x4E, celui de la machine de référence).
    [Fact]
    public void Un_emplacement_refuse_laisse_lire_l_autre()
    {
        var c = new FausseCarte { EmplacementRefuse = 0 };
        void Regime(int i, int trMin) { c.Ec[(ushort)(0x140 + 2 * i)] = (byte)(trMin >> 8); c.Ec[(ushort)(0x141 + 2 * i)] = (byte)trMin; }
        Regime(0, 1265);

        PawnIoSuperIo.Lire(c).Should().NotBeNull();
        c.EnConfig.Should().BeFalse();
    }

    [Fact]
    public void Les_ventilateurs_de_la_NCT6687D_se_lisent_regime_et_cycle()
    {
        var lus = PawnIoSuperIo.Lire(Reference())!;

        lus.Puce.Should().Be(0xD592);
        lus.Ventilateurs[0].Should().Be(new PawnIoSuperIo.Ventilateur(0, 1265, 55), "140/255 = 55 %");
        lus.Ventilateurs[5].TrMin.Should().Be(1210);
        lus.Ventilateurs[1].TrMin.Should().Be(0, "en-tête sans ventilateur");
        lus.Ventilateurs.Should().HaveCount(8);
    }

    [Fact]
    public void La_sortie_de_configuration_est_toujours_envoyee_et_la_page_EC_rendue()
    {
        var carte = Reference();
        PawnIoSuperIo.Lire(carte);

        carte.EnConfig.Should().BeFalse("0xAA envoyé après la lecture de la configuration");
        carte.Trace.Last(t => t.StartsWith("out A24")).Should().Be("out A24 FF", "la page EC rendue aux autres lecteurs");
        carte.Trace.Should().NotContain(t => t.StartsWith("cfg") && !t.StartsWith("cfg 7 B"), "seul le choix du périphérique logique est écrit");
    }

    [Fact]
    public void Une_autre_puce_ou_aucune_ne_fait_rien_lire()
    {
        PawnIoSuperIo.Lire(new FausseCarte(puce: 0xC803)).Should().BeNull("une puce ITE : son protocole n'est pas celui-là, rien n'est deviné");
        PawnIoSuperIo.Lire(new FausseCarte(puce: 0xFFFF)).Should().BeNull();
    }

    [Theory]
    [InlineData(0xFF, 0xFF, 0)]
    [InlineData(0xFF, 0xF8, 0)]
    [InlineData(0x04, 0xF1, 1265)]
    public void Un_regime_hors_mesure_se_lit_zero(byte haut, byte bas, int attendu)
        => PawnIoSuperIo.Regime(haut, bas).Should().Be(attendu);
}

/// <summary>La vraie carte : une MSI à puce NCT6687, PawnIO installé, session élevée ; sinon ignoré avec sa raison.</summary>
public sealed class PawnIoSuperIoSurLaVraieCarteTests
{
    [FactSiCarteMsiLisible]
    [Trait("Category", "Machine")]
    public void Les_ventilateurs_de_la_vraie_carte_se_lisent_et_l_un_d_eux_tourne()
    {
        var lus = PawnIoSuperIo.Lire();

        lus.Should().NotBeNull();
        PawnIoSuperIo.Nct6687(lus!.Puce).Should().BeTrue();
        lus.Ventilateurs.Should().Contain(v => v.TrMin > 200, "un PC allumé a au moins un ventilateur qui tourne");
        lus.Ventilateurs.Should().OnlyContain(v => v.PwmPourcent >= 0 && v.PwmPourcent <= 100 && v.TrMin < 10_000);
    }
}

public sealed class FactSiCarteMsiLisibleAttribute : FactAttribute
{
    public FactSiCarteMsiLisibleAttribute()
    {
        var admin = new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        using var bios = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var fabricant = bios?.GetValue("BaseBoardManufacturer") as string ?? "";
        if (!PawnIoIntel.Installe()) Skip = "PawnIO n'est pas installé sur cette machine";
        else if (!admin) Skip = "session sans droits administrateur : PawnIO ne répond qu'à un processus élevé";
        else if (!fabricant.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase)) Skip = $"carte « {fabricant} » : la puce NCT6687 est celle des cartes MSI";
    }
}
