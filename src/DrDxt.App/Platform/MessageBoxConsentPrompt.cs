using System.Runtime.Versioning;
using System.Windows;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// MessageBoxConsentPrompt : le vrai IConsentPrompt.
//
// Demande à l'utilisateur (Oui/Non) s'il faut activer la Restauration du système quand elle
// est coupée. Sert seulement dans ce cas ; si l'utilisateur refuse, le SafetyManager bloque
// avec RESTORE_DECLINED (jamais d'avance sans point vérifié). Sans interface, la réponse par
// défaut est non (refus au moindre doute). Ne lève jamais.
//
// La question et son titre viennent des ressources : c'est la seule question que DrDXT pose avant
// de toucher la machine, et elle doit parler la langue de l'utilisateur. Écrits en dur, ils
// échapperaient aussi aux gardes de ressources du dépôt, qui ne lisent que les `.resx`.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class MessageBoxConsentPrompt(ILocalizer localizer) : IConsentPrompt
{
    public bool AskEnableRestore()
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher is null)
                return false; // pas d'interface → on ne suppose pas le consentement

            var result = MessageBox.Show(
                localizer.Get("Dialog.Restore.Question"),
                localizer.Get("Dialog.Restore.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            return result == MessageBoxResult.Yes;
        }
        catch
        {
            return false;
        }
    }
}
