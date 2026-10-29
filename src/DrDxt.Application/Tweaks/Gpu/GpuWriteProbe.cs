using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuWriteProbe : prouver, par carte et par pilote, qu'une écriture de réglage GPU prend vraiment, avant de régler quoi
// que ce soit. Personne ne possède 14 générations de GPU (« 2015-2026 ») ; la spec de recherche
// (drdxt-hardware-matrix-2015-2026.md, Key policy conclusions) : « GPU tuning writes must be capability-probed per driver
// and device. Architecture alone is not enough. »
//
// La sonde rejoue l'op inverse du levier, qui porte la valeur tout juste relevée, puis relit :
//   * à l'identique : l'état matériel ne change pas, que la sonde passe ou échoue ;
//   * même op typée sur liste blanche que la vraie pose : aucune nouvelle surface privilégiée ni feuille d'allowlist ;
//   * la relecture prouve que l'écriture a atteint le silicium : un pilote qui dit « réussi » sans rien changer (puce
//     verrouillée, mobile ou bridée par l'OEM, version de structure refusée) ne produit ni faux « appliqué » ni faux gain.
// Au moindre doute → TWEAK.UNSUPPORTED_ON_THIS_GPU, rien de réglé. Une carte jamais vue n'est pas refusée sur son nom.
//
// heuristique : une seule valeur par levier, pas toute la plage (une carte peut accepter -20 MHz et borner -120) ;
// GpuSafeBounds, le test de stabilité et le retour automatique couvrent ce sens. Sonder la vraie valeur cible seulement
// si une carte accepte l'écriture à l'identique et borne en silence une vraie valeur.
// -----------------------------------------------------------------------
internal static class GpuWriteProbe
{
    /// <summary>Réécrit l'origine relevée sur elle-même et confirme la relecture. Ok = le levier peut être posé ; Fail =
    /// TWEAK.UNSUPPORTED_ON_THIS_GPU, l'appelant n'écrit pas.</summary>
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

        // Une écriture refusée garde son code (helper absent, élévation manquante, op rejetée) : une panne d'infrastructure
        // n'est pas un verdict matériel. Le verdict de la sonde est réservé à une écriture qui dit ok et ne fait rien.
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
