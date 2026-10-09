using System.Runtime.Versioning;
using System.Windows;
using DrDxt.Core;

namespace DrDxt.App.Platform;

// -----------------------------------------------------------------------
// MessageBoxConsentPrompt — the real IConsentPrompt.
//
// Asks the user (Yes/No) whether to enable System Restore when it is off. Used
// only on the OFF path; if the user declines the SafetyManager blocks with
// RESTORE_DECLINED (never proceeds without a verified point). A headless / no-UI
// context defaults to not consenting (fail-closed). Never throws.
//
// 19/09/2026 — cette question etait ecrite en dur, en anglais.
// C'est la seule question que DrDXT pose avant de toucher la machine, et elle
// s'affichait dans une langue que l'utilisateur n'a pas choisie — sur le poste
// francais auquel ce produit s'adresse — pendant qu'il doit repondre Oui ou Non.
// La regle etait deja ecrite dans `AppReconcilerSeams.cs` : « human-readable text
// resolves from a resx KEY via ILocalizer, never hardcoded ». Une surface l'appliquait,
// celle-ci non. Le titre portait meme un tiret cadratin, que les gardes de ressources
// de ce depot refusent — sans les voir, puisqu'elles lisent les `.resx`.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class MessageBoxConsentPrompt(ILocalizer localizer) : IConsentPrompt
{
    public bool AskEnableRestore()
    {
        try
        {
            if (System.Windows.Application.Current?.Dispatcher is null)
                return false; // no UI context → do not assume consent

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
