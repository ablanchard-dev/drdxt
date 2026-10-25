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
                return new(DrsValueOrigin.InheritedFromGlobalProfile, false, Docteur.T("DO.Global"));

            case BaseProfile:
                return new(DrsValueOrigin.InheritedFromBaseProfile, false, Docteur.T("DO.Base"));

            case DefaultProfile:
                return new(DrsValueOrigin.DriverDefault, false, Docteur.T("DO.Defaut"));

            case CurrentProfile:
                return isCurrentPredefined != 0
                    ? new(DrsValueOrigin.ShippedWithTheDriver, false,
                        Docteur.T("DO.Livre") + (isPredefinedValid != 0 ? "" : Docteur.T("DO.LivreIllisible")))
                    : new(DrsValueOrigin.SetByUser, true, Docteur.T("DO.Utilisateur"));

            default:
                return new(DrsValueOrigin.Unknown, false, Docteur.T("DO.Inconnu", settingLocation));
        }
    }
}
