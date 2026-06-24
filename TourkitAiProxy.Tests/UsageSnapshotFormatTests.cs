using TourkitAiProxy.Services;
using Xunit;

namespace TourkitAiProxy.Tests;

/// Test pure-logic format Snapshot từ list rows → object endpoint trả ra.
/// Không đụng DB.
public class UsageSnapshotFormatTests
{
    [Fact]
    public void Format_EmptyList_ReturnsZeroes()
    {
        var rows = new List<UsageRepository.CounterRow>();
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(0L, (long)snap.calls);
        Assert.Equal(0L, (long)snap.inputTokens);
        Assert.Equal(0L, (long)snap.outputTokens);
        Assert.Equal(0L, (long)snap.avgLatencyMs);
    }

    [Fact]
    public void Format_SingleRow_AggregatesCorrectly()
    {
        var rows = new List<UsageRepository.CounterRow>
        {
            new("deepseek-v4-flash", Calls: 10, InTokens: 1000, OutTokens: 500, TotalLatencyMs: 5000)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(10L, (long)snap.calls);
        Assert.Equal(1000L, (long)snap.inputTokens);
        Assert.Equal(500L, (long)snap.outputTokens);
        Assert.Equal(500L, (long)snap.avgLatencyMs);    // 5000 / 10
    }

    [Fact]
    public void Format_MultipleModels_SumsTotalsAndKeepsByModel()
    {
        var rows = new List<UsageRepository.CounterRow>
        {
            new("model-a", Calls: 5, InTokens: 100, OutTokens: 50, TotalLatencyMs: 500),
            new("model-b", Calls: 3, InTokens: 60, OutTokens: 30, TotalLatencyMs: 600)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(8L, (long)snap.calls);
        Assert.Equal(160L, (long)snap.inputTokens);
        Assert.Equal(80L, (long)snap.outputTokens);
        Assert.Equal(137L, (long)snap.avgLatencyMs);    // 1100 / 8 = 137.5 → 137 (long truncates)
        var byModel = (Dictionary<string, long>)snap.byModel;
        Assert.Equal(5L, byModel["model-a"]);
        Assert.Equal(3L, byModel["model-b"]);
    }

    [Fact]
    public void Format_CostEstimate_UsesDeepseekPricing()
    {
        // Cost hardcode DeepSeek V4 Pro retail ($0.27/$1.10 per Mtok) — không chính xác model khác
        // nhưng đủ order-of-magnitude (giữ tương thích endpoint cũ).
        var rows = new List<UsageRepository.CounterRow>
        {
            new("any-model", Calls: 1, InTokens: 1_000_000, OutTokens: 1_000_000, TotalLatencyMs: 100)
        };
        dynamic snap = UsageTracker.FormatSnapshot(rows);
        Assert.Equal(1.37, (double)snap.estimatedCostUsd, precision: 2);   // 0.27 + 1.10
    }
}
