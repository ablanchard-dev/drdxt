namespace DrDxt.Core;

/// <summary>
/// Turns the two fields NVIDIA fills on every DRS read — settingLocation and isCurrentPredefined —
/// into an answer to "who put this value here?".
///
/// The product was discarding both and keeping only the DWORD. On a machine carrying 10218
/// driver-shipped application profiles, that makes every value look equally like a user choice,
/// and makes the question "which settings did I actually change?" unanswerable.
///
/// Semantics from NVIDIA's Driver Settings Programming Guide (PG-12072-001 v01): p.2 separates
/// "predefined settings" (shipped with the driver) from "user settings" (edited after install);
/// p.9-10 state that settings not from the current profile are inherited from the base or global
/// profiles and must be skipped when asking what a profile owns.
///
/// Lives in Core since 10/09/2026: the elevated helper needs the same reading to prove that a
/// revert returned a game's profile to its original provenance, and it references only Core.
/// Two copies of this mapping would be two definitions of "restored".
///
/// Pure and total: no I/O. An unrecognised location degrades to <see cref="DrsValueOrigin.Unknown"/>.
/// </summary>
public static class DrsSettingOrigin
{
    // NVDRS_SETTING_LOCATION (PG-12072-001).
    private const uint CurrentProfile = 0, GlobalProfile = 1, BaseProfile = 2, DefaultProfile = 3;

    /// <param name="settingLocation">NVDRS_SETTING.settingLocation as returned by the driver.</param>
    /// <param name="isCurrentPredefined">NVDRS_SETTING.isCurrentPredefined: non-zero = the current
    /// value is the one NVIDIA shipped.</param>
    /// <param name="isPredefinedValid">NVDRS_SETTING.isPredefinedValid: whether predefinedValue is
    /// usable. Reported, never used to override the two fields above.</param>
    public static DrsOriginState Describe(uint settingLocation, uint isCurrentPredefined, uint isPredefinedValid)
    {
        // Inheritance is decided first. isCurrentPredefined == 0 on an inherited value would
        // otherwise read as "the user set this here" — the user set it somewhere else, and
        // sending someone to edit a profile that owns nothing is worse than saying nothing.
        switch (settingLocation)
        {
            case GlobalProfile:
                return new(DrsValueOrigin.InheritedFromGlobalProfile, false,
                    "This value is inherited from the global profile: this profile does not own it, " +
                    "so changing it here would not be changing what the driver applies.");

            case BaseProfile:
                return new(DrsValueOrigin.InheritedFromBaseProfile, false,
                    "This value is inherited from the base profile: this profile does not own it.");

            case DefaultProfile:
                return new(DrsValueOrigin.DriverDefault, false,
                    "This is the driver default: nobody set it anywhere.");

            case CurrentProfile:
                return isCurrentPredefined != 0
                    ? new(DrsValueOrigin.ShippedWithTheDriver, false,
                        "This value ships with the NVIDIA driver for this application: it is a " +
                        "predefined setting, not a choice anyone made on this machine." +
                        (isPredefinedValid != 0 ? "" : " (the shipped value itself was not readable)"))
                    : new(DrsValueOrigin.SetByUser, true,
                        "This value was set on this profile and is not one the driver ships: " +
                        "someone changed it on this machine.");

            default:
                return new(DrsValueOrigin.Unknown, false,
                    $"The driver reported an unrecognised setting location ({settingLocation}), " +
                    "so where this value comes from is not established.");
        }
    }
}
