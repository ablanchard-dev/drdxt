using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// Répond à la question dont dépendent chaque passe A/B et chaque « mon levier a-t-il tenu ? » :
/// <b>un changement qui exige un redémarrage a-t-il vraiment pris effet ?</b>
///
/// Deux pièges qu'il évite, tous deux observés en vrai :
///
/// 1. <b>PendingFileRenameOperations n'est pas un drapeau de redémarrage.</b> Les contrôles
///    « redémarrage en attente » tout faits testent sa simple présence et s'allument sur un PC sain :
///    la valeur porte couramment des entrées bénignes mises en file par la maintenance ordinaire.
///    Ici elle ne donne jamais seule <see cref="RestartVerdict.RestartRequired"/> ; les signaux qui
///    font foi sont les clés de maintenance dont l'existence même est le drapeau, par conception de
///    Microsoft (Component Based Servicing\RebootPending, WindowsUpdate\Auto Update\RebootRequired).
///    Source : https://learn.microsoft.com/en-us/windows/deployment/update/waas-restart
///
/// 2. <b>Le démarrage rapide fait mentir « je l'ai éteint ».</b> Avec HiberbootEnabled, « Arrêter »
///    puis rallumer met en veille prolongée et restaure la session noyau : les pilotes ne se
///    rechargent pas et les réglages de démarrage ne s'appliquent pas. Seul « Redémarrer » fait une
///    vraie initialisation à froid du noyau. Un uptime court ne prouve donc pas un redémarrage, et un
///    changement peut sembler « annulé » sans bruit. Démarrage rapide actif : on refuse de conclure et
///    on dit quel bouton presser, au lieu d'affirmer Applied.
///    Source : https://learn.microsoft.com/en-us/troubleshoot/windows-client/setup-upgrade-and-drivers/updates-not-install-with-fast-startup
///    (« When you shut down your computer, Fast Startup will put your computer into a hibernation state
///    instead of a full shutdown » ; « This behavior doesn't occur when you restart your computer ».
///    L'ancienne page windows-fast-startup répond 404, relevé le 25/09/2026.)
///
/// Pur et total : ni E/S, ni horloge. Toutes les lectures de l'OS reviennent à l'appelant ; null
/// veut dire « non lu », jamais « absent » : une entrée illisible donne <see cref="RestartVerdict.Indeterminate"/>,
/// jamais un certificat de bonne santé inventé (même contrat que <see cref="PagefileInferencer"/>).
/// </summary>
public static class RestartStateInferencer
{
    /// <param name="servicingRebootPending">
    /// Vrai si l'une des clés de maintenance existe, faux si les deux ont été lues absentes, null si illisible.
    /// </param>
    /// <param name="pendingFileRenames">
    /// Entrées brutes de PendingFileRenameOperations, vide si la valeur est absente, null si illisible.
    /// Signal faible par construction : voir le piège 1.
    /// </param>
    /// <param name="fastStartup">HiberbootEnabled : On, Off ou Unknown.</param>
    /// <param name="uptime">Temps depuis la dernière initialisation du noyau (GetTickCount64), null si non lu.</param>
    /// <param name="sinceChange">
    /// Âge du changement vérifié, null pour un diagnostic à froid sans changement à vérifier :
    /// le verdict décrit alors la machine, pas un changement.
    /// </param>
    public static RestartState Infer(
        bool? servicingRebootPending,
        IReadOnlyList<string>? pendingFileRenames,
        TriState fastStartup,
        TimeSpan? uptime,
        TimeSpan? sinceChange)
    {
        // Une clé de maintenance fait foi à elle seule : Windows attend un redémarrage, donc rien de
        // mesuré maintenant ne décrit la configuration qui tournera après.
        if (servicingRebootPending == true)
            return new RestartState(RestartVerdict.RestartRequired,
                "Windows servicing has a restart pending: measurements taken now do not describe " +
                "the configuration that will be running after it.");

        var renamesQueued = pendingFileRenames is { Count: > 0 }
                            && pendingFileRenames.Any(e => !string.IsNullOrWhiteSpace(e));

        if (sinceChange is null)
            return ColdDiagnostic(servicingRebootPending, pendingFileRenames, renamesQueued);

        if (uptime is null)
            return new RestartState(RestartVerdict.Indeterminate,
                "Uptime could not be read, so we cannot tell whether the machine has restarted " +
                "since the change.");

        // Uptime au moins aussi vieux que le changement ⇒ cette session noyau le précède ⇒ pas de redémarrage depuis.
        if (uptime.Value >= sinceChange.Value)
            return new RestartState(RestartVerdict.RestartRequired,
                "The machine has not restarted since the change was written, so anything requiring " +
                "a restart is not in effect yet.");

        // Une initialisation du noyau a eu lieu après le changement. Qu'elle soit réelle dépend du démarrage rapide.
        if (fastStartup == TriState.On)
            return new RestartState(RestartVerdict.Indeterminate,
                "The machine started after the change, but Fast Startup is enabled: a shutdown then " +
                "power-on restores the previous kernel session and does not reload drivers. Use " +
                "Restart, not Shut down, then check again.");

        if (fastStartup == TriState.Unknown)
            return new RestartState(RestartVerdict.Indeterminate,
                "The machine started after the change, but Fast Startup could not be read, so we " +
                "cannot tell whether that start was a real kernel init.");

        return new RestartState(RestartVerdict.NoRestartPending,
            renamesQueued
                ? "A real restart happened after the change: it is in effect. File renames are " +
                  "queued for the next restart, which is routine and not a defect."
                : "A real restart happened after the change: it is in effect.");
    }

    private static RestartState ColdDiagnostic(
        bool? servicingRebootPending, IReadOnlyList<string>? pendingFileRenames, bool renamesQueued)
    {
        if (servicingRebootPending is null && pendingFileRenames is null)
            return new RestartState(RestartVerdict.Indeterminate,
                "No restart indicator could be read on this machine.");

        // Piège 1 : les renommages seuls sont le signal que tout contrôle tout fait lit mal. On le
        // rapporte comme non prouvé, jamais comme « redémarrage requis ».
        if (renamesQueued)
            return new RestartState(RestartVerdict.Indeterminate,
                "File renames are queued for the next restart. On its own this is a weak signal (" +
                "ordinary servicing queues them on healthy machines), so it does not establish that " +
                "a restart is required.");

        return new RestartState(RestartVerdict.NoRestartPending,
            "No restart is pending on this machine.");
    }
}
