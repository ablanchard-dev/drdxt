using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Application.Tweaks.Gpu;

// -----------------------------------------------------------------------
// GpuWriteProbe — prove, per card + driver, that a GPU tuning write ACTUALLY TAKES,
// before we tune anything (north-star plan §6.0, doctrine of 21/08/2026).
//
// Why this exists. DrDXT claims "2015-2026". Nobody owns 14 GPU generations, so the
// old gate ("test the write on every architecture") could never close. The research
// spec already gave the way out (drdxt-hardware-matrix-2015-2026.md, Key policy
// conclusions): "GPU tuning writes must be capability-probed per driver and device.
// Architecture alone is not enough."
//
// THE PROBE. Before the real write, replay the tweak's own inverse op — which carries
// the value we just captured from the card — and read the value back:
//   * the write is an IDENTITY write: it sets the current value to itself, so the
//     hardware state is unchanged whether the probe passes or fails,
//   * it uses the same allowlisted typed op as the real apply → no new privileged
//     surface, no new allowlist leaf, no security-sensitive change,
//   * read-back proves the write reached the silicon. A driver that reports success
//     and changes nothing (locked/mobile/OEM-capped part, refused struct version)
//     is caught here instead of producing a fake "applied" and a fake gain.
//
// FAIL-CLOSED: anything other than "write ok AND read-back == captured value" →
// TWEAK.UNSUPPORTED_ON_THIS_GPU, said honestly (§22.3), nothing tuned. A card we have
// never seen is not refused by name — it is ASKED.
//
// heuristique : identity-write + read-back on ONE scalar per lever. It does not prove a
// driver honours the full RANGE (a card could accept -20 MHz and clamp -120). The
// existing GpuSafeBounds + stability-test + auto-revert already cover that direction;
// upgrade to a probe of the real target value only if a card is found that takes the
// identity write and silently clamps a real one.
// -----------------------------------------------------------------------
internal static class GpuWriteProbe
{
    /// <summary>Proves the tuning write path is live on this device by writing the
    /// captured original back onto itself (a no-op for the hardware) and confirming
    /// the read-back. Ok = the lever may be applied. Fail = TWEAK.UNSUPPORTED_ON_THIS_GPU
    /// and the caller must not write.</summary>
    /// <param name="identityOp">The tweak's inverse op (carries the captured original).</param>
    /// <param name="readBack">Re-reads the same value from the vendor SDK.</param>
    /// <param name="captured">The value read before the probe — what read-back must return.</param>
    /// <param name="lever">Human-readable lever name for the honest message.</param>
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

        // A REFUSED write already carries an honest, more specific code (helper down,
        // elevation missing, driver rejected the op) — pass it through verbatim rather than
        // relabelling an infrastructure failure as a hardware verdict. The probe's own
        // verdict is reserved for what only it can see: a write that SAYS ok and does nothing.
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
