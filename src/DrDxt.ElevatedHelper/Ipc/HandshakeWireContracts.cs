namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Contrats de fil du handshake d'amorçage (négociation d'une clé de session éphémère).
//
// C'est le handshake au moment de la connexion, qui tourne avant le protocole typé signé en
// HMAC existant. Son seul rôle est d'établir la clé HMAC par session qu'utilise ensuite, sans
// changement, la couche éprouvée TypedPrivilegedServer / TypedNamedPipeElevatedClient.
//
// Ordre sur le fil (un aller-retour, tramé par WireCodec comme tout autre message : JSON
// préfixé par sa longueur, borné, membres inconnus rejetés) :
//   1. client -> serveur : HandshakeHello (marqueur de protocole seulement ; aucun secret)
//   2. le serveur authentifie l'appelant (PID -> identité d'image) avant de répondre
//   3a. appelant bon     -> serveur -> client : HandshakeResult{ Ok=true,  SessionKeyBase64 }
//   3b. appelant mauvais -> serveur -> client : HandshakeResult{ Ok=false } (aucune clé) puis fermeture
//
// Sécurité :
//   * La clé est générée côté serveur (le bout élevé, de confiance) par un CSPRNG et elle est
//     éphémère (par connexion, en mémoire seulement, jamais persistée).
//   * La clé ne passe que par le pipe local FIRST_PIPE_INSTANCE protégé par DACL, et seulement
//     après l'authentification de l'appelant. Un pair qui n'est pas DrDxt.App ne reçoit jamais
//     de clé (il est refusé avant 3a).
//   * Le Hello ne porte aucun secret ; le rejouer ne peut pas extraire de clé, car une clé n'est
//     frappée qu'après l'auth de l'appelant, qu'un processus attaquant ne peut pas passer.
// -----------------------------------------------------------------------

/// <summary>Salut de connexion client -> serveur. Ne porte qu'un marqueur de version de
/// protocole pour que le serveur rejette proprement un pair incompatible ou déchet. Aucun
/// secret ne passe ici.</summary>
public sealed record HandshakeHello
{
    /// <summary>Marqueur de protocole fermé. Incrémenté si la forme du handshake change un jour.</summary>
    public string Protocol { get; init; } = HandshakeProtocol.Version;
}

/// <summary>Issue du handshake serveur -> client. En cas de succès, porte la clé de session
/// éphémère tout juste frappée (base64, 32 octets). En cas d'échec, ne porte aucune clé
/// (le client doit se fermer, fail-closed, et n'obtient aucun canal).</summary>
public sealed record HandshakeResult
{
    public bool Ok { get; init; }
    /// <summary>Base64 de la clé de session éphémère de 32 octets. Présente seulement quand
    /// <see cref="Ok"/> vaut true et que l'appelant a été authentifié.</summary>
    public string? SessionKeyBase64 { get; init; }
}

/// <summary>Constantes du protocole de handshake (source unique du marqueur
/// qu'envoie le client et que vérifie le serveur).</summary>
public static class HandshakeProtocol
{
    public const string Version = "drdxt-handshake-v1";

    /// <summary>Longueur en octets de la clé de session éphémère (clé du HMAC-SHA256).</summary>
    public const int SessionKeyBytes = 32;
}
