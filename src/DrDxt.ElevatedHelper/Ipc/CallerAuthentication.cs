using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// Couture d'autorisation de l'appelant.
//
// Sécurité (« jamais d'auth stubée en build distribuable ») :
//   * L'autorisation est une question distincte de l'intégrité du transport (HMAC).
//     Le HMAC prouve « les octets sont intacts et le pair détient la clé de session » ;
//     cette couture décide « le pair qui se connecte a-t-il seulement le droit de lancer des
//     ops privilégiées », c'est-à-dire est-ce vraiment notre DrDxt.App.
//   * L'implémentation de production / par défaut refuse par défaut. Elle ne doit jamais tout
//     autoriser en silence dans un build distribuable. Les tests injectent un faux qui autorise.
// -----------------------------------------------------------------------

/// <summary>Contexte de l'appelant pour une décision d'autorisation. Porte le process id du
/// pair qui se connecte (résolu depuis le pipe via GetNamedPipeClientProcessId) pour que
/// l'authentificateur vérifie que le pair est notre DrDxt.App. Un pid null veut dire que le
/// pid n'a pas pu être résolu → refus.</summary>
public readonly record struct CallerInfo(int? ClientProcessId = null);

/// <summary>Décision d'autorisation en refus au moindre doute (fail-closed) pour un pair qui se connecte.</summary>
public interface ICallerAuthenticator
{
    Result Authorize(CallerInfo caller);
}

/// <summary>
/// Authentificateur fail-closed par défaut. Refuse tout appelant. Sert de repli sûr et dans
/// les tests du chemin de refus ; un vrai hôte élevé branche l'authentificateur du processus
/// App (DrDxtAppCallerAuthenticator).
/// </summary>
public sealed class DenyByDefaultCallerAuthenticator : ICallerAuthenticator
{
    public Result Authorize(CallerInfo caller) =>
        Result.Fail(ErrorCodes.ElevationDenied, "Caller authorization denied (deny-by-default).");
}

/// <summary>
/// Autorise un pair qui correspond à l'une quelconque d'un ensemble d'images d'appelants de
/// confiance : le helper doit accepter plusieurs appelants distincts (le DrDxt.App interactif et
/// l'hôte réconciliateur peu privilégié), chacun avec son authentificateur chemin + SHA-256.
///
/// Sécurité (canal multi-appelant) : le pair est exactement un processus, donc un « ou » logique sur les
/// authentificateurs par image est correct : il autorise si et seulement si le pair est l'une des
/// images de confiance. Fail-closed par construction :
///   * un ensemble vide n'autorise rien (refus par défaut : aucune image de confiance fournie) ;
///   * un pair qui ne correspond à aucun membre → refus (tiers, ou binaire remplacé à un chemin
///     de confiance : chaque membre impose aussi le SHA-256, le chemin seul ne gagne jamais) ;
///   * un membre qui lève compte comme « n'a pas autorisé » (défense en profondeur) : un membre
///     défaillant ne fait jamais planter la décision, et n'autorise jamais en silence.
/// </summary>
public sealed class CompositeCallerAuthenticator : ICallerAuthenticator
{
    private readonly ICallerAuthenticator[] _members;

    public CompositeCallerAuthenticator(params ICallerAuthenticator[] members)
    {
        ArgumentNullException.ThrowIfNull(members);
        // Copie défensive ; refuser les entrées null pour qu'un null ne court-circuite ni ne lève jamais.
        _members = members.ToArray();
        if (Array.IndexOf(_members, null) >= 0)
            throw new ArgumentNullException(nameof(members), "Authenticator members must be non-null.");
    }

    public CompositeCallerAuthenticator(IEnumerable<ICallerAuthenticator> members)
        : this((members ?? throw new ArgumentNullException(nameof(members))).ToArray()) { }

    public Result Authorize(CallerInfo caller)
    {
        foreach (var member in _members)
        {
            Result decision;
            try
            {
                decision = member.Authorize(caller);
            }
            catch
            {
                // Un membre qui lève n'autorise jamais et ne fait jamais planter la décision.
                continue;
            }

            if (decision.IsOk)
                return Result.Ok();
        }

        // Aucun membre n'a autorisé le pair (ou il n'y en avait aucun) → refus, fail-closed.
        return Result.Fail(ErrorCodes.ElevationDenied,
            "Caller authorization denied (not one of the trusted caller images).");
    }
}

/// <summary>
/// N'autorise un pair qui se connecte que si l'image de son processus est l'exécutable
/// DrDxt.App de confiance.
///
/// Sécurité : le pipe est déjà limité par ACL à l'utilisateur courant, donc le pair tourne sous
/// le même utilisateur. On ajoute par-dessus une vérification d'identité de processus :
///   * résoudre le PID du client (depuis le pipe) → le chemin d'image de son module principal,
///     et exiger qu'il égale le chemin DrDxt.App attendu (complet, sans tenir compte de la casse) ;
///   * (durci) exiger aussi que le SHA-256 de l'image égale celui de l'App attendue : un autre
///     binaire posé au même chemin est rejeté. C'est la preuve d'identité d'un build de dev non
///     signé. Utiliser <see cref="WithImageIdentity"/>.
/// Tout ce qui est incertain (pas de PID, chemin ou empreinte non résolus, désaccord, résolveur
/// qui lève) refuse (fail-closed ; jamais fail-open).
///
/// Limite connue tant que l'App n'est pas signée : remplacer l'égalité d'empreinte d'image par une
/// vérification Authenticode complète de l'image de l'appelant (WinVerifyTrust + épinglage de
/// notre certificat d'éditeur, une fois DrDxt.App signé). L'empreinte d'image en tient lieu au
/// niveau dev d'ici là ; la forme de la couture ne changera pas.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DrDxtAppCallerAuthenticator : ICallerAuthenticator
{
    private readonly string _expectedImagePath;
    private readonly byte[]? _expectedImageHash;        // null → chemin seul (historique, niveau dev)
    private readonly Func<int, string?> _resolveImagePath;
    private readonly ICallerImageIdentity? _imageIdentity; // non-null → hardened path

    /// <summary>Authentificateur par chemin seul (historique, niveau dev). Gardé pour les appelants
    /// et tests qui ne vérifient que le chemin d'image. Préférer <see cref="WithImageIdentity"/>.</summary>
    /// <param name="expectedImagePath">Chemin complet de l'exe DrDxt.App de confiance.</param>
    /// <param name="resolveImagePath">Relie un PID client → le chemin d'image de son module
    /// principal. Le vrai résolveur Win32 par défaut ; les tests injectent un faux.</param>
    public DrDxtAppCallerAuthenticator(string expectedImagePath, Func<int, string?>? resolveImagePath = null)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath))
            throw new ArgumentException("Expected image path must be non-empty.", nameof(expectedImagePath));
        _expectedImagePath = Path.GetFullPath(expectedImagePath);
        _resolveImagePath = resolveImagePath ?? DefaultResolveImagePath;
        _expectedImageHash = null;
        _imageIdentity = null;
    }

    private DrDxtAppCallerAuthenticator(
        string expectedImagePath, byte[] expectedImageHash, ICallerImageIdentity imageIdentity)
    {
        if (string.IsNullOrWhiteSpace(expectedImagePath))
            throw new ArgumentException("Expected image path must be non-empty.", nameof(expectedImagePath));
        if (expectedImageHash is null || expectedImageHash.Length == 0)
            throw new ArgumentException("Expected image hash must be non-empty.", nameof(expectedImageHash));
        _expectedImagePath = Path.GetFullPath(expectedImagePath);
        _expectedImageHash = (byte[])expectedImageHash.Clone();
        _imageIdentity = imageIdentity ?? throw new ArgumentNullException(nameof(imageIdentity));
        _resolveImagePath = DefaultResolveImagePath; // inutilisé sur le chemin durci
    }

    /// <summary>Authentificateur durci : exige que le chemin d'image du pair et son SHA-256
    /// correspondent à l'App attendue. La barrière d'identité du build de dev (ferme le trou de
    /// l'exe remplacé au même chemin). <paramref name="imageIdentity"/> vaut par défaut le vrai
    /// résolveur Win32 chemin + empreinte.</summary>
    public static DrDxtAppCallerAuthenticator WithImageIdentity(
        string expectedImagePath, byte[] expectedImageHash, ICallerImageIdentity? imageIdentity = null) =>
        new(expectedImagePath, expectedImageHash, imageIdentity ?? new Win32CallerImageIdentity());

    public Result Authorize(CallerInfo caller)
    {
        // Aucun PID résolu → pair non identifiable → refus.
        if (caller.ClientProcessId is not { } pid || pid <= 0)
            return Deny("Could not resolve the connecting client's process id.");

        return _imageIdentity is null
            ? AuthorizeByPath(pid)
            : AuthorizeByImageIdentity(pid);
    }

    // Historique, niveau dev : égalité de chemin seulement.
    private Result AuthorizeByPath(int pid)
    {
        string? actual;
        try
        {
            actual = _resolveImagePath(pid);
        }
        catch
        {
            // Un résolveur qui lève (processus terminé, accès refusé…) → refus.
            return Deny("Could not resolve the connecting client's image path.");
        }

        if (string.IsNullOrWhiteSpace(actual))
            return Deny("Connecting client has no resolvable image path.");

        string normalized;
        try
        {
            normalized = Path.GetFullPath(actual);
        }
        catch
        {
            return Deny("Connecting client image path is malformed.");
        }

        return PathMatches(normalized)
            ? Result.Ok()
            : Deny("Connecting client is not the trusted DrDxt.App process.");
    }

    // Durci : égalité de chemin et égalité d'empreinte d'image (en temps constant).
    private Result AuthorizeByImageIdentity(int pid)
    {
        CallerImage? resolved;
        try
        {
            resolved = _imageIdentity!.Resolve(pid);
        }
        catch
        {
            return Deny("Could not resolve the connecting client's image identity.");
        }

        if (resolved is not { } image)
            return Deny("Connecting client image identity is unresolved.");

        string normalized;
        try
        {
            normalized = Path.GetFullPath(image.Path);
        }
        catch
        {
            return Deny("Connecting client image path is malformed.");
        }

        if (!PathMatches(normalized))
            return Deny("Connecting client is not the trusted DrDxt.App process.");

        // Aucune empreinte résolue (fichier illisible) → identité improuvable → refus.
        if (image.Sha256 is null || image.Sha256.Length == 0)
            return Deny("Connecting client image could not be hashed for identity.");

        return System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   image.Sha256, _expectedImageHash!)
            ? Result.Ok()
            : Deny("Connecting client image does not match the trusted DrDxt.App binary.");
    }

    private bool PathMatches(string normalizedActual) =>
        string.Equals(normalizedActual, _expectedImagePath, StringComparison.OrdinalIgnoreCase);

    private static string? DefaultResolveImagePath(int pid)
    {
        using var proc = System.Diagnostics.Process.GetProcessById(pid);
        return proc.MainModule?.FileName;
    }

    private static Result Deny(string message) => Result.Fail(ErrorCodes.ElevationDenied, message);
}
