// TourkitAiProxy.Tests/Chat/UnresolvedQuestionsLogTests.cs
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Chat;

namespace TourkitAiProxy.Tests.Chat;

public class UnresolvedQuestionsLogTests : IDisposable
{
    private readonly FakeWebHostEnvironment _env;
    private readonly UnresolvedQuestionsLog _log;

    public UnresolvedQuestionsLogTests()
    {
        _env = new FakeWebHostEnvironment();
        _log = new UnresolvedQuestionsLog(_env, NullLogger<UnresolvedQuestionsLog>.Instance);
    }

    public void Dispose() => _env.Dispose();

    // ── Append + Read co ban ──────────────────────────────────────────────────────

    [Fact]
    public void Append_creates_file_and_Read_returns_entry()
    {
        _log.Append(
            tag:            "both_planner_and_heuristic_fail",
            sessionId:      "sess-001",
            tenantId:       "tenant-xmedia",
            question:       "doanh thu thang nay la bao nhieu?",
            history:        null,
            plannerRaw:     null,
            toolChosen:     null,
            aiReplyPreview: null,
            provider:       "opencode-go",
            model:          "deepseek-v4-flash",
            iterations:     1,
            latencyMs:      350,
            tokensIn:       120,
            tokensOut:      45);

        var entries = _log.Read(days: 7);

        Assert.Single(entries);
        var e = entries[0];
        Assert.Equal("both_planner_and_heuristic_fail", e.GetProperty("tag").GetString());
        Assert.Equal("sess-001",       e.GetProperty("sessionId").GetString());
        Assert.Equal("tenant-xmedia", e.GetProperty("tenantId").GetString());
        Assert.Equal(350L, e.GetProperty("latencyMs").GetInt64());
    }

    [Fact]
    public void Append_multiple_entries_Read_returns_newest_first()
    {
        _log.Append("tool_returned_empty",  "s1", "t1", "cau hoi 1", null, null, "financial_summary", null, "opencode-go", null, 1, 100, 10, 5);
        _log.Append("input_truncated",      "s2", "t1", "cau hoi 2", null, null, null, null, "opencode-go", null, 1, 200, 20, 10);
        _log.Append("iteration_limit_reached", "s3", "t1", "cau hoi 3", null, null, null, null, "anthropic", null, 3, 3000, 500, 200);

        var all = _log.Read(days: 7);
        // Newest (cuoi file) tra truoc
        Assert.Equal(3, all.Count);
        Assert.Equal("iteration_limit_reached", all[0].GetProperty("tag").GetString());
        Assert.Equal("tool_returned_empty",      all[2].GetProperty("tag").GetString());
    }

    [Fact]
    public void Read_filter_by_tag_returns_only_matching()
    {
        _log.Append("tool_returned_empty", "s1", "t1", "q1", null, null, null, null, null, null, 1, 100, 10, 5);
        _log.Append("input_truncated",     "s2", "t1", "q2", null, null, null, null, null, null, 1, 200, 20, 8);
        _log.Append("tool_returned_empty", "s3", "t1", "q3", null, null, null, null, null, null, 1, 300, 30, 12);

        var filtered = _log.Read(days: 7, tag: "tool_returned_empty");
        Assert.Equal(2, filtered.Count);
        Assert.All(filtered, e => Assert.Equal("tool_returned_empty", e.GetProperty("tag").GetString()));
    }

    // ── Truncation / truncation cua question + plannerRaw ────────────────────────

    [Fact]
    public void Append_truncates_long_question_at_500()
    {
        var longQ = new string('a', 600);
        _log.Append("input_truncated", "s", "t", longQ, null, null, null, null, null, null, 1, 0, 0, 0);

        var entry = _log.Read(days: 1).Single();
        var q = entry.GetProperty("question").GetString()!;
        Assert.True(q.Length <= 502); // 500 + "…" (1 ky tu Unicode)
        Assert.EndsWith("…", q);
    }

    [Fact]
    public void Append_truncates_plannerRaw_at_400()
    {
        var longRaw = new string('x', 500);
        _log.Append("both_planner_and_heuristic_fail", "s", "t", "q", null, longRaw, null, null, null, null, 1, 0, 0, 0);

        var entry = _log.Read(days: 1).Single();
        var raw = entry.GetProperty("plannerRaw").GetString()!;
        Assert.True(raw.Length <= 402);
        Assert.EndsWith("…", raw);
    }

    // ── History 3 luot gan nhat ───────────────────────────────────────────────────

    [Fact]
    public void Append_keeps_only_last_3_history_turns()
    {
        var history = new List<ChatTurn>
        {
            new("user",      "turn 1"),
            new("assistant", "reply 1"),
            new("user",      "turn 2"),
            new("assistant", "reply 2"),
            new("user",      "turn 3 - current"),
        };

        _log.Append("planner_none_but_data_intent", "s", "t", "q", history, null, null, null, null, null, 1, 0, 0, 0);

        var entry = _log.Read(days: 1).Single();
        var hist = entry.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(3, hist.Count);
        // TakeLast(3) tu 5 turn: [user"turn 2", assistant"reply 2", user"turn 3 - current"]
        Assert.Equal("user",      hist[0].GetProperty("role").GetString());
        Assert.Equal("assistant", hist[1].GetProperty("role").GetString());
        Assert.Equal("user",      hist[2].GetProperty("role").GetString());
        Assert.Equal("turn 3 - current", hist[2].GetProperty("content").GetString());
    }

    // ── Read khi file chua ton tai ───────────────────────────────────────────────

    [Fact]
    public void Read_returns_empty_when_file_not_yet_created()
    {
        // Log moi tao, chua Append gi ca -- file chua ton tai
        var result = _log.Read(days: 7);
        Assert.Empty(result);
    }

    // ── maxEntries limit ─────────────────────────────────────────────────────────

    [Fact]
    public void Read_respects_maxEntries_cap()
    {
        for (int i = 0; i < 10; i++)
            _log.Append("tool_returned_empty", $"s{i}", "t", $"q{i}", null, null, null, null, null, null, 1, 0, 0, 0);

        var limited = _log.Read(days: 30, maxEntries: 3);
        Assert.Equal(3, limited.Count);
    }

    // ── Ghi dong thoi (thread safety co ban) ────────────────────────────────────

    [Fact]
    public async Task Append_is_thread_safe_writes_all_entries()
    {
        const int threads = 20;
        var tasks = Enumerable.Range(0, threads)
            .Select(i => Task.Run(() =>
                _log.Append("upstream_persistent_error", $"s{i}", "t", $"q{i}", null, null, null, null, null, null, 1, 0, 0, 0)))
            .ToArray();
        await Task.WhenAll(tasks);

        var all = _log.Read(days: 1, maxEntries: 50);
        Assert.Equal(threads, all.Count);
    }
}
