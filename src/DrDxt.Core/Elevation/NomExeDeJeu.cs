using System.Text.RegularExpressions;

namespace DrDxt.Core;

/// <summary>
/// Le nom d'exe d'un jeu tel que le helper l'accepte pour le plafond d'envoi : nu, ASCII, rien qui sorte de l'apostrophe
/// d'un script PowerShell. Une seule regle, lue par l'appli et le helper (sinon l'appli rallumerait un nom refuse
/// ensuite, apres une invite UAC).
/// </summary>
public static class NomExeDeJeu
{
    // 64 caractères au plus avant l'extension, une espace possible à l'intérieur seulement (« League of Legends.exe ») :
    // le nom est posé entre apostrophes dans le script. Sans IgnoreCase : avec lui, [A-Za-z] accepte le signe Kelvin (U+212A).
    private static readonly Regex Motif = new(@"^[A-Za-z0-9_.\-](?:[A-Za-z0-9_. \-]{0,62}[A-Za-z0-9_.\-])?\.[Ee][Xx][Ee]\z",
        RegexOptions.CultureInvariant);

    public static bool EstValide(string? nom) => nom is not null && Motif.IsMatch(nom);

    // Le fichier est écrivable par tout programme du compte et se rallume sans clic : un binaire de Windows toujours
    // lancé (explorer.exe) serait exempté pendant que tout le reste est bridé. Un jeu ne vit pas dans les dossiers de
    // Windows, et un nom qui n'est pas un exe nu ne se rallume pas. Limite connue : un autre programme toujours lancé hors
    // de ces dossiers reste possible. Appliquée aussi par le helper (« retenir ») et le service SYSTEM, et par la veille
    // du ping : une vidéo plein écran, un lecteur, un logiciel de stream ne sont pas des parties. Liste fermée des plus
    // courants, à étendre quand un joueur en voit un autre pris pour un jeu.
    private static readonly HashSet<string> PasDesJeux = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "opera_gx.exe", "brave.exe", "vivaldi.exe",
        "iexplore.exe", "vlc.exe", "mpc-hc.exe", "mpc-hc64.exe", "mpc-be64.exe", "PotPlayerMini64.exe",
        "wmplayer.exe", "Discord.exe", "obs64.exe", "Spotify.exe",
        // Lanceurs et programmes de fond : ouverts toute la session, retenus ils brideraient tout le reste en
        // permanence, et la veille prendrait Steam Big Picture (plein ecran) pour une partie.
        "steam.exe", "steamwebhelper.exe", "EpicGamesLauncher.exe", "Battle.net.exe", "EADesktop.exe",
        "EpicWebHelper.exe", "UbisoftConnect.exe", "upc.exe", "GalaxyClient.exe", "RiotClientServices.exe",
        "OneDrive.exe", "Dropbox.exe", "ms-teams.exe", "Teams.exe", "Slack.exe",
        // Écran de verrouillage, consoles et terminal.
        "LockApp.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "WindowsTerminal.exe", "OpenConsole.exe",
        // DrDXT lui-même : sa fenêtre (agrandie, ou posée hors écran pour une capture) n'est pas une partie.
        "DrDxt.App.exe", "DrDxt.Lanceur.exe", "DrDxt.ElevatedHelper.exe", "DrDxt.ReconcilerHost.exe",
        "PresentMonService.exe",
    };

    // Les jeux compétitifs les plus joués, reconnus par leur nom même en fenêtré ou réduits. Liste courte et fermée : un
    // jeu absent reste vu par la règle du plein écran ; elle sert à ne jamais rater ceux-là.
    private static readonly HashSet<string> JeuxConnus = new(StringComparer.OrdinalIgnoreCase)
    {
        "cod.exe", "cs2.exe", "VALORANT-Win64-Shipping.exe", "FortniteClient-Win64-Shipping.exe", "r5apex.exe", "r5apex_dx12.exe",
        "Overwatch.exe", "TslGame.exe", "RainbowSix.exe", "RainbowSix_DX12.exe", "RainbowSix_Vulkan.exe",
        "Marvel-Win64-Shipping.exe", "League of Legends.exe", "RocketLeague.exe", "dota2.exe", "JeuBClient-Win64-Shipping.exe",
    };

    public static bool EstUnJeuConnu(string? exe) => exe is not null && JeuxConnus.Contains(exe);

    /// <summary>Vrai seulement quand le nom (ou le chemin) est lu et dit « pas un jeu » ; un nom illisible (anti-triche) ou
    /// hors format strict (« Among Us.exe ») ne s'écarte pas, il n'est juste pas admis là où le format garde.</summary>
    public static bool EstSurementPasUnJeu(string? exe, string? chemin)
        => exe is not null && !EstUnJeuConnu(exe) && (SousWindows(chemin) || (EstValide(exe) && !EstUnJeuPossible(exe)));

    /// <summary>Avec le chemin du processus quand il est lu : tout ce qui vit sous le dossier de Windows, à toute
    /// profondeur (SystemApps, WindowsPowerShell…), n'est pas un jeu ; puis la règle des noms.</summary>
    public static bool EstUnJeuPossible(string? exe, string? chemin) => !SousWindows(chemin) && EstUnJeuPossible(exe);

    private static bool SousWindows(string? chemin)
    {
        if (chemin is null) return false;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        try
        {
            var plein = Path.GetFullPath(chemin);
            return windows.Length > 0 && plein.StartsWith(Path.TrimEndingDirectorySeparator(windows) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception) { return false; } // chemin illisible : la règle des noms
    }

    public static bool EstUnJeuPossible(string? exe)
    {
        if (exe is null || !EstValide(exe) || Path.GetFileName(exe) != exe) return false;
        if (PasDesJeux.Contains(exe)) return false;
        foreach (var dossier in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                     Environment.GetFolderPath(Environment.SpecialFolder.System),
                     Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                 })
        {
            if (dossier.Length > 0 && File.Exists(Path.Combine(dossier, exe))) return false;
        }
        return true;
    }
}
