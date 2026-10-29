namespace DrDxt.Infrastructure.Updates;

// Codes d'erreur de mise à jour, locaux au module (même convention que ReconcilerErrorCodes) : des codes lisibles par
// machine, jamais du texte d'interface. Règle du chemin : un paquet est vérifié (signature puis empreinte) avant toute
// exécution ou tout remplacement de fichier ; jamais fail-open, toute incertitude garde la version installée.
public static class UpdateErrorCodes
{
    // La signature Ed25519 sur le manifeste canonique ne se vérifie pas : signature altérée, champs
    // du manifeste altérés (url/sha256/version), signature absente ou vide, signée par la mauvaise
    // clé, ou clé publique de mise à jour embarquée corrompue. Échec propre : le téléchargement est
    // refusé ; on ne récupère même pas la charge, car l'url / le sha256 d'un manifeste non fiable ne
    // valent pas qu'on agisse dessus. Jamais « ok par défaut ».
    public const string SignatureInvalid = "UPDATE_SIG_INVALID";

    // Rien n'est publié à l'adresse du manifeste (404). Ni « à jour » ni une panne : vrai que le canal soit sain (rien de
    // publié encore, l'état du site GitHub Pages) ou cassé (adresse fausse). Seul ce qui est su se dit.
    public const string NotPublished = "UPDATE_NOT_PUBLISHED";

    // Le SHA-256 de la charge téléchargée n'égale pas le sha256 porté par le manifeste (déjà vérifié
    // par signature). La charge n'est jamais exécutée / préparée / extraite. On ne se fie qu'à une
    // empreinte venue d'un manifeste signé : ce contrôle tourne donc strictement après le passage de SignatureInvalid.
    public const string HashMismatch = "UPDATE_HASH_MISMATCH";

    // La version du manifeste est inférieure à la version installée, hors canal bêta. Refusé : une
    // mise à jour ne doit jamais ramener l'utilisateur en arrière en silence (une rétrogradation est
    // le vecteur classique pour réintroduire une faille corrigée). Les canaux bêta peuvent l'autoriser.
    public const string Downgrade = "UPDATE_DOWNGRADE";

    // Le flux de mise à jour est injoignable (pas de réseau / panne de transport). Non bloquant :
    // l'app continue sur la version actuelle. Distinct exprès de SignatureInvalid : un flux injoignable
    // ne dégrade jamais la confiance, il ne fait que reporter.
    public const string Offline = "UPDATE_OFFLINE";

    // Aucun serveur de mise a jour n'est configure (ou son url n'est pas une url https
    // absolue). L'updater n'est pas monte : le produit reste sur sa version installee et ne tente
    // rien. Deliberement distinct d'Offline — « pas configure » se corrige en configurant, tandis
    // qu'« hors ligne » envoie chercher une panne de reseau qui n'existe pas.
    public const string NotConfigured = "UPDATE_NOT_CONFIGURED";

    // Une charge vérifiée n'a pas pu être préparée / extraite sur le disque (échec d'E/S, disque plein,
    // permission). La version précédente reste intacte (transaction propre, jamais un état à moitié
    // appliqué). Échec propre, jamais d'exception à travers la frontière.
    public const string StageFailed = "UPDATE_STAGE_FAILED";

    // Une exception qu'aucune couture ne prévoit, arrêtée à la frontière : rien n'est proposé ni installé (fail-closed),
    // et l'App le dit comme un échec, pas comme un réseau coupé.
    public const string Unexpected = "UPDATE_UNEXPECTED";
}
