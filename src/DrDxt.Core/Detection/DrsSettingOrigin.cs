namespace DrDxt.Core;

/// <summary>
/// « Qui a mis cette valeur ici ? » depuis settingLocation et isCurrentPredefined, remplis à chaque lecture DRS : sans
/// eux, sur une machine aux 10218 profils livrés par le pilote, chaque valeur ressemble à un choix de l'utilisateur.
/// Source : Driver Settings Programming Guide de NVIDIA (PG-12072-001 v01), p. 2 (« predefined » contre « user
/// settings »), p. 9-10 (hors du profil courant : hérité du profil de base ou global). Dans Core, pour que le helper
/// prouve un retour par la même lecture (une seule définition de « rendu »). Pur ; emplacement inconnu →
/// <see cref="DrsValueOrigin.Unknown"/>.
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
        // L'héritage d'abord : isCurrentPredefined == 0 sur une valeur héritée se lirait « posée ici ».
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
