using DrDxt.Core;
using DrDxt.Reconciler;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// LoggingReconcilerNotifier : l'IReconcilerNotifier de l'hôte.
//
// Quand une dérive est classée UserIntentional/Unknown, le réconciliateur ne l'écrase pas (I5) ;
// il lève cette notification à la place. Dans le service d'arrière-plan il n'y a pas de surface
// de toast interactive : la notification est journalisée par IDiagnosticsLog (la même trace
// d'audit que lit l'App), et l'App peut la montrer au lancement suivant.
//
// i18n : le texte lisible vient d'une clé resx via ILocalizer, jamais d'une chaîne en dur (le
// projet a un localiseur). Le message localisé est construit pour l'audit / l'interface ; la
// ligne de journal elle-même enregistre les faits structurés (id du levier + valeur actuelle
// posée par l'utilisateur) pour qu'un balayage du journal reste indépendant de la langue.
// -----------------------------------------------------------------------
public sealed class LoggingReconcilerNotifier : IReconcilerNotifier
{
    /// <summary>La clé resx de la notification « un réglage optimisé par DrDXT a été changé, laissé
    /// tel que vous l'avez posé ». {0} = le titre / l'id du levier.</summary>
    public const string UserDriftKey = "reconciler.notify.user_drift";

    /// <summary>Acteur de journal qui marque une issue « notifié, pas écrasé » : reprend l'acteur
    /// « DrDXT/notified » du journal des changements du réconciliateur.</summary>
    public const string NotifiedActor = "DrDXT/notified";

    private readonly IDiagnosticsLog _diagnostics;
    private readonly ILocalizer _localizer;

    public LoggingReconcilerNotifier(IDiagnosticsLog diagnostics, ILocalizer localizer)
    {
        _diagnostics = diagnostics;
        _localizer = localizer;
    }

    public Task NotifyUserIntentionalDriftAsync(
        AppliedTweak tweak, string currentValueJson, CancellationToken ct)
    {
        // Le message localisé se résout ici pour qu'une clé absente se voie au premier passage ; le service n'a pas
        // d'écran et ne l'affiche pas. La ligne de journal structurée ci-dessous reste neutre en langue.
        _ = string.Format(
            System.Globalization.CultureInfo.CurrentUICulture,
            _localizer.Get(UserDriftKey),
            tweak.TweakId);

        // Journaliser les faits structurés : la valeur voulue a dérivé vers la valeur posée par
        // l'utilisateur ; elle a été notifiée, pas écrasée (I5).
        _diagnostics.Change(
            tweakId: tweak.TweakId,
            from: tweak.DesiredValueJson,
            to: currentValueJson,
            actor: NotifiedActor);

        return Task.CompletedTask;
    }
}
