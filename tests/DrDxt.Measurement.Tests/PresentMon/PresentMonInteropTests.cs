using System.Runtime.InteropServices;
using DrDxt.Measurement.PresentMon;
using FluentAssertions;
using Xunit;

namespace DrDxt.Measurement.Tests.PresentMon;

public class PresentMonInteropTests
{
    [Fact]
    public void QueryElement_struct_has_C_ABI_layout_of_32_bytes()
        => Marshal.SizeOf<PM_QUERY_ELEMENT>().Should().Be(32); // 4+4+4+4+8+8

    [Fact]
    public void Status_success_is_zero()
        => ((int)PM_STATUS.PM_STATUS_SUCCESS).Should().Be(0);

    [Fact]
    public void Stat_none_is_zero()
        => ((int)PM_STAT.PM_STAT_NONE).Should().Be(0);

    [Fact]
    public void Frametime_and_latency_metrics_exist()
    {
        _ = PM_METRIC.PM_METRIC_BETWEEN_PRESENTS;
        _ = PM_METRIC.PM_METRIC_RENDER_PRESENT_LATENCY; // compile = présents
    }

    // Numeric values must match third_party/presentmon/PresentMonAPI.h ordering,
    // otherwise the wrong metric is queried and the blob is parsed for the wrong column.
    [Fact]
    public void Between_presents_metric_has_header_value_78()
        => ((int)PM_METRIC.PM_METRIC_BETWEEN_PRESENTS).Should().Be(78);

    [Fact]
    public void Render_present_latency_metric_has_header_value_82()
        => ((int)PM_METRIC.PM_METRIC_RENDER_PRESENT_LATENCY).Should().Be(82);

    [Fact]
    public void Invalid_pid_status_has_header_value_6()
        => ((int)PM_STATUS.PM_STATUS_INVALID_PID).Should().Be(6);
}
