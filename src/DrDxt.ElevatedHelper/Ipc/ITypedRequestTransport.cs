namespace DrDxt.ElevatedHelper.Ipc;

// -----------------------------------------------------------------------
// ITypedRequestTransport : la couture de fil pour une requête typée signée.
//
// Sépare la logique client de sérialisation + signature (TypedNamedPipeElevatedClient) du
// transport réel (pipe nommé). Les tests injectent une boucle en mémoire vers un vrai
// TypedPrivilegedServer et exercent tout l'aller-retour à échec propre avec des faux : pas de
// vrai pipe, rien d'élevé.
// -----------------------------------------------------------------------
public interface ITypedRequestTransport
{
    Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct);
}
