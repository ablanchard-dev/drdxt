using DrDxt.Core;
using FluentAssertions;

namespace DrDxt.Core.Tests;

// L'écran de verrouillage (LockApp.exe, sous Windows\SystemApps) et
// powershell.exe (sous System32\WindowsPowerShell\v1.0) étaient pris pour des parties : le filtre ne regardait que la racine
// des dossiers de Windows. Le chemin du processus fait foi quand il est lu ; sinon, la liste des noms.
public sealed class NomExeDeJeuTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    [Theory]
    [InlineData("LockApp.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("WindowsTerminal.exe")]
    public void Les_programmes_de_Windows_vus_en_plein_ecran_ne_sont_pas_des_jeux(string exe)
        => NomExeDeJeu.EstUnJeuPossible(exe).Should().BeFalse();

    // La fenêtre de DrDXT, agrandie ou posée hors écran pour une capture, ne passe jamais pour une partie. SondeJeu.exe
    // n'est pas de DrDXT : c'est le faux jeu des tests machine, qui doit rester reconnu comme un jeu.
    [Theory]
    [InlineData("DrDxt.App.exe")]
    [InlineData("DrDxt.Lanceur.exe")]
    [InlineData("DrDxt.ElevatedHelper.exe")]
    [InlineData("DrDxt.ReconcilerHost.exe")]
    [InlineData("PresentMonService.exe")]
    public void Les_executables_de_DrDXT_ne_sont_jamais_des_jeux(string exe)
    {
        NomExeDeJeu.EstUnJeuPossible(exe).Should().BeFalse();
        NomExeDeJeu.EstSurementPasUnJeu(exe, @"C:\Program Files\DrDXT\" + exe).Should().BeTrue();
        NomExeDeJeu.EstUnJeuPossible("SondeJeu.exe").Should().BeTrue("témoin : le faux jeu des tests machine reste un jeu possible");
    }

    [Fact]
    public void Tout_ce_qui_vit_sous_le_dossier_de_Windows_n_est_pas_un_jeu_meme_dans_un_sous_dossier()
    {
        NomExeDeJeu.EstUnJeuPossible("Inconnu.exe", Path.Combine(Windows, "SystemApps", "Microsoft.Truc_cw5n1h2txyewy", "Inconnu.exe"))
            .Should().BeFalse();
        NomExeDeJeu.EstUnJeuPossible("cod.exe", @"D:\Jeux\Call of Duty\cod.exe").Should().BeTrue();
        NomExeDeJeu.EstUnJeuPossible("cod.exe", null).Should().BeTrue("chemin non lu : la règle des noms");
        NomExeDeJeu.EstUnJeuPossible("cod.exe", Windows + "Old" + @"\cod.exe").Should().BeTrue("un dossier qui commence pareil n'est pas Windows");
    }

    // Les jeux les plus joués se reconnaissent à leur nom, même en fenêtré ou réduits : la fenêtre seule se trompait
    // (Discord agrandi sur un second écran passait pour le jeu).
    [Theory]
    [InlineData("cod.exe", true)]
    [InlineData("COD.EXE", true)]
    [InlineData("cs2.exe", true)]
    [InlineData("VALORANT-Win64-Shipping.exe", true)]
    [InlineData("League of Legends.exe", true)]
    [InlineData("JeuBClient-Win64-Shipping.exe", true)]
    [InlineData("Discord.exe", false)]
    [InlineData("Inconnu.exe", false)]
    [InlineData(null, false)]
    public void Les_jeux_connus_se_reconnaissent_a_leur_nom(string? exe, bool connu)
        => NomExeDeJeu.EstUnJeuConnu(exe).Should().Be(connu);

    // Un programme qu'on sait ne pas être un jeu est écarté ; un nom illisible (anti-triche qui refuse la lecture) ne
    // l'est pas : « non lu » n'est pas « pas un jeu ».
    [Theory]
    [InlineData("Discord.exe", true)]
    [InlineData("chrome.exe", true)]
    [InlineData("cod.exe", false)]
    [InlineData(null, false)]
    public void Seul_un_programme_reconnu_comme_pas_un_jeu_est_ecarte(string? exe, bool ecarte)
        => NomExeDeJeu.EstSurementPasUnJeu(exe, null).Should().Be(ecarte);

    // Un vrai jeu au nom hors du format strict (espace, accent) n'est pas écarté comme « sûrement pas un jeu ».
    [Theory]
    [InlineData("Among Us.exe", @"D:\SteamLibrary\steamapps\common\Among Us\Among Us.exe")]
    [InlineData("Lethal Company.exe", null)]
    public void Un_nom_inhabituel_n_est_pas_ecarte(string exe, string? chemin)
        => NomExeDeJeu.EstSurementPasUnJeu(exe, chemin).Should().BeFalse();

    [Fact]
    public void Un_programme_de_windows_reste_ecarte()
        => NomExeDeJeu.EstSurementPasUnJeu("notepad.exe", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe"))
            .Should().BeTrue();
}
