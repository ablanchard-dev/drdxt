using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuWriteProbe : prouver, par carte et par pilote, qu'une écriture de réglage GPU prend
// vraiment, avant de régler quoi que ce soit.
//
// Pourquoi. DrDXT annonce « 2015-2026 ». Personne ne possède 14 générations de GPU : l'ancienne
// garde (« tester l'écriture sur chaque architecture ») ne pouvait jamais se fermer. La spec de
// recherche donnait déjà l'issue (drdxt-hardware-matrix-2015-2026.md, Key policy
// conclusions) : « GPU tuning writes must be capability-probed per driver and device.
// Architecture alone is not enough. »
//
// La sonde. Avant la vraie écriture, on rejoue l'op inverse du levier, qui porte la valeur
// tout juste relevée sur la carte, et on relit la valeur :
//   * l'écriture est à l'identique : elle remet la valeur courante sur elle-même, l'état
//     matériel ne change donc pas, que la sonde passe ou échoue,
//   * elle utilise la même op typée sur liste blanche que la vraie pose → aucune nouvelle
//     surface privilégiée, aucune nouvelle feuille d'allowlist, aucun changement sensible,
//   * la relecture prouve que l'écriture a atteint le silicium. Un pilote qui dit « réussi »
//     sans rien changer (puce verrouillée, mobile ou bridée par l'OEM, version de structure
//     refusée) est pris ici au lieu de produire un faux « appliqué » et un faux gain.
//
// Refus au moindre doute : tout ce qui n'est pas « écriture ok et relecture == valeur
// relevée » → TWEAK.UNSUPPORTED_ON_THIS_GPU, dit honnêtement, rien de réglé. Une carte
// jamais vue n'est pas refusée sur son nom : on l'interroge.
//
// heuristique : écriture à l'identique + relecture sur une seule valeur par levier. Elle ne
// prouve pas qu'un pilote accepte toute la plage (une carte peut accepter -20 MHz et borner
// -120). GpuSafeBounds, le test de stabilité et le retour automatique couvrent déjà ce sens ;
// passer à une sonde de la vraie valeur cible seulement si une carte accepte l'écriture à
// l'identique et borne en silence une vraie valeur.
// -----------------------------------------------------------------------
internal static class GpuWriteProbe
{
    /// <summary>Prouve que l'écriture de réglage marche sur ce périphérique en réécrivant l'origine
    /// relevée sur elle-même (sans effet pour le matériel) et en confirmant la relecture. Ok = le
    /// levier peut être posé. Fail = TWEAK.UNSUPPORTED_ON_THIS_GPU, et l'appelant ne doit pas écrire.</summary>
    /// <param name="identityOp">L'op inverse du levier (porte l'origine relevée).</param>
    /// <param name="readBack">Relit la même valeur par le SDK du fabricant.</param>
    /// <param name="captured">La valeur lue avant la sonde, celle que la relecture doit rendre.</param>
    /// <param name="lever">Nom lisible du levier, pour le message honnête.</param>
    public static async Task<Result> ConfirmWritableAsync(
        IElevatedClient elevated,
        PrivilegedOp identityOp,
        Func<Result<int>> readBack,
        int captured,
        string lever)
    {
        Result write;
        try
        {
            write = await elevated.ExecuteAsync(identityOp, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            write = Result.Fail(ErrorCodes.HelperOpFailed, ex.Message);
        }

        // Une écriture refusée porte déjà un code honnête et plus précis (helper absent, élévation
        // manquante, op rejetée par le pilote) : on le rend tel quel au lieu de requalifier une panne
        // d'infrastructure en verdict matériel. Le verdict propre de la sonde est réservé à ce
        // qu'elle seule voit : une écriture qui dit ok et ne fait rien.
        if (!write.IsOk)
            return write;

        Result<int> after;
        try
        {
            after = readBack();
        }
        catch (Exception ex)
        {
            return Result.Fail(ErrorCodes.TweakUnsupportedOnThisGpu,
                $"Could not confirm the {lever} write on this GPU (read-back error: {ex.Message}): " +
                "the lever is unavailable on this card. Nothing was changed.");
        }

        if (!after.IsOk)
            return Result.Fail(ErrorCodes.TweakUnsupportedOnThisGpu,
                $"Could not confirm the {lever} write on this GPU (read-back unavailable: {after.Error?.Code}): " +
                "the lever is unavailable on this card. Nothing was changed.");

        if (after.Value != captured)
            return Result.Fail(ErrorCodes.TweakUnsupportedOnThisGpu,
                $"The {lever} write reported success but did not take on this GPU " +
                $"(read back {after.Value}, expected {captured}): the lever is unavailable on this card. " +
                "Nothing was changed.");

        return Result.Ok();
    }
}
