using DrDxt.Core;

namespace DrDxt.Bios;

/// <summary>
/// Résout le jeton de génération d'interface BIOS qui indexe la bibliothèque de guides, à partir du
/// constructeur + <see cref="BiosInfo"/>. Chaque constructeur nomme à sa façon son interface BIOS
/// récente (MSI « Click BIOS 5 », ASUS « AI Tweaker UEFI », Gigabyte « Aorus UEFI », ASRock « UEFI »).
///
/// Heuristique grossière (la correspondance par révision n'a pas encore de données de banc) : le jeton « récent » du
/// constructeur, et la bibliothèque se rabat sur un repli étiqueté. Ne lève jamais, rend toujours un jeton non vide.
/// </summary>
public static class BiosUiGenerationDetector
{
    /// <summary>Le jeton de génération générique, indépendant du constructeur (toujours une clé valide de la bibliothèque).</summary>
    public const string Generic = "Generic";

    public static string Detect(BiosVendor vendor, BiosInfo bios)
    {
        _ = bios; // réservé à une future distinction par version / date (données de banc)
        return vendor switch
        {
            BiosVendor.Msi => "Click BIOS 5",
            BiosVendor.Asus => "AI Tweaker UEFI",
            BiosVendor.Gigabyte => "Aorus UEFI",
            BiosVendor.Asrock => "ASRock UEFI",
            _ => Generic,
        };
    }
}
