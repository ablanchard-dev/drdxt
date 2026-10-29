using System.Runtime.Versioning;
using System.Windows;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// MessageBoxConsentPrompt : le vrai IConsentPrompt. Oui/Non pour activer la Restauration du système coupée ; refusé, le
// SafetyManager bloque (RESTORE_DECLINED, jamais d'avance sans point vérifié). Sans interface, non. Ne lève jamais.
// Question et titre viennent des ressources (la langue de l'utilisateur, et les gardes des `.resx`).
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
