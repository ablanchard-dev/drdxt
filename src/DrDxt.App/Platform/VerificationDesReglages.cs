using DrDxt.App.ViewModels;
using DrDxt.Application.Tweaks;
using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;
using DrDxt.Reconciler.Desinstallation;
using Microsoft.Extensions.DependencyInjection;

namespace DrDxt.App.Platform;

/// <summary>
/// Verifie au lancement que les reglages poses par DrDXT tiennent. Un autre optimiseur, une mise a jour
/// Windows ou l'utilisateur peuvent defaire un reglage ; le produit promet « persistant », il doit le remarquer.
///
/// Au lancement, chaque reglage du manifeste signe est relu par le verificateur existant
/// (<see cref="AppliedStateVerifier"/>) contre ce que DrDXT a pose (<see cref="RenduALaDesinstallation.PoseesDe"/>,
/// la meme regle que la desinstallation). Ce qui a bouge est dit ; rien n'est reecrit (regle I5 : on
/// notifie, on n'ecrase pas). Lecture seule, aucun service, aucune elevation.
/// Les reglages HKCU (Game Mode, Game DVR, modele flip) ne sont pas dans le manifeste.
/// </summary>
public static class VerificationDesReglages
{
    /// <summary>La preuve de chaque reglage garde. Manifeste illisible ⇒ liste vide (on ne dit rien de faux).</summary>
    public static async Task<IReadOnlyList<AppliedStateEvidence>> VerifierAsync(IDesiredStateStore manifeste,
        IRegistryReader registre, INvApi? nvApi = null, IPowerPlanReader? alimentation = null)
    {
        var charge = await manifeste.LoadAsync().ConfigureAwait(false);
        if (!charge.IsOk || charge.Value is not { } etat)
            return Array.Empty<AppliedStateEvidence>();

        var entrees = etat.Tweaks.Select(t => (t, Correction: true))
            .Concat((etat.RevertOnly ?? Array.Empty<AppliedTweak>()).Select(t => (t, Correction: false)));

        return entrees.Select(e =>
        {
            var posees = RenduALaDesinstallation.PoseesDe(e.t, e.Correction);
            var appliquee = posees switch
            {
                null or { Count: 0 } => null,
                { Count: 1 } => PrivilegedOpCodec.Serialize(posees[0]).Value,
                _ => CompositeSnapshotCodec.Encode(posees).Value,
            };
            return AppliedStateVerifier.Verifier(
                new TweakSnapshot(e.t.TweakId, e.t.OriginalValueJson, appliquee), registre, nvApi, alimentation);
        }).ToList();
    }

    /// <summary>Une ligne par reglage qui a bouge (change par autre chose, ou revenu a l'origine), ou
    /// <c>null</c> quand tout tient. « Inconnu » et « illisible » ne sont pas des derives : on ne les
    /// presente pas comme telles.</summary>
    public static string? Dire(IReadOnlyList<AppliedStateEvidence> preuves, Func<string, string> localiser)
    {
        var lignes = preuves
            .Where(p => p.Verdict is EtatApplique.Drifted or EtatApplique.NotApplied)
            .Select(p => string.Format(System.Globalization.CultureInfo.CurrentUICulture,
                localiser("reconciler.notify.user_drift"), localiser(TweakDisplayKeys.TitleKey(p.TweakId))))
            .ToList();
        return lignes.Count == 0 ? null : string.Join(Environment.NewLine, lignes);
    }

    /// <summary>Appele apres l'ouverture de la fenetre. Ne bloque jamais le demarrage, ne leve jamais.</summary>
    public static async Task AuLancementAsync(IServiceProvider services, ScanViewModel ecran)
    {
        try
        {
            var localiseur = services.GetRequiredService<ILocalizer>();
            var preuves = await Task.Run(() => VerifierAsync(
                services.GetRequiredService<IDesiredStateStore>(),
                services.GetRequiredService<IRegistryReader>(),
                services.GetService<INvApi>(),
                services.GetService<IPowerPlanReader>())).ConfigureAwait(true);
            ecran.ReglagesModifies = Dire(preuves, k => localiseur.Get(k));
        }
        catch
        {
            // La verification au lancement ne doit jamais empecher l'application de s'ouvrir.
        }
    }
}
