using System.Runtime.Versioning;
using DrDxt.Core;

namespace DrDxt.Reconciler.Manifest;

// -----------------------------------------------------------------------
// MachineManifestCommit — ce que fait le HELPER eleve d'un `CommitManifestOp` (11/09/2026).
//
// Les entrees sont deja validees par l'allowlist du helper quand elles arrivent ici (identifiants du
// catalogue, valeurs bornees, ce qui serait execute passe l'allowlist). Cette classe fait le reste :
//   1. charge la cle de la machine, la cree si elle n'existe pas (MachineManifestKey) ;
//   2. relit le manifeste existant par le meme chemin verifie que le reconciliateur ;
//   3. Fusionne (SignedDesiredStateStore.CommitAsync → DesiredState.Fusionner : premiere origine gagne),
//      retire ce qu'on lui demande de retirer (levier annule, 06/10/2026), signe, ecrit atomiquement ;
//   4. pose ou repare la cle publique a cote, pour l'App et le service.
//
// La regle de l'illisible. Un manifeste qui ne se relit pas (altere, signe par une autre cle — une
// ancienne, ou celle de dev —, JSON casse, ACL non fiable) n'est jamais ecrase : refus nomme
// ErrorCodes.ManifestUnreadable, rien n'est ecrit. Meme chose pour une cle inutilisable
// (ManifestKeyUnusable). La seule sortie est la REPRISE explicite (`repartir`) : l'illisible est mis
// de cote (renomme, jamais efface) et un manifeste neuf commence avec les entrees recues. Sur un
// manifeste LISIBLE, la reprise est refusee : on ne jette jamais des origines qu'on sait relire.
//
// Pourquoi pas « reconstruire depuis `Gardes` » : ce journal vit sous %LOCALAPPDATA%, ecrivable par
// tout processus du compte. Le rejouer ici ferait d'un simple fichier utilisateur une autorite SIGNEE
// que la desinstallation executerait elevee. La reprise part des entrees que l'App vient d'envoyer, et
// elle DIT que les origines de l'ancien manifeste ne seront plus rendues.
//
// Un seul appel a la fois : la boucle du helper sert une connexion apres l'autre.
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
    /// <param name="reference">11/09/2026 — le fichier du reconciliateur ou la reference de derive (build,
    /// pilote) est posee dans le meme geste que le manifeste (ApplyReferenceSeedingStore), par ce processus
    /// eleve. Absent (avec ses sondes) = pas de reference posee.</param>
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

    public async Task<Result> ExecuterAsync(DesiredState ajout, bool repartir, IReadOnlyCollection<string>? retirer = null)
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

            // Reprise : la cle et le manifeste qu'elle signait sont mis de cote (sans cle, il ne se relit plus).
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
        // 06/10/2026 : un retrait seul (levier annulé) n'inscrit rien.
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
