namespace DrDxt.ElevatedHelper.Ipc;

// ITypedRequestTransport : la couture de fil d'une requête typée signée, entre la sérialisation + signature
// (TypedNamedPipeElevatedClient) et le pipe nommé. En test, une boucle en mémoire vers un vrai TypedPrivilegedServer :
// tout l'aller-retour, sans pipe ni élévation.
public interface ITypedRequestTransport
{
    Task<PrivilegedResponse> SendAsync(TypedPrivilegedRequest request, CancellationToken ct);
}
