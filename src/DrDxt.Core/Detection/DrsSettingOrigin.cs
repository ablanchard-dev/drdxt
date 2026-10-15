namespace DrDxt.Core;

/// <summary>
/// Transforme les deux champs que NVIDIA remplit à chaque lecture DRS (settingLocation et
/// isCurrentPredefined) en réponse à « qui a mis cette valeur ici ? ».
///
/// Sans eux, on ne garde que le DWORD. Sur une machine qui porte 10218 profils d'application
/// livrés par le pilote, chaque valeur ressemble alors à un choix de l'utilisateur, et la
/// question « quels réglages ai-je vraiment changés ? » n'a plus de réponse.
///
/// Sens tiré du Driver Settings Programming Guide de NVIDIA (PG-12072-001 v01) : la p. 2 sépare
/// les « predefined settings » (livrés avec le pilote) des « user settings » (modifiés après
/// installation) ; les p. 9-10 disent que les réglages hors du profil courant sont hérités du
/// profil de base ou global, et à ignorer quand on demande ce qu'un profil possède.
///
/// Vit dans Core : le helper élevé a besoin de la même lecture pour prouver qu'un retour arrière
/// a rendu au profil d'un jeu sa provenance d'origine, et il ne référence que Core.
/// Deux copies de cette correspondance feraient deux définitions de « rendu ».
///
/// Pur et total : aucune E/S. Un emplacement inconnu donne <see cref="DrsValueOrigin.Unknown"/>.
/// </summary>
public static class DrsSettingOrigin
{
    // NVDRS_SETTING_LOCATION (PG-12072-001).
    private const uint CurrentProfile = 0, GlobalProfile = 1, BaseProfile = 2, DefaultProfile = 3;

    /// <param name="settingLocation">NVDRS_SETTING.settingLocation tel que rendu par le pilote.</param>
    /// <param name="isCurrentPredefined">NVDRS_SETTING.isCurrentPredefined : non nul = la valeur
    /// courante est celle livrée par NVIDIA.</param>
    /// <param name="isPredefinedValid">NVDRS_SETTING.isPredefinedValid : predefinedValue est-il
    /// utilisable. Rapporté, jamais utilisé pour passer outre les deux champs ci-dessus.</param>
    public static DrsOriginState Describe(uint settingLocation, uint isCurrentPredefined, uint isPredefinedValid)
    {
        // L'héritage se décide d'abord. Sinon isCurrentPredefined == 0 sur une valeur héritée se
        // lirait « l'utilisateur l'a posée ici » ; il l'a posée ailleurs, et envoyer quelqu'un
        // modifier un profil qui ne possède rien est pire que ne rien dire.
        switch (settingLocation)
        {
            case GlobalProfile:
                return new(DrsValueOrigin.InheritedFromGlobalProfile, false,
                    "This value is inherited from the global profile: this profile does not own it, " +
                    "so changing it here would not be changing what the driver applies.");

            case BaseProfile:
                return new(DrsValueOrigin.InheritedFromBaseProfile, false,
                    "This value is inherited from the base profile: this profile does not own it.");

            case DefaultProfile:
                return new(DrsValueOrigin.DriverDefault, false,
                    "This is the driver default: nobody set it anywhere.");

            case CurrentProfile:
                return isCurrentPredefined != 0
                    ? new(DrsValueOrigin.ShippedWithTheDriver, false,
                        "This value ships with the NVIDIA driver for this application: it is a " +
                        "predefined setting, not a choice anyone made on this machine." +
                        (isPredefinedValid != 0 ? "" : " (the shipped value itself was not readable)"))
                    : new(DrsValueOrigin.SetByUser, true,
                        "This value was set on this profile and is not one the driver ships: " +
                        "someone changed it on this machine.");

            default:
                return new(DrsValueOrigin.Unknown, false,
                    $"The driver reported an unrecognised setting location ({settingLocation}), " +
                    "so where this value comes from is not established.");
        }
    }
}
