using DrDxt.Core;

namespace DrDxt.Detection.Settings;

/// <summary>
/// NS-4 (RAM reliability; Microsoft, https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file). Classifies the Windows page file from the raw
/// REG_MULTI_SZ HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PagingFiles:
///   • empty / all-blank  → Off   (page file DISABLED — a real reliability hazard: at the
///                                  commit limit Windows hits "out of memory" → freeze/crash),
///   • any real entry      → On    (a page file IS configured — system-managed "C:\pagefile.sys
///                                  0 0" OR a custom size; a present page file is a valid,
///                                  possibly deliberate choice we never second-guess),
///   • null (unreadable)   → Unknown (never claim Off when we cannot read — §16 honesty).
/// This is DETECTION only: it feeds an honest reliability finding, not a "+FPS" claim, and never
/// writes (restoring "system-managed" is a WMI operation, out of the current typed-op model).
/// Verified PagingFiles encoding (so "empty = disabled" is not a false positive): a
/// system-managed / "automatically managed" page file stores an ENTRY without sizes (e.g.
/// "?:\pagefile.sys" or "C:\pagefile.sys 0 0") — non-empty; a custom one is "…sys min max";
/// only DISABLING the page file leaves the value EMPTY. So any non-blank entry ⇒ present (On).
/// Source: https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file
/// </summary>
public static class PagefileInferencer
{
    public static TriState Infer(IReadOnlyList<string>? pagingFiles)
    {
        if (pagingFiles is null) return TriState.Unknown;
        return pagingFiles.Any(entry => !string.IsNullOrWhiteSpace(entry))
            ? TriState.On
            : TriState.Off;
    }
}
