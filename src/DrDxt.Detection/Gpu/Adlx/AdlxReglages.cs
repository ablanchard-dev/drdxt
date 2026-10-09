using DrDxt.Application.Tweaks.Hardware;
using DrDxt.Core;

namespace DrDxt.Detection.Gpu.Adlx;

/// <summary>A1 (06/10/2026) : le lecteur ADLX partagé du produit. Chargé seulement si le pilote AMD a posé
/// amdadlx64.dll dans System32 : sans elle, aucun appel natif, et chaque lecture rend « non lu » nommé.
/// Une seule instance par processus (un seul ADLXInitialize).
/// Source : les en-têtes du SDK ADLX, https://github.com/GPUOpen-LibrariesAndSDKs/ADLX.</summary>
public static class AdlxReglages
{
    public const string DllAbsente = "amdadlx64.dll absente de System32 : pas de pilote AMD (Adrenalin) sur ce PC.";

    public static bool DllPresente()
    {
        try { return File.Exists(Path.Combine(Environment.SystemDirectory, "amdadlx64.dll")); }
        catch (Exception) { return false; }
    }

    private static readonly Lazy<AdlxNative?> _partage = new(() => DllPresente() ? new AdlxNative() : null);

    /// <summary>L'instance native partagée, ou null quand la DLL manque.</summary>
    public static AdlxNative? Partage => _partage.Value;

    /// <summary>Lit les réglages de la carte AMD dédiée. Ne lève jamais.</summary>
    public static AdlxReglagesLus Lire() => Lire(DllPresente() ? Partage : null);

    /// <summary>Même lecture sur un natif donné (null = DLL absente).</summary>
    public static AdlxReglagesLus Lire(IAdlxNative? natif)
    {
        if (natif is null) return AdlxReglagesLus.RienLu(DllAbsente);
        try { return natif.LireReglages(); }
        catch (Exception ex) { return AdlxReglagesLus.RienLu($"Lecture ADLX interrompue : {ex.GetType().Name}."); }
    }
}

/// <summary>La couture de latence AMD (<see cref="IAdlx"/>) sur le lecteur réel : Anti-Lag et limite d'images
/// lus, ou Fail(TWEAK.SDK_UNAVAILABLE) avec la raison — jamais une valeur par défaut.</summary>
public sealed class AdlxLatencyReader(Func<AdlxReglagesLus> lire) : IAdlx
{
    public bool IsAvailable => lire().Raison is null;

    public Result<int> GetAntiLagState()
    {
        var l = lire();
        return l.AntiLag is { } on
            ? Result<int>.Ok(on ? 1 : 0)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "Anti-Lag non lu par ADLX.");
    }

    public Result<int> GetFrameRateLimit()
    {
        var l = lire();
        return l.LimiteImages is { } fps
            ? Result<int>.Ok(fps)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "Limite d'images (FRTC/Chill) non lue par ADLX.");
    }

    public Result<int> GetFrameRateTarget()
    {
        var l = lire();
        return l.FrtcCode is { } code
            ? Result<int>.Ok(code)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "FRTC non lu par ADLX.");
    }

    public Result<int> GetVsyncMode()
    {
        var l = lire();
        return l.VsyncMode is { } m
            ? Result<int>.Ok(m)
            : Result<int>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "Vsync du pilote AMD non lue par ADLX.");
    }

    public Result<bool> GetEnhancedSyncActif()
    {
        var l = lire();
        return l.EnhancedSyncActif is { } on
            ? Result<bool>.Ok(on)
            : Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "Enhanced Sync non lu par ADLX.");
    }

    public Result<bool> GetChillActif()
    {
        var l = lire();
        return l.ChillActif is { } on
            ? Result<bool>.Ok(on)
            : Result<bool>.Fail(ErrorCodes.TweakSdkUnavailable, l.Raison ?? "Radeon Chill non lu par ADLX.");
    }
}
