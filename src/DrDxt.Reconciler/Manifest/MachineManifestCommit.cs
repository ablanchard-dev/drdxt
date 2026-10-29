using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// MachineManifestCommit : ce que fait le helper eleve d'un `CommitManifestOp`.
//
// Les entrees arrivent validees par l'allowlist du helper (identifiants du catalogue, valeurs bornees). Le reste :
//   1. charge la cle de la machine, la cree si elle n'existe pas (MachineManifestKey) ;
//   2. relit le manifeste existant par le meme chemin verifie que le reconciliateur ;
//   3. fusionne (SignedDesiredStateStore.CommitAsync → DesiredState.Fusionner : premiere origine gagne),
//      retire ce qu'on lui demande de retirer (levier annule), signe, ecrit atomiquement ;
//   4. pose ou repare la cle publique a cote, pour l'App et le service.
//
// Un manifeste illisible (altere, signe par une autre cle, une ancienne ou celle de dev, JSON casse, ACL non fiable)
// n'est jamais ecrase : ErrorCodes.ManifestUnreadable, rien d'ecrit ; de meme pour une cle inutilisable
// (ManifestKeyUnusable). Seule sortie : la reprise explicite (`repartir`), qui met l'illisible de cote (renomme, jamais
// efface) ; refusee sur un manifeste lisible. La reprise ne reconstruit pas depuis `Gardes` : sous %LOCALAPPDATA%,
// ecrivable par tout processus du compte, il deviendrait une autorite signee executee elevee a la desinstallation.
//
// Un seul appel a la fois, meme entre deux helpers (deux comptes) : un fichier verrou dans la racine de confiance.
// -----------------------------------------------------------------------
[SupportedOSPlatform("windows")]
public sealed class MachineManifestCommit
{
    private readonly string _racine;
    private readonly bool _appliquerAcl;
    private readonly IClock _horloge;
    private readonly IReconcilabilityPolicy _politique = new DefaultReconcilabilityPolicy();
    private readonly IReconcilerStateStore? _reference;
    private readonly IOsBuildProbe? _build;
    private readonly IGpuDriverProbe? _pilote;

    /// <param name="appliquerAcl">Production : true. Tests : false (dossier temporaire).</param>
    /// <param name="reference">Le fichier ou la reference de derive (build, pilote) est posee avec le manifeste
    /// (ApplyReferenceSeedingStore), par ce processus eleve. Absent = pas de reference posee.</param>
    public MachineManifestCommit(string racine, bool appliquerAcl, IClock horloge,
        IReconcilerStateStore? reference = null, IOsBuildProbe? build = null, IGpuDriverProbe? pilote = null)
    {
        _racine = racine;
        _appliquerAcl = appliquerAcl;
        _horloge = horloge;
        _reference = reference;
        _build = build;
        _pilote = pilote;
    }

    /// <summary>Le verrou des écritures du manifeste et de sa clé : un fichier exclusif dans la racine de confiance, où
    /// seuls les administrateurs créent un fichier (un mutex nommé pourrait être pris d'avance sans droits).</summary>
    public const string NomDuVerrou = "manifeste.verrou";

    private static readonly TimeSpan AttenteDuVerrou = TimeSpan.FromSeconds(10);

    public async Task<Result> ExecuterAsync(DesiredState ajout, bool repartir, IReadOnlyCollection<string>? retirer = null)
    {
        // La racine est durcie (sans point de reparse, propriétaire Administrateurs) avant d'y ouvrir le verrou.
        if (_appliquerAcl)
        {
            var racine = TrustedManifestRoot.EnsureCreatedHardened(_racine);
            if (!racine.IsOk) return racine;
        }
        else Directory.CreateDirectory(_racine);
        var chemin = Path.Combine(_racine, NomDuVerrou);
        var fin = DateTime.UtcNow + AttenteDuVerrou;
        FileStream? verrou = null;
        while (verrou is null)
        {
            try { verrou = new FileStream(chemin, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (DateTime.UtcNow < fin) { await Task.Delay(50).ConfigureAwait(false); }
            catch (IOException ex) { return Result.Fail(ErrorCodes.IoFailure, $"Manifest is busy: {ex.Message}"); }
        }
        using (verrou) return await ExecuterSousVerrouAsync(ajout, repartir, retirer).ConfigureAwait(false);
    }

    private async Task<Result> ExecuterSousVerrouAsync(DesiredState ajout, bool repartir, IReadOnlyCollection<string>? retirer)
    {
        var stockage = new ProgramDataManifestStorage(_racine, _appliquerAcl);

        var cle = MachineManifestKey.ChargerOuCreer(_racine, _appliquerAcl);
        var dejaMisDeCote = false;
        if (!cle.IsOk)
        {
            if (cle.Error!.Value.Code != ErrorCodes.ManifestKeyUnusable)
                return Result.Fail(cle.Error.Value.Code, cle.Error.Value.Message);
            if (!repartir)
                return Result.Fail(ErrorCodes.ManifestKeyUnusable, cle.Error.Value.Message + " Nothing was written.");

            // Reprise : cle et manifeste mis de cote ; ce que la cle publique relit encore est re-signe par la nouvelle.
            DesiredState? repris = null;
            var verificateur = MachineManifestKey.ChargerVerificateur(_racine, _appliquerAcl);
            if (verificateur.IsOk)
            {
                var lu = await new SignedDesiredStateStore(stockage, signer: null, verificateur.Value!, _politique, _horloge)
                    .LoadAsync().ConfigureAwait(false);
                if (lu.IsOk) repris = lu.Value;
            }
            if (repris is not null) ajout = DesiredState.Fusionner(repris, ajout);

            var cleDeCote = MachineManifestKey.MettreDeCote(_racine);
            if (!cleDeCote.IsOk) return cleDeCote;
            var manifesteDeCote = stockage.MettreDeCote();
            if (!manifesteDeCote.IsOk) return manifesteDeCote;
            cle = MachineManifestKey.ChargerOuCreer(_racine, _appliquerAcl);
            if (!cle.IsOk) return Result.Fail(cle.Error!.Value.Code, cle.Error.Value.Message);
            dejaMisDeCote = true;
        }

        var paire = cle.Value!;
        var magasin = new SignedDesiredStateStore(stockage, paire.Signataire, paire.Verificateur, _politique, _horloge);

        if (!dejaMisDeCote)
        {
            var existant = await magasin.LoadManifestAsync().ConfigureAwait(false);
            if (!existant.IsOk)
            {
                if (!repartir)
                    return Result.Fail(ErrorCodes.ManifestUnreadable,
                        $"The existing signed manifest cannot be read ({existant.Error!.Value.Code}: "
                        + $"{existant.Error.Value.Message}). Nothing was written; it can be set aside explicitly.");
                var deCote = stockage.MettreDeCote();
                if (!deCote.IsOk) return deCote;
            }
            else if (repartir)
            {
                return Result.Fail(ErrorCodes.OpNotAllowed,
                    "The existing signed manifest reads fine: it is never set aside.");
            }
        }

        IDesiredStateStore ecrivain = _reference is not null && _build is not null && _pilote is not null
            ? new ApplyReferenceSeedingStore(magasin, _reference, _build, _pilote, _politique)
            : magasin;
        // Un retrait seul (levier annulé) n'inscrit rien.
        var commit = ajout.Tweaks.Count + (ajout.RevertOnly?.Count ?? 0) == 0 && retirer is { Count: > 0 }
            ? Result.Ok()
            : await ecrivain.CommitAsync(ajout).ConfigureAwait(false);
        // Une reference non posee n'empeche pas de publier la cle : le manifeste, lui, est ecrit.
        if (!commit.IsOk && commit.Error!.Value.Code != ErrorCodes.ManifestReferenceNotSeeded)
            return commit;
        if (retirer is { Count: > 0 })
        {
            var retire = await magasin.RetirerAsync(retirer.ToList()).ConfigureAwait(false);
            if (!retire.IsOk) return retire;
        }

        var publiee = MachineManifestKey.PublierClePublique(_racine, _appliquerAcl, paire.ClePublique);
        if (!publiee.IsOk) return publiee;

        return commit;
    }
}
