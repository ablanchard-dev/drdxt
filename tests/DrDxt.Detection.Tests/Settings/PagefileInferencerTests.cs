using DrDxt.Core;
using DrDxt.Detection.Settings;
using FluentAssertions;
using Xunit;

namespace DrDxt.Detection.Tests.Settings;

// NS-4 (RAM reliability). A DISABLED pagefile is a real reliability hazard (commit-limit
// OOM → freeze/crash — Microsoft, « Introduction to the page file »). We infer it from the raw REG_MULTI_SZ
// HKLM\...\Memory Management\PagingFiles: an empty value = pagefile OFF. A pagefile that IS
// present (system-managed "C:\pagefile.sys 0 0" OR a custom size) is a valid, possibly
// deliberate choice → On, never second-guessed. Unreadable/null → Unknown (never a lie).
public class PagefileInferencerTests
{
    [Fact]
    public void Empty_list_means_pagefile_disabled_off()
    {
        PagefileInferencer.Infer(Array.Empty<string>()).Should().Be(TriState.Off);
    }

    [Fact]
    public void Only_blank_entries_means_disabled_off()
    {
        PagefileInferencer.Infer(new[] { "", "   " }).Should().Be(TriState.Off);
    }

    [Fact]
    public void System_managed_entry_is_on()
    {
        PagefileInferencer.Infer(new[] { @"C:\pagefile.sys 0 0" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Custom_fixed_size_is_on_not_second_guessed()
    {
        // A present custom pagefile is a valid/deliberate choice — we only flag DISABLED.
        PagefileInferencer.Infer(new[] { @"C:\pagefile.sys 4096 8192" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Automatically_managed_wildcard_form_is_on_not_a_false_disabled()
    {
        // The DEFAULT "automatically manage" state stores an entry without sizes (often the
        // wildcard "?:\pagefile.sys"). It is a present, healthy page file → On, never a false
        // "disabled" on a fine machine (audit C2: never invent a problem). Verified encoding:
        // auto = entry-without-sizes, disabled = empty value.
        PagefileInferencer.Infer(new[] { @"?:\pagefile.sys" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Any_real_entry_among_blanks_is_on()
    {
        PagefileInferencer.Infer(new[] { "", @"D:\pagefile.sys 0 0" }).Should().Be(TriState.On);
    }

    [Fact]
    public void Null_is_unknown_not_a_lie()
    {
        PagefileInferencer.Infer(null).Should().Be(TriState.Unknown);
    }
}
