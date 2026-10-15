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
// Cette question etait ecrite en dur, en anglais.
// C'est la seule question que DrDXT pose avant de toucher la machine, et elle
// s'affichait dans une langue que l'utilisateur n'a pas choisie (sur le poste
// francais auquel ce produit s'adresse) pendant qu'il doit repondre Oui ou Non.
// La regle etait deja ecrite dans `AppReconcilerSeams.cs` : « le texte lisible vient
// d'une clé resx via ILocalizer, jamais écrit en dur ». Une surface l'appliquait,
// celle-ci non. Le titre portait meme un tiret cadratin, que les gardes de ressources
// de ce depot refusent, sans les voir, puisqu'elles lisent les `.resx`.
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
