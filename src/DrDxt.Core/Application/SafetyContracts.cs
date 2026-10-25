namespace DrDxt.Core;

// --- Coutures de sécurité et de retour arrière ---
//
// Les frontières testables dont dépend le SafetyManager, pour que toute la logique
// « jamais d'application sans point de restauration vérifié » / « le retour arrière rend
// l'origine relevée » se teste avec des doublures : rien ici ne touche la vraie
// Restauration du système ni ne lance quoi que ce soit en élevé. Le vrai travail privilégié
// (créer le point) passe toujours par IElevatedClient + un CreateRestorePointOp typé ;
// cette couture ne modélise que « la restauration est-elle utilisable » + la vérification.

/// <summary>Abstraction de la Restauration du système, testable. Les implémentations disent
/// si la restauration est utilisable, tentent de l'activer, et vérifient qu'un point tout
/// juste créé existe vraiment (une GPO ou l'édition peut annuler la création en silence).</summary>
public interface IRestorePointService
{
    /// <summary>Vrai quand la Restauration du système est active et qu'un point peut être créé.</summary>
    bool IsEnabled();

    /// <summary>Une stratégie de Windows (« Désactiver la Restauration du système », DisableSR) l'interdit : ni le joueur ni
    /// DrDXT ne peuvent l'activer. Par défaut : non.</summary>
    bool InterditeParStrategie() => false;

    /// <summary>Tentative, au mieux, d'activer la Restauration du système (peut échouer en silence
    /// sous GPO ou sur une édition non prise en charge). Vrai seulement si elle est utilisable ensuite.</summary>
    bool TryEnable();

    /// <summary>La date (UTC) du point de restauration le plus recent cree depuis
    /// <paramref name="depuisUtc"/>, quel que soit son nom, ou <c>null</c> s'il n'y en a aucun : la
    /// garde contre une creation silencieusement ignoree (GPO/SKU).
    /// Pourquoi « quel que soit son nom » : par defaut Windows saute la creation s'il existe un point de
    /// moins de 24 h et repond quand meme « reussi » (Microsoft Learn, SRSetRestorePointW). Ce point-la,
    /// Windows Update ou autre, date d'avant nos changements : il protege, et l'ecran le dit.</summary>
    DateTime? DernierPointDepuis(DateTime depuisUtc);
}

/// <summary>Frontière de consentement côté UI : demande à l'utilisateur s'il faut activer la
/// Restauration du système quand elle est coupée. Abstraite pour tester la logique du
/// SafetyManager sans vraie invite.</summary>
public interface IConsentPrompt
{
    bool AskEnableRestore();
}
