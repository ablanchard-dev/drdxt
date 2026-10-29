namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Contrats de fil du handshake d'amorçage : à la connexion, avant le protocole typé signé en HMAC, il établit la clé de
// session qu'utilise ensuite TypedPrivilegedServer / TypedNamedPipeElevatedClient.
// Un aller-retour, tramé par WireCodec (JSON préfixé par sa longueur, borné, membres inconnus rejetés) :
//   1. client -> serveur : HandshakeHello (marqueur de protocole seulement ; aucun secret)
//   2. le serveur authentifie l'appelant (PID -> identité d'image) avant de répondre
//   3a. appelant bon     -> serveur -> client : HandshakeResult{ Ok=true,  SessionKeyBase64 }
//   3b. appelant mauvais -> serveur -> client : HandshakeResult{ Ok=false } (aucune clé) puis fermeture
//
// Sécurité :
//   * La clé est générée côté serveur (élevé, de confiance) par un CSPRNG, éphémère (par connexion, en mémoire, jamais
//     persistée).
//   * Elle ne passe que par le pipe local FIRST_PIPE_INSTANCE protégé par DACL, après l'authentification : un pair qui
//     n'est pas DrDxt.App est refusé avant 3a.
//   * Le Hello ne porte aucun secret : le rejouer n'extrait rien, la clé n'est frappée qu'après l'auth.
// -----------------------------------------------------------------------

/// <summary>Salut client -> serveur : un marqueur de version de protocole seulement, pour rejeter un pair incompatible
/// ou déchet. Aucun secret.</summary>
public sealed record HandshakeHello
{
    /// <summary>Marqueur de protocole fermé. Incrémenté si la forme du handshake change un jour.</summary>
    public string Protocol { get; init; } = HandshakeProtocol.Version;
}

/// <summary>Issue serveur -> client : en succès, la clé de session éphémère (base64, 32 octets) ; en échec, aucune clé,
/// le client se ferme (fail-closed).</summary>
public sealed record HandshakeResult
{
    public bool Ok { get; init; }
    /// <summary>La clé de session de 32 octets en base64, seulement quand <see cref="Ok"/> et l'appelant
    /// authentifié.</summary>
    public string? SessionKeyBase64 { get; init; }
}

/// <summary>Constantes du protocole de handshake, source unique du marqueur envoyé et vérifié.</summary>
public static class HandshakeProtocol
{
    public const string Version = "drdxt-handshake-v1";

    /// <summary>Longueur en octets de la clé de session éphémère (clé du HMAC-SHA256).</summary>
    public const int SessionKeyBytes = 32;
}
