using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Display;

/// <summary>
/// La lecture des écrans, gardée un court instant. Lire les écrans coûte 40 à 57 ms (mesuré le 25/10/2026 sur la machine de
/// référence : les modes de chaque écran, la lecture VRR au pilote, tous les écrans pour en garder un), et un scan les relit
/// plusieurs fois en quelques millisecondes : chaque levier qui en dépend, AppliesTo puis EtatNonLu. Deux secondes couvrent
/// ces rafales ; un changement de fréquence fait dans Windows se voit au scan suivant. Un échec ne se garde pas : la lecture
/// suivante réessaie.
/// </summary>
public sealed class EcransEnCache(IDisplayInfo source, Func<DateTime>? horloge = null) : IDisplayInfo
{
    public static readonly TimeSpan Duree = TimeSpan.FromSeconds(2);

    private readonly Func<DateTime> _horloge = horloge ?? (() => DateTime.UtcNow);
    private readonly object _verrou = new();
    private (DateTime Quand, IReadOnlyList<DisplayInfo> Ecrans)? _lu;

    public Result<IReadOnlyList<DisplayInfo>> GetAllDisplays()
    {
        lock (_verrou)
        {
            var maintenant = _horloge();
            if (_lu is { } lu && maintenant - lu.Quand <= Duree) return Result<IReadOnlyList<DisplayInfo>>.Ok(lu.Ecrans);
            var r = source.GetAllDisplays();
            _lu = r.IsOk ? (maintenant, r.Value!) : null;
            return r;
        }
    }

    /// <summary>Le principal parmi les écrans lus, comme la source le rend ; aucun principal : rien n'est deviné.</summary>
    public Result<DisplayInfo> GetPrimaryGameDisplay()
    {
        var tous = GetAllDisplays();
        if (!tous.IsOk) return Result<DisplayInfo>.Fail(tous.Error!.Value.Code, tous.Error.Value.Message);
        return tous.Value!.FirstOrDefault(d => d.IsPrimaryGameDisplay) is { } principal
            ? Result<DisplayInfo>.Ok(principal)
            : Result<DisplayInfo>.Fail(ErrorCodes.TweakSdkUnavailable, "No primary display among the attached displays.");
    }
}
