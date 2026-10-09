using DrDxt.Core;

namespace DrDxt.Detection.Gpu;

public interface IAdlxProvider
{
    /// <summary>SAM/ReBAR AMD via ADLX. Absence ADLX / pas de GPU AMD → Unknown (jamais throw).</summary>
    TriState GetSmartAccessMemoryState();
}
