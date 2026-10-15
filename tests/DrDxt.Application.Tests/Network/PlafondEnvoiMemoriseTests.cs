using DrDxt.Application.Network;
using FluentAssertions;

namespace DrDxt.Application.Tests.Network;

public sealed class PlafondEnvoiMemoriseTests : IDisposable
{
    private readonly string _dossier = Path.Combine(Path.GetTempPath(), "drdxt-plafond-" + Guid.NewGuid().ToString("N"));
    private string Chemin => Path.Combine(_dossier, "plafond-envoi.json");

    public void Dispose()
    {
        if (Directory.Exists(_dossier)) Directory.Delete(_dossier, recursive: true);
    }

    [Fact]
    public void Ce_qui_est_retenu_se_relit_tel_quel()
    {
        var m = new PlafondEnvoiMemorise(Chemin);
        var retenu = new PlafondEnvoiRetenu("cod.exe", 20_000_000, new DateTimeOffset(2026, 9, 25, 2, 40, 0, TimeSpan.FromHours(2)));
        m.Retenir(retenu).IsOk.Should().BeTrue();
        new PlafondEnvoiMemorise(Chemin).Lire().Should().Be(retenu);
    }

    [Fact]
    public void Eteint_absent_ou_abime_ne_rallume_rien()
    {
        var m = new PlafondEnvoiMemorise(Chemin);
        m.Lire().Should().BeNull("aucun fichier");

        m.Retenir(new("cod.exe", 20_000_000, DateTimeOffset.Now));
        m.Oublier().IsOk.Should().BeTrue();
        m.Lire().Should().BeNull("le joueur l'a eteint");

        File.WriteAllText(Chemin, "{ pas du json");
        m.Lire().Should().BeNull("un fichier abime n'allume jamais rien");

        File.WriteAllText(Chemin, """{"Exe":"cod.exe","PlafondBps":0,"MesureLe":"2026-09-25T02:40:00+02:00"}""");
        m.Lire().Should().BeNull("un plafond nul n'est pas une mesure");
    }

    // Le fichier est écrivable par tout programme du compte. Y mettre un binaire de Windows qui tourne
    // toujours (explorer.exe) exempterait ce seul programme et briderait l'envoi de tout le reste dès le
    // lancement de DrDXT. Un jeu ne vit jamais dans les dossiers de Windows, que le compte ne peut pas remplir.
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("svchost.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData(@"..\..\Windows\explorer.exe")]
    [InlineData(@"C:\Games\cod.exe")]
    public void Un_binaire_de_Windows_ou_un_chemin_ne_se_rallume_pas(string exe)
    {
        var m = new PlafondEnvoiMemorise(Chemin);
        m.Retenir(new(exe, 1_000_000, DateTimeOffset.Now)).IsOk.Should().BeTrue();
        m.Lire().Should().BeNull("seul un nom d'exe nu qui n'est pas un binaire de Windows se rallume");
    }

    // La veille du ping prend le programme en plein ecran pour le jeu. Une video en plein ecran
    // dans un navigateur ou un lecteur n'en est pas un : l'exempter briderait tout le reste pour rien.
    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("msedge.exe")]
    [InlineData("Firefox.exe")]
    [InlineData("opera.exe")]
    [InlineData("brave.exe")]
    [InlineData("vlc.exe")]
    [InlineData("mpc-hc64.exe")]
    [InlineData("Discord.exe")]
    [InlineData("obs64.exe")]
    public void Un_navigateur_ou_un_lecteur_n_est_pas_un_jeu(string exe)
    {
        PlafondEnvoiMemorise.EstUnJeuPossible(exe).Should().BeFalse();
        var m = new PlafondEnvoiMemorise(Chemin);
        m.Retenir(new(exe, 1_000_000, DateTimeOffset.Now)).IsOk.Should().BeTrue();
        m.Lire().Should().BeNull();
    }

    // Un lanceur ou un programme de fond tourne toute la session. Retenu, il laisserait brider
    // l'envoi de tout le reste en permanence ; pris par la veille (Steam Big Picture est en plein ecran),
    // il se ferait passer pour la partie.
    [Theory]
    [InlineData("steam.exe")]
    [InlineData("steamwebhelper.exe")]
    [InlineData("EpicGamesLauncher.exe")]
    [InlineData("Battle.net.exe")]
    [InlineData("EADesktop.exe")]
    [InlineData("OneDrive.exe")]
    public void Un_lanceur_ou_un_programme_de_fond_n_est_pas_un_jeu(string exe)
    {
        PlafondEnvoiMemorise.EstUnJeuPossible(exe).Should().BeFalse();
        var m = new PlafondEnvoiMemorise(Chemin);
        m.Retenir(new(exe, 1_000_000, DateTimeOffset.Now)).IsOk.Should().BeTrue();
        m.Lire().Should().BeNull();
    }

    [Theory]
    [InlineData("cod.exe")]
    [InlineData("VALORANT-Win64-Shipping.exe")]
    public void Un_jeu_reste_un_jeu(string exe) => PlafondEnvoiMemorise.EstUnJeuPossible(exe).Should().BeTrue();

    // Le fichier est ecrivable par tout le compte et relu a chaque lancement, sans clic.
    // Un contenu abime ne fait ni tomber le lancement ni rallumer quoi que ce soit.
    public static TheoryData<string> Abimes() => new()
    {
        "",
        "null",
        "{",
        "[]",
        "{\"Exe\":null,\"PlafondBps\":5000000}",
        "{\"Exe\":\"cod.exe\",\"PlafondBps\":\"beaucoup\"}",
        "{\"Exe\":\"cod.exe\",\"PlafondBps\":1e400}",
        "{\"Exe\":\"cod.exe\",\"PlafondBps\":-5}",
        "{\"Exe\":\"cod.exe\",\"PlafondBps\":5000000,\"MesureLe\":\"jamais\"}",
        "{\"Exe\":\"co\\u0000d.exe\",\"PlafondBps\":5000000}",
        "{\"Exe\":123,\"PlafondBps\":5000000}",
        new string('[', 5000) + new string(']', 5000),
    };

    [Theory]
    [MemberData(nameof(Abimes))]
    public void Un_fichier_abime_ne_fait_pas_tomber_le_lancement_et_ne_rallume_rien(string contenu)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Chemin)!);
        File.WriteAllText(Chemin, contenu);

        var lire = () => new PlafondEnvoiMemorise(Chemin).Lire();

        lire.Should().NotThrow();
        lire().Should().BeNull();
    }
}
