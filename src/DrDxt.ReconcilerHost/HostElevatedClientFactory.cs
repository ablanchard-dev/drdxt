using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.ElevatedHelper;
using DrDxt.ElevatedHelper.Ipc;

namespace DrDxt.ReconcilerHost;

// -----------------------------------------------------------------------
// HostElevatedClientFactory : construit l'IElevatedClient du contexte service.
//
// Reprend l'ElevatedClientFactory de l'App, mais pour le service d'arrière-plan : quand une clé
// HMAC par session est fournie (base64, >= 32 octets), il rend le vrai
// TypedNamedPipeElevatedClient sur le pipe nommé protégé par ACL ; sinon un client
// « indisponible » à échec propre qui refuse chaque op (Result.Fail), pour qu'une tentative de
// réconciliation journalise « élévation indisponible » et échoue proprement : jamais de
// plantage, jamais de fausse élévation réussie.
//
// État réel : ni Create ni CreateConnectOnly ne sont branchés en production. L'hôte utilise
// l'exécuteur en processus (InProcessElevatedClient, option B, voir
// HostServiceCollectionExtensions). CreateConnectOnly (handshake en connexion seule vers un
// service helper déjà en marche, sans UAC) existe et est testé : c'est le durcissement V2 gardé.
// Create, par clé fournie dans l'environnement, reste le mode historique : sans clé, il rend le
// client indisponible. Aucune session n'est fabriquée.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public static class HostElevatedClientFactory
{
    /// <summary>La variable d'environnement qui porte la clé HMAC par session en base64 (même
    /// contrat que le mode historique côté App). Rien ne la pose dans le contexte service : vide →
    /// client indisponible.</summary>
    public const string SessionKeyEnv = "DRDXT_HELPER_SESSION_KEY";

    /// <summary>Construit le client. <paramref name="keySource"/> lit la clé de session brute en
    /// base64 (injectée pour tester le chemin d'échec propre sans toucher au vrai environnement) ; la
    /// production passe le lecteur de variable d'environnement.</summary>
    public static IElevatedClient Create(Func<string?> keySource)
    {
        var key = ParseKey(keySource());
        if (key is null)
            return new UnavailableElevatedClient();

        var transport = new NamedPipeTypedRequestTransport(PipeSecurityFactory.DefaultPipeName);
        return new TypedNamedPipeElevatedClient(key, transport);
    }

    /// <summary>Surcharge de production : lit la clé de session dans l'environnement.</summary>
    public static IElevatedClient Create()
        => Create(() => Environment.GetEnvironmentVariable(SessionKeyEnv));

    /// <summary>Construit le client réconciliateur→helper en connexion seule.
    ///
    /// Le service peu privilégié ne lance jamais le helper (pas d'UAC) : le <see cref="HandshakingElevatedClient"/> nu se
    /// connecte au helper déjà en marche, avec une clé éphémère négociée comme l'App. Sans helper à l'écoute, l'échéance
    /// bornée passe et il rend HELPER_UNREACHABLE : le réconciliateur dit « élévation indisponible » et ne réapplique pas.
    /// Le serveur revérifie l'image de l'appelant (défense en profondeur).</summary>
    /// <param name="pipeName">Surcharge pour les tests ; par défaut le pipe standard protégé par ACL.</param>
    /// <param name="connectTimeoutMs">Échéance de reprise de connexion ; le test unitaire prend une
    /// valeur courte pour que le chemin d'échec propre sans helper rende vite la main.</param>
    /// <param name="expectedHelperImagePath">Le chemin du helper que ce client accepte
    /// d'ecouter. Par defaut celui pose a cote du service (meme source de verite que l'App).
    /// S'il est introuvable, la resolution rend null et le client refuse toute session plutot
    /// que de faire confiance a un tube quelconque : c'est le sens fail-closed de la garde.</param>
    public static IElevatedClient CreateConnectOnly(
        string? pipeName = null, int connectTimeoutMs = 5000, string? expectedHelperImagePath = null)
        => new HandshakingElevatedClient(
            pipeName ?? PipeSecurityFactory.DefaultPipeName,
            expectedHelperImagePath ?? PipeServerIdentity.TryResolveExpectedHelperPath(),
            connectTimeoutMs);

    private static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64))
            return null;
        try
        {
            var key = Convert.FromBase64String(base64);
            return key.Length >= 32 ? key : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Client à échec propre rendu par Create quand aucune clé de session n'est fournie :
    /// refuse chaque op pour qu'une réconciliation échoue proprement au lieu de planter. Ne rapporte
    /// jamais un faux succès.</summary>
    private sealed class UnavailableElevatedClient : IElevatedClient
    {
        public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) =>
            Task.FromResult(Result.Fail(ErrorCodes.ElevationDenied,
                "Elevation unavailable: the reconciler service has no provisioned helper session."));
    }
}
