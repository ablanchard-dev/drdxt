using DrDxt.Core;
using DrDxt.Detection.Gpu.Adlx;

namespace DrDxt.Detection.Gpu;

/// <summary>SAM (AMD Smart Access Memory, le ReBAR AMD) lu par ADLX : IADLXGPUTuningServices1.GetSmartAccessMemory
/// puis IsSupported / IsEnabled, sur la carte dédiée. DLL absente, pas de carte AMD, SAM non pris en charge ou entrée
/// en erreur : Unknown, jamais Off inventé. Ne lève jamais. (A1, 06/10/2026 ; avant : Unknown en dur.)</summary>
public sealed class AdlxProvider(Func<AdlxReglagesLus>? lire = null) : IAdlxProvider
{
    private readonly Func<AdlxReglagesLus> _lire = lire ?? AdlxReglages.Lire;

    public TriState GetSmartAccessMemoryState()
    {
        try
        {
            return _lire().Sam switch { true => TriState.On, false => TriState.Off, null => TriState.Unknown };
        }
        catch (Exception) { return TriState.Unknown; }
    }
}
