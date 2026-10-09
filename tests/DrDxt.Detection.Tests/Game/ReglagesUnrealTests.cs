using DrDxt.Core;
using DrDxt.Detection.Game;
using FluentAssertions;

namespace DrDxt.Detection.Tests.Game;

// 07/10/2026 (P1) : les réglages des jeux Unreal Engine joués sur le PC, lus dans GameUserSettings.ini, jamais écrits.
public sealed class ReglagesUnrealTests : IDisposable
{
    private readonly string _local = Directory.CreateTempSubdirectory("drdxt-unreal-").FullName;
    public void Dispose() => Directory.Delete(_local, true);

    // Extrait du fichier de JeuB relevé sur le PC du mainteneur le 07/10 (mêmes clés, mêmes valeurs).
    private const string JeuB = "[/Script/Engine.GameUserSettings]\nsg.ResolutionQuality=100\nFrameRateLimit=0.000000\n"
        + "NvidiaReflexMode=3\nFullscreenMode=1\nbUseVSync=False\nUpscalingMethod=DLSS\nBackgroundFrameRateLimit=120.000000\n";

    [Theory]
    [InlineData("JeuBClient-Win64-Shipping.exe", "JeuB")]
    [InlineData("FortniteClient-Win64-Shipping.exe", "Fortnite")]
    [InlineData("PioneerGame-Win64-Shipping.exe", "PioneerGame")]
    [InlineData("cod.exe", null)]
    // Un chemin ne remonte pas : seul le nom du fichier compte.
    [InlineData("..\\x-Win64-Shipping.exe", "x")]
    public void Le_projet_se_deduit_de_l_executable_d_un_jeu_unreal(string exe, string? projet)
        => ReglagesUnreal.Projet(exe).Should().Be(projet);

    [Fact]
    public void Le_fichier_se_trouve_sous_le_projet_ou_le_projet_suivi_de_game()
    {
        var jeub = Path.Combine(_local, "JeuB", "Saved", "Config", "WindowsClient");
        var fortnite = Path.Combine(_local, "FortniteGame", "Saved", "Config", "WindowsClient");
        Directory.CreateDirectory(jeub); Directory.CreateDirectory(fortnite);
        File.WriteAllText(Path.Combine(jeub, "GameUserSettings.ini"), JeuB);
        File.WriteAllText(Path.Combine(fortnite, "GameUserSettings.ini"), JeuB);

        ReglagesUnreal.Trouver("JeuBClient-Win64-Shipping.exe", _local)!.DirectoryName.Should().Be(jeub);
        ReglagesUnreal.Trouver("FortniteClient-Win64-Shipping.exe", _local)!.DirectoryName.Should().Be(fortnite);
        ReglagesUnreal.Trouver("cod.exe", _local).Should().BeNull();
    }

    [Fact]
    public void Les_valeurs_se_disent_comme_le_jeu_les_a_ecrites()
    {
        var r = ReglagesUnreal.Lire("JeuB", "GameUserSettings.ini", JeuB, DateTime.Now);
        r.Should().Be(new ReglagesUnrealLus("JeuB", "GameUserSettings.ini", r.ModifieLe, false, 0, 1, "3", "DLSS"));

        var c = ReglagesUnreal.Constat(r, DateTime.Now);
        c.Statut.Should().Be(RepairStatus.Observed);
        c.ValeurCourante.Should().Be("V-Sync coupé ; plafond d'images aucun ; affichage plein écran fenêtré ; Reflex « 3 » (valeur du jeu) ; mise à l'échelle DLSS");
        c.Explication.Should().Contain("n'écrit rien");
    }

    [Fact, Trait("Category", "Machine")] // lit les vrais réglages des jeux Unreal de cette machine (JeuB chez le mainteneur)
    public void Les_jeux_unreal_de_la_machine_se_lisent()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var f = ReglagesUnreal.Trouver("JeuBClient-Win64-Shipping.exe", local);
        f.Should().NotBeNull();
        var c = ReglagesUnreal.Constat(ReglagesUnreal.Lire("JeuB", f!.FullName, File.ReadAllText(f.FullName), f.LastWriteTime), DateTime.Now);
        Console.WriteLine(c.ValeurCourante);
        c.ValeurCourante.Should().NotContain("V-Sync ?").And.NotContain("affichage ?");
    }

    [Fact]
    public void Une_cle_absente_ne_devient_pas_une_valeur()
    {
        var r = ReglagesUnreal.Lire("X", "f", "FullscreenMode=\nbUseVSync=peut-etre\n", DateTime.Now.AddDays(-60));
        r.VSync.Should().BeNull();
        r.ModeFenetre.Should().BeNull();
        ReglagesUnreal.Constat(r, DateTime.Now).ValeurCourante.Should().Contain("V-Sync ?").And.Contain("affichage ?");
        ReglagesUnreal.Constat(r, DateTime.Now).Explication.Should().Contain("peut-être dépassées");
    }
}
