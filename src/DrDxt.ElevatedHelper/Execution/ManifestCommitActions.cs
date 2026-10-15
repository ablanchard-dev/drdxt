using System.Runtime.Versioning;
using DrDxt.Core;
using DrDxt.Reconciler.Manifest;

namespace DrDxt.ElevatedHelper.Execution;

/// <summary>Le geste d'ecriture du manifeste signe, derriere une couture comme les autres
/// actions du helper. Atteinte seulement apres HMAC → anti-rejeu → authentification de l'appelant → codec
/// borne → OpAllowlist (ValidateCommitManifest).</summary>
public interface IManifestCommitActions
{
    Result Commit(CommitManifestOp op);
}

/// <summary>Le vrai : cle de la machine, fusion, signature et ecriture sous <c>%ProgramData%\DrDxt</c>
/// (voir <see cref="MachineManifestCommit"/>).</summary>
[SupportedOSPlatform("windows")]
public sealed class ManifestCommitActions : IManifestCommitActions
{
    private readonly MachineManifestCommit _commit;

    public ManifestCommitActions() : this(ProgramDataManifestStorage.DefaultRoot(), appliquerAcl: true) { }

    /// <param name="appliquerAcl">Production : true. Les tests passent un dossier temporaire et false.</param>
    public ManifestCommitActions(string racine, bool appliquerAcl)
        => _commit = new MachineManifestCommit(racine, appliquerAcl, new DrDxt.Reconciler.SystemClock(),
            // La reference de derive est posee ici, processus eleve, dans le meme geste que le
            // manifeste ; l'App non elevee ne pouvait pas l'ecrire (et l'echec etait avale).
            new DrDxt.Reconciler.JsonReconcilerStateStore(Path.Combine(racine, "reconciler-state.json")),
            new SondeDeBuildWin32(), new SondeDePiloteWmi());

    // Simplification voulue : synchrone comme les autres actions ; le stockage l'est deja sous son Task.
    public Result Commit(CommitManifestOp op)
        => _commit.ExecuterAsync(
                new DesiredState(op.Tweaks, op.RevertOnly.Count == 0 ? null : op.RevertOnly),
                op.RepartirDUnManifesteVide, op.Retirer)
            .GetAwaiter().GetResult();
}
