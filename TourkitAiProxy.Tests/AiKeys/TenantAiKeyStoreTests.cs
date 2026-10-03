using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TourkitAiProxy.Infrastructure.AiKeys;
using TourkitAiProxy.Infrastructure.Db;
using TourkitAiProxy.Services.AiKeys;
using Xunit;

namespace TourkitAiProxy.Tests.AiKeys;

/// <summary>
/// Canh lời hứa với chủ dự án (03/10/2026): <b>key AI riêng không được ảnh hưởng hệ thống đang chạy.</b>
///
/// <para>Cách chứng minh "không chạm CSDL" bằng HÀNH VI chứ không bằng đọc mã nguồn: dựng kho với
/// một chuỗi kết nối HỎNG. Hễ có ai chạm CSDL là repository ghi đúng một dòng cảnh báo — nên đếm
/// cảnh báo là biết có chạm hay không. (Ghi nhớ dự án: canh nguồn không thấy được hành vi khung.)</para>
/// </summary>
public class TenantAiKeyStoreTests
{
    /// Bắt mọi dòng log mức Warning trở lên của repository.
    private sealed class WarnCounter : ILogger<TenantAiKeyRepository>
    {
        public int Warnings;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? ex,
            Func<TState, Exception?, string> fmt)
        {
            if (level >= LogLevel.Warning) Interlocked.Increment(ref Warnings);
        }
    }

    private static (TenantAiKeyStore Store, WarnCounter Log) Build(bool featureOn)
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            // Cố ý KHÔNG phải chuỗi kết nối hợp lệ: chạm CSDL là hỏng ngay, không phải chờ hết giờ.
            ["ConnectionStrings:PushDb"] = "day-khong-phai-chuoi-ket-noi",
            ["Features:ByoAiKey"] = featureOn ? "true" : "false",
        }).Build();

        var log = new WarnCounter();
        var db = new TourkitAiDb(cfg, NullLogger<TourkitAiDb>.Instance);
        var repo = new TenantAiKeyRepository(db, log);
        return (new TenantAiKeyStore(repo, cfg, NullLogger<TenantAiKeyStore>.Instance), log);
    }

    [Fact]
    public async Task Feature_off_never_touches_the_database()
    {
        var (store, log) = Build(featureOn: false);

        await store.RefreshAsync();

        // Cờ tắt mà vẫn chạm CSDL thì đây đã có một dòng cảnh báo "nạp cấu hình key hỏng".
        Assert.Equal(0, log.Warnings);
    }

    [Fact]
    public void Feature_off_always_picks_system_key()
    {
        var (store, _) = Build(featureOn: false);
        Assert.False(store.Decide("staging.tourkit.vn").UseTenantKey);
    }

    [Fact]
    public async Task Database_failure_never_reaches_the_caller()
    {
        // Cờ bật + CSDL hỏng: lần nạp phải nuốt lỗi (có ghi log), và lệnh AI vẫn chạy bằng key hệ
        // thống. Ném ra ở đây là làm hỏng lệnh AI của khách — đúng thứ không được phép.
        var (store, log) = Build(featureOn: true);

        var ex = await Record.ExceptionAsync(() => store.RefreshAsync());

        Assert.Null(ex);
        Assert.Equal(1, log.Warnings);   // có chạm, có báo — chứng minh test đọc đúng tín hiệu
        Assert.False(store.Decide("staging.tourkit.vn").UseTenantKey);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_tenant_picks_system_key_even_when_feature_on(string? tenant)
    {
        var (store, _) = Build(featureOn: true);
        Assert.False(store.Decide(tenant).UseTenantKey);
    }

    [Fact]
    public void ReportOk_on_a_healthy_tenant_writes_nothing()
    {
        // Lượt thành công bình thường chỉ được tốn một lần tra từ điển. Ghi CSDL ở đây là thêm một
        // lần chạm CSDL vào MỌI lệnh AI.
        var (store, log) = Build(featureOn: true);

        store.ReportOk("staging.tourkit.vn");
        Thread.Sleep(300);   // nếu có ghi thì nó chạy nền — chờ cho nó kịp hỏng và ghi log

        Assert.Equal(0, log.Warnings);
    }
}
