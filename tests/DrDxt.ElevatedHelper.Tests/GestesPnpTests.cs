using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;
using FluentAssertions;

namespace DrDxt.ElevatedHelper.Tests;

// Trois gestes du guide par pnputil (Microsoft Learn, « PnPUtil command syntax ») : relancer un périphérique en échec,
// retirer un vieux paquet de pilote d'affichage après l'avoir exporté, et le remettre depuis cet export.
public sealed class GestesPnpTests : IDisposable
{
    private readonly string _racine = Path.Combine(Path.GetTempPath(), "drdxt-pnp-" + Guid.NewGuid().ToString("N"));
    private string Inf => Path.Combine(_racine, "INF");
    private string Exports => Path.Combine(_racine, "exports");

    public GestesPnpTests() => Directory.CreateDirectory(Inf);
    public void Dispose() => Directory.Delete(_racine, recursive: true);

    private const string Affichage = "[Version]\r\nSignature = \"$WINDOWS NT$\"\r\nClass       = Display\r\nClassGuid = {4d36e968-e325-11ce-bfc1-08002be10318}\r\n";
    private const string Usb = "[Version]\r\nClass=USB\r\n";

    private sealed class Pnputil(string inf)
    {
        public List<string> Appels { get; } = [];
        public int CodeRelance { get; init; }
        public bool ExportRate { get; init; }
        public bool PaquetEnService { get; init; }

        public (int, string) Run(string[] a)
        {
            Appels.Add(string.Join(" ", a));
            switch (a[0])
            {
                case "/restart-device": return (CodeRelance, "");
                case "/export-driver":
                    if (ExportRate) return (1, "");
                    Directory.CreateDirectory(a[2]);
                    File.Copy(Path.Combine(inf, a[1]), Path.Combine(a[2], "nv_dispi.inf"));
                    return (0, "");
                case "/delete-driver":
                    if (PaquetEnService) return (1, "");
                    File.Delete(Path.Combine(inf, a[1]));
                    return (0, "");
                case "/add-driver": return (0, "");
                default: return (1, "");
            }
        }
    }

    private DateTime _horloge = new(2026, 10, 15, 6, 0, 0, DateTimeKind.Utc);

    private Win32PnpActions Actions(Pnputil p, bool fiable = true, bool? enPanne = true) =>
        new(p.Run, Inf, Exports, d => { Directory.CreateDirectory(d); return Result.Ok(); },
            _ => fiable ? Result.Ok() : Result.Fail(ErrorCodes.SignatureInvalid, "non fiable"), _ => enPanne, () => _horloge = _horloge.AddSeconds(1));

    private string Export(string nom) => Directory.GetDirectories(Exports, nom + "-*").Single();

    [Theory]
    [InlineData(0, true)]
    [InlineData(3010, true)]
    [InlineData(1, false)]
    public void Relancer_un_peripherique_lit_le_code_de_pnputil(int code, bool ok)
    {
        var p = new Pnputil(Inf) { CodeRelance = code };

        Actions(p).RestartDevice(@"USB\VID_046D&PID_C539\5&1A2B&0&3").IsOk.Should().Be(ok);

        p.Appels.Should().Equal(@"/restart-device USB\VID_046D&PID_C539\5&1A2B&0&3");
    }

    // Un périphérique qui marche (ou que Windows ne sait pas dire) ne se relance jamais, quel que soit l'appelant.
    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void Un_peripherique_qui_n_est_pas_en_panne_n_est_pas_relance(bool? enPanne)
    {
        var p = new Pnputil(Inf);

        Actions(p, enPanne: enPanne).RestartDevice(@"PCI\VEN_10DE&DEV_2882\4&2A1C23A&0&0008").IsOk.Should().BeFalse();

        p.Appels.Should().BeEmpty();
    }

    [Fact]
    public void Un_vieux_pilote_d_affichage_est_exporte_puis_retire()
    {
        File.WriteAllText(Path.Combine(Inf, "oem98.inf"), Affichage);
        var p = new Pnputil(Inf);

        Actions(p).RemoveDriverPackage("oem98.inf").IsOk.Should().BeTrue();

        p.Appels.Should().HaveCount(2, "un export, puis un retrait");
        p.Appels[0].Should().StartWith("/export-driver oem98.inf ");
        p.Appels[1].Should().Be("/delete-driver oem98.inf", "jamais /force ni /uninstall : Windows refuse un paquet en service");
        File.Exists(Path.Combine(Export("oem98"), "nv_dispi.inf")).Should().BeTrue("l'annulation repart de cet export");
    }

    [Fact]
    public void Un_paquet_qui_n_est_pas_un_pilote_d_affichage_n_est_pas_touche()
    {
        File.WriteAllText(Path.Combine(Inf, "oem0.inf"), Usb);
        var p = new Pnputil(Inf);

        Actions(p).RemoveDriverPackage("oem0.inf").IsOk.Should().BeFalse();

        p.Appels.Should().BeEmpty();
    }

    [Fact]
    public void Sans_export_rien_n_est_retire()
    {
        File.WriteAllText(Path.Combine(Inf, "oem98.inf"), Affichage);
        var p = new Pnputil(Inf) { ExportRate = true };

        Actions(p).RemoveDriverPackage("oem98.inf").IsOk.Should().BeFalse();

        p.Appels.Should().NotContain(c => c.StartsWith("/delete-driver"));
        File.Exists(Path.Combine(Inf, "oem98.inf")).Should().BeTrue();
    }

    [Fact]
    public void Un_paquet_que_Windows_garde_est_dit_non_retire()
    {
        File.WriteAllText(Path.Combine(Inf, "oem98.inf"), Affichage);

        Actions(new Pnputil(Inf) { PaquetEnService = true }).RemoveDriverPackage("oem98.inf").IsOk.Should().BeFalse();
    }

    [Fact]
    public void Un_paquet_absent_du_magasin_n_est_pas_touche()
    {
        var p = new Pnputil(Inf);

        Actions(p).RemoveDriverPackage("oem7.inf").IsOk.Should().BeFalse();

        p.Appels.Should().BeEmpty();
    }

    [Fact]
    public void Remettre_reinstalle_l_export_d_affichage()
    {
        File.WriteAllText(Path.Combine(Inf, "oem98.inf"), Affichage);
        var p = new Pnputil(Inf);
        var a = Actions(p);
        a.RemoveDriverPackage("oem98.inf");

        a.RestoreDriverPackage("oem98.inf").IsOk.Should().BeTrue();

        p.Appels.Last().Should().Be($"/add-driver {Path.Combine(Export("oem98"), "nv_dispi.inf")} /install");
    }

    // Windows redonne un même oemNN.inf à un autre paquet : le second retrait n'efface pas la sauvegarde du premier, et
    // l'Annuler remet le dernier retiré.
    [Fact]
    public void Un_nom_reutilise_n_efface_pas_la_sauvegarde_d_avant()
    {
        var p = new Pnputil(Inf);
        var a = Actions(p);
        File.WriteAllText(Path.Combine(Inf, "oem12.inf"), Affichage);
        a.RemoveDriverPackage("oem12.inf").IsOk.Should().BeTrue();
        File.WriteAllText(Path.Combine(Inf, "oem12.inf"), Affichage + "; paquet B\r\n");
        a.RemoveDriverPackage("oem12.inf").IsOk.Should().BeTrue();

        var exports = Directory.GetDirectories(Exports, "oem12-*").Order(StringComparer.Ordinal).ToList();
        exports.Should().HaveCount(2);
        a.RestoreDriverPackage("oem12.inf").IsOk.Should().BeTrue();
        p.Appels.Last().Should().Be($"/add-driver {Path.Combine(exports[1], "nv_dispi.inf")} /install");
    }

    // Le helper installe en administrateur ce qu'il trouve dans ce dossier : un dossier qu'un programme sans droits
    // pourrait remplir ferait installer son pilote.
    [Fact]
    public void Un_export_dans_un_dossier_non_fiable_n_est_jamais_installe()
    {
        Directory.CreateDirectory(Path.Combine(Exports, "oem98-20261015000000000"));
        File.WriteAllText(Path.Combine(Exports, "oem98-20261015000000000", "pose.inf"), Affichage);
        var p = new Pnputil(Inf);

        Actions(p, fiable: false).RestoreDriverPackage("oem98.inf").IsOk.Should().BeFalse();

        p.Appels.Should().BeEmpty();
    }

    [Fact]
    public void Sans_export_rien_n_est_remis()
    {
        var p = new Pnputil(Inf);

        Actions(p).RestoreDriverPackage("oem98.inf").IsOk.Should().BeFalse();

        p.Appels.Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"PCI\VEN_10DE&DEV_2882&SUBSYS_88BE1043&REV_A1\4&2A1C23A&0&0008", true)]
    [InlineData(@"USB\VID_046D&PID_C539\5&1A2B&0&3", true)]
    [InlineData(@"SWD\MMDEVAPI\{0.0.0.00000000}.{3B8A6C1E-2C2B-4E0F-9C9B-0E7B2F1C3D4A}", true)]
    [InlineData(@"HID\VID_046D&PID_C539&MI_01&COL01\7&3A&0&0000", true)]
    [InlineData(@"SWD\DAFUPNPPROVIDER\uuid:5b3f1a2c-0000-1000-8000-00e04c000001", true)]
    [InlineData("", false)]
    [InlineData("/restart-device", false)]
    [InlineData(@"USB\VID_046D"" /force", false)]
    [InlineData(@"USB\VID 046D", false)]
    [InlineData("PCI", false)]
    public void L_allowlist_ne_laisse_passer_qu_un_identifiant_de_peripherique(string id, bool permis)
        => new OpAllowlist().Validate(new RestartDeviceOp(id)).IsOk.Should().Be(permis);

    [Theory]
    [InlineData("oem98.inf", true)]
    [InlineData("oem0.inf", true)]
    [InlineData("OEM12.INF", true)]
    [InlineData("nv_dispi.inf", false)]
    [InlineData(@"..\oem98.inf", false)]
    [InlineData("oem98.inf /force", false)]
    [InlineData("oem.inf", false)]
    [InlineData("", false)]
    public void L_allowlist_ne_laisse_passer_qu_un_paquet_publie(string nom, bool permis)
    {
        new OpAllowlist().Validate(new RemoveDriverPackageOp(nom)).IsOk.Should().Be(permis);
        new OpAllowlist().Validate(new RestoreDriverPackageOp(nom)).IsOk.Should().Be(permis);
    }

    [Fact]
    public void Les_trois_gestes_traversent_le_codec()
    {
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new RestartDeviceOp(@"USB\X\1")).Value!).Value
            .Should().Be(new RestartDeviceOp(@"USB\X\1"));
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new RemoveDriverPackageOp("oem98.inf")).Value!).Value
            .Should().Be(new RemoveDriverPackageOp("oem98.inf"));
        PrivilegedOpCodec.Deserialize(PrivilegedOpCodec.Serialize(new RestoreDriverPackageOp("oem98.inf")).Value!).Value
            .Should().Be(new RestoreDriverPackageOp("oem98.inf"));
    }
    // Un dossier non fiable au nom plus récent (posé d'avance dans un %ProgramData%\DrDxt préparé par un compte, la
    // réparation ne descendant pas) passait en tête et bloquait pour toujours la remise du vrai export. Le plus récent est
    // pris parmi les exports fiables : le posé est sauté, jamais installé.
    [Fact]
    public void Un_export_non_fiable_au_nom_plus_recent_ne_bloque_pas_la_remise()
    {
        var p = new Pnputil(Inf);
        File.WriteAllText(Path.Combine(Inf, "oem12.inf"), Affichage);
        Actions(p).RemoveDriverPackage("oem12.inf").IsOk.Should().BeTrue();
        var vrai = Export("oem12");
        var pose = Path.Combine(Exports, "oem12-99999999999999999");
        Directory.CreateDirectory(pose);
        File.WriteAllText(Path.Combine(pose, "pose.inf"), Affichage);
        var a = new Win32PnpActions(p.Run, Inf, Exports, d => { Directory.CreateDirectory(d); return Result.Ok(); },
            d => d == pose ? Result.Fail(ErrorCodes.SignatureInvalid, "non fiable") : Result.Ok(), _ => true, () => _horloge);

        a.RestoreDriverPackage("oem12.inf").IsOk.Should().BeTrue();

        p.Appels.Last().Should().Be($"/add-driver {Path.Combine(vrai, "nv_dispi.inf")} /install");
    }
}
