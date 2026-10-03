using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TourkitAiProxy.Domain.AiKeys;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Infrastructure.AiKeys;
using TourkitAiProxy.Infrastructure.Db;
using TourkitAiProxy.Services;
using TourkitAiProxy.Services.AiKeys;
using TourkitAiProxy.Services.Providers;
using Xunit;

namespace TourkitAiProxy.Tests.AiKeys;

/// <summary>
/// Lớp bọc lùi key, kiểm bằng HÀNH VI: DI thật, AiModelRegistry thật, bộ đệm thật, chỉ nhà cung cấp
/// là giả. Đây là ca "key bị lỗi" chủ dự án yêu cầu (03/10/2026) — ca duy nhất không thử được trên
/// máy thật nếu không thu hồi key của họ.
///
/// <para>Điều phải chứng minh: key khách hỏng thì lệnh VẪN chạy (bằng key hệ thống), lượt ĐƯỢC trừ, có
/// lời báo, và cờ lỗi được ghi. Còn lệnh chạy bằng key khách thì KHÔNG trừ lượt.</para>
/// </summary>
public class ByoAwareProviderTests
{
    private const string Tenant = "staging.tourkit.vn";
    private const string TenantKey = "xai-cua-khach";
    private const string SystemKey = "sk-he-thong";

    /// Nhà cung cấp giả: ghi lại từng lệnh nhận được và cờ miễn lượt lúc nhận.
    private sealed class FakeProvider : IAiProvider
    {
        private readonly AiCallContext _ctx;
        public FakeProvider(string id, AiCallContext ctx) { Id = id; _ctx = ctx; }
        public string Id { get; }
        public string Label => Id;
        public IReadOnlyList<ProviderModel> Models => new[] { new ProviderModel("m-" + Id, "M", true) };
        public Func<CompleteRequest, CompleteResult>? Behaviour { get; set; }
        public List<(string? Key, bool FreeOfQuota)> Calls { get; } = new();

        public Task<CompleteResult> CompleteAsync(CompleteRequest req, CancellationToken ct)
        {
            Calls.Add((req.ApiKey, _ctx.Resolve().FreeOfQuota));
            return Task.FromResult(Behaviour?.Invoke(req) ?? Ok(Id));
        }

        public async Task<CompleteResult> StreamAsync(CompleteRequest req, Func<string, Task> onDelta, CancellationToken ct)
        {
            var r = await CompleteAsync(req, ct);
            await onDelta(r.Text);
            return r;
        }
    }

    private static CompleteResult Ok(string from) => new("tra loi tu " + from, "m", 1, 1, 1, "stop");

    private sealed record Rig(ServiceProvider Sp, FakeProvider Grok, FakeProvider Sys, TenantAiKeyStore Store,
        AiCallContext Ctx, AiModelRegistry Models, ProviderRegistry Providers);

    private static Rig Build(bool featureOn = true, bool seedKey = true)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:PushDb"] = "khong-phai-chuoi-ket-noi",   // ghi CSDL (RecordFail) sẽ hỏng và bị nuốt
            ["Features:ByoAiKey"] = featureOn ? "true" : "false",
            ["Models:Primary:Provider"] = "sys",
            ["Models:Primary:Model"] = "m-sys",
            ["Models:Primary:ApiKey"] = SystemKey,
        }).Build();

        var s = new ServiceCollection();
        s.AddLogging();
        s.AddSingleton<IConfiguration>(cfg);
        s.AddSingleton<TourkitAiDb>();
        s.AddSingleton<TenantAiKeyRepository>();
        s.AddSingleton<TenantAiKeyStore>();
        // Resolve() trả bản Push trước, không chạm HTTP hay phiên — nên không cần TkSessionStore thật.
        s.AddSingleton(new AiCallContext(new HttpContextAccessor(), null!));
        s.AddSingleton(sp => new FakeProvider("grok", sp.GetRequiredService<AiCallContext>()));
        s.AddSingleton(sp => new FakeProvider("sys", sp.GetRequiredService<AiCallContext>()));
        // Đăng ký Y HỆT mã sản phẩm: IAiProvider luôn là bản đã bọc.
        s.AddSingleton<IAiProvider>(sp => new ByoAwareProvider(sp.GetServices<FakeProvider>().First(p => p.Id == "grok"), sp));
        s.AddSingleton<IAiProvider>(sp => new ByoAwareProvider(sp.GetServices<FakeProvider>().First(p => p.Id == "sys"), sp));
        s.AddSingleton<ProviderRegistry>();
        s.AddSingleton<AiModelRegistry>();

        var sp = s.BuildServiceProvider();
        var store = sp.GetRequiredService<TenantAiKeyStore>();
        if (seedKey)
        {
            var key = new TenantAiKey(Tenant, "grok", null, "ENC", TenantAiKey.MaskOf(TenantKey), true,
                DateTime.UtcNow, "admin", DateTime.UtcNow);
            store.SeedForTests(new TenantAiKeyRepository.Loaded(key, TenantKey));
        }
        var fakes = sp.GetServices<FakeProvider>().ToList();
        return new(sp, fakes.First(p => p.Id == "grok"), fakes.First(p => p.Id == "sys"), store,
            sp.GetRequiredService<AiCallContext>(), sp.GetRequiredService<AiModelRegistry>(),
            sp.GetRequiredService<ProviderRegistry>());
    }

    /// Gọi AI đúng như mã sản phẩm: Push ngữ cảnh → Resolve → chọn provider theo kết quả → gọi.
    private static async Task<CompleteResult> Call(Rig r)
    {
        using var _ = r.Ctx.Push(AiFeatures.ChatInbox, Tenant);
        var resolved = r.Models.Resolve(AiFeature.ChatInbox);
        var provider = r.Providers.Resolve(resolved.Provider);
        return await provider.CompleteAsync(new CompleteRequest(
            Prompt: "hi", Provider: provider.Id, Model: resolved.Model, MaxTokens: 10,
            Temperature: 0, System: null, ApiKey: resolved.ApiKey), CancellationToken.None);
    }

    // ── Có key ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task With_tenant_key_runs_on_tenant_provider_and_is_FREE_of_quota()
    {
        var r = Build();
        var kq = await Call(r);

        Assert.Equal("tra loi tu grok", kq.Text);
        Assert.Single(r.Grok.Calls);
        Assert.Equal(TenantKey, r.Grok.Calls[0].Key);
        Assert.True(r.Grok.Calls[0].FreeOfQuota);    // khách tự trả nhà cung cấp → không trừ lượt
        Assert.Empty(r.Sys.Calls);
        Assert.Null(kq.Warning);
    }

    // ── Key bị lỗi ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(401, "invalid_api_key")]
    [InlineData(402, "")]
    [InlineData(429, "insufficient_quota")]
    [InlineData(400, "Incorrect API key provided. You can obtain an API key from https://console.x.ai.")]
    public async Task Broken_tenant_key_falls_back_to_system_key_and_IS_charged(int status, string body)
    {
        var r = Build();
        r.Grok.Behaviour = _ => throw new UpstreamException(status, "loi", body);

        var kq = await Call(r);

        // Lệnh VẪN chạy — khách không bị đứt dịch vụ…
        Assert.Equal("tra loi tu sys", kq.Text);
        // …bằng key HỆ THỐNG…
        Assert.Single(r.Sys.Calls);
        Assert.Equal(SystemKey, r.Sys.Calls[0].Key);
        // …và lượt BỊ TRỪ (nền tảng không trả hộ).
        Assert.False(r.Sys.Calls[0].FreeOfQuota);
        // Có nói ra, và cờ lỗi được ghi để trang cấu hình hiện cảnh báo.
        Assert.NotNull(kq.Warning);
        Assert.Contains("key hệ thống", kq.Warning);
        Assert.True(r.Store.IsFailing(Tenant));
    }

    [Theory]
    [InlineData(429, "rate_limit_exceeded")]   // gọi quá dày — vài giây sau là được
    [InlineData(503, "overloaded")]            // nhà cung cấp lỗi — không liên quan key
    public async Task Transient_errors_do_NOT_fall_back(int status, string body)
    {
        // Lùi ở đây là đốt lượt của khách cho một lỗi tự hết.
        var r = Build();
        r.Grok.Behaviour = _ => throw new UpstreamException(status, "loi", body);

        await Assert.ThrowsAsync<UpstreamException>(() => Call(r));
        Assert.Empty(r.Sys.Calls);
        Assert.False(r.Store.IsFailing(Tenant));
    }

    [Fact]
    public async Task Tenant_key_working_again_clears_the_failing_flag()
    {
        var r = Build();
        r.Grok.Behaviour = _ => throw new UpstreamException(402, "het tien", "");
        await Call(r);
        Assert.True(r.Store.IsFailing(Tenant));

        r.Grok.Behaviour = null;     // khách đã nạp tiền
        await Call(r);
        Assert.False(r.Store.IsFailing(Tenant));
    }

    // ── Chưa có key / cờ tắt: không đổi gì ────────────────────────────────────

    [Fact]
    public async Task Without_tenant_key_runs_on_system_and_IS_charged()
    {
        var r = Build(seedKey: false);
        var kq = await Call(r);

        Assert.Equal("tra loi tu sys", kq.Text);
        Assert.False(r.Sys.Calls.Single().FreeOfQuota);
        Assert.Empty(r.Grok.Calls);
    }

    [Fact]
    public async Task Feature_off_ignores_a_configured_key_completely()
    {
        var r = Build(featureOn: false);
        var kq = await Call(r);

        Assert.Equal("tra loi tu sys", kq.Text);
        Assert.False(r.Sys.Calls.Single().FreeOfQuota);
        Assert.Empty(r.Grok.Calls);
    }

    [Fact]
    public async Task A_call_carrying_another_key_is_NOT_made_free()
    {
        // Lệnh tự mang key khác (vd key hệ thống) trong lúc công ty có BYO: không phải lệnh của khách,
        // không được miễn lượt — đây là cái lỗ "nền tảng trả hộ" tìm ra 03/10/2026.
        var r = Build();
        using var _ = r.Ctx.Push(AiFeatures.ChatInbox, Tenant);
        await r.Providers.Resolve("grok").CompleteAsync(new CompleteRequest(
            Prompt: "hi", Provider: "grok", Model: "m", MaxTokens: 10, Temperature: 0, System: null,
            ApiKey: SystemKey), CancellationToken.None);

        Assert.False(r.Grok.Calls.Single().FreeOfQuota);
    }
}
