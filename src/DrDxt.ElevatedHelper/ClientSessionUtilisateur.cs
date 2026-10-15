using DrDxt.Core;
using DrDxt.ElevatedHelper.Execution;

namespace DrDxt.ElevatedHelper;

/// <summary>
/// Une valeur de la session de l'utilisateur (HKCU) s'écrit avec le jeton de l'App, jamais par le helper
/// administrateur. Élevé avec le compte d'un autre (UAC « par-dessus l'épaule »), le helper écrirait la ruche de cet autre
/// compte. Le code d'écriture reste celui du helper (sa liste blanche, ses actions de registre), exécuté dans le processus
/// de l'App pour la seule session de l'utilisateur ; tout le reste passe au helper.
/// </summary>
public sealed class ClientSessionUtilisateur(IElevatedClient eleve, IRegistryActions? registre = null) : IElevatedClient
{
    private static readonly OpAllowlist Liste = new();

    /// <summary>Le client qui porte tout le reste au helper.</summary>
    public IElevatedClient VersLeHelper => eleve;
    private readonly IRegistryActions _registre = registre ?? new Win32RegistryActions();

    public Task<Result> ExecuteAsync(PrivilegedOp op, CancellationToken ct) => op switch
    {
        SetRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } s => Task.FromResult(
            Liste.Validate(s) is { IsOk: false } refus ? refus : _registre.SetValue(s.Hive, s.Path, s.Name, s.Kind, s.Value)),
        DeleteRegistryValueOp { Hive: RegistryHiveKind.CurrentUser } d => Task.FromResult(
            Liste.Validate(d) is { IsOk: false } refus ? refus : _registre.DeleteValue(d.Hive, d.Path, d.Name)),
        // Un programme au démarrage de la session : sa valeur et son dossier Démarrage sont ceux de l'utilisateur.
        SetStartupApprovalOp { Hive: RegistryHiveKind.CurrentUser } a => Task.FromResult(
            Liste.Validate(a) is { IsOk: false } refus ? refus : _registre.SetStartupApproval(a.Hive, a.Liste, a.Nom, a.Actif)),
        _ => eleve.ExecuteAsync(op, ct),
    };

    // Les lectures (santé de Windows, variables firmware) vont au helper ; sans cette ligne,
    // la méthode par défaut de l'interface répondait « pas de canal de retour ».
    public Task<Result<byte[]>> ReadAsync(PrivilegedOp op, CancellationToken ct) => eleve.ReadAsync(op, ct);
}
