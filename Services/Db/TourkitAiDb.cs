// Services/Db/TourkitAiDb.cs
using Microsoft.Data.SqlClient;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Db;

/// <summary>
/// Factory + initializer cho SQL Server `PushDb` (dùng chung instance với TourKit/PushNotification).
/// Đọc ConnectionStrings:PushDb từ appsettings — value có thể là "ENC:" mã hoá Crypton, tự decrypt.
/// InitAsync() chạy CREATE TABLE IF NOT EXISTS — idempotent, an toàn chạy mỗi lần khởi động.
///
/// Pattern Dapper (không phải EF Core) — gọn, không cần migrations binary, schema dưới dạng SQL thuần.
/// </summary>
public class TourkitAiDb
{
    private readonly string _connStr;
    private readonly ILogger<TourkitAiDb> _log;

    public TourkitAiDb(IConfiguration cfg, ILogger<TourkitAiDb> log)
    {
        _log = log;
        var conn = cfg.GetConnectionString("PushDb")
            ?? throw new InvalidOperationException("Thiếu ConnectionStrings:PushDb trong appsettings");
        if (conn.StartsWith("ENC:"))
            conn = Crypton.Decrypt(conn.Substring(4));
        _connStr = conn;
    }

    /// Mở connection mới. Caller phải dispose (using).
    public SqlConnection Open()
    {
        var c = new SqlConnection(_connStr);
        c.Open();
        return c;
    }

    /// Async open.
    public async Task<SqlConnection> OpenAsync(CancellationToken ct = default)
    {
        var c = new SqlConnection(_connStr);
        await c.OpenAsync(ct);
        return c;
    }

    /// Tạo schema 3 bảng nếu chưa có. Chạy 1 lần lúc startup.
    public async Task InitAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = await OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = SchemaSql;
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory đã có/đã tạo)");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "TourkitAiDb InitAsync thất bại — DB chưa sẵn sàng, các repo sẽ fallback file cũ");
        }
    }

    // ─── Schema (SQL Server 2016+ — dùng IF NOT EXISTS / OBJECT_ID idempotent) ──
    private const string SchemaSql = @"
IF OBJECT_ID('dbo.Reviews', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Reviews (
        CustomerId     NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        [Rank]         NVARCHAR(2)     NULL,
        AlertLevel     NVARCHAR(32)    NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        TokensIn       INT             NULL,
        TokensOut      INT             NULL,
        GeneratedAt    BIGINT          NOT NULL,
        FeedbackJson   NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_Reviews PRIMARY KEY CLUSTERED (TenantId, CustomerId)
    );
    CREATE INDEX IX_Reviews_TenantId_Rank   ON dbo.Reviews(TenantId, [Rank]);
    CREATE INDEX IX_Reviews_GeneratedAt     ON dbo.Reviews(GeneratedAt DESC);
END;

IF OBJECT_ID('dbo.DealScores', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.DealScores (
        DealId         NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        WinRate        INT             NULL,
        [Level]        NVARCHAR(32)    NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        TokensIn       INT             NULL,
        TokensOut      INT             NULL,
        GeneratedAt    BIGINT          NOT NULL,
        CONSTRAINT PK_DealScores PRIMARY KEY CLUSTERED (TenantId, DealId)
    );
    CREATE INDEX IX_DealScores_TenantId_Level ON dbo.DealScores(TenantId, [Level]);
END;

IF OBJECT_ID('dbo.AiHistory', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.AiHistory (
        Id             BIGINT          IDENTITY(1,1) NOT NULL,
        Feature        NVARCHAR(32)    NOT NULL,
        EntityId       NVARCHAR(64)    NOT NULL,
        TenantId       NVARCHAR(128)   NOT NULL,
        Fingerprint    NVARCHAR(64)    NOT NULL,
        DataJson       NVARCHAR(MAX)   NOT NULL,
        AiProvider     NVARCHAR(64)    NULL,
        AiModel        NVARCHAR(128)   NULL,
        GeneratedAt    BIGINT          NOT NULL,
        CONSTRAINT PK_AiHistory PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_AiHistory_FeatureEntity ON dbo.AiHistory(Feature, EntityId, GeneratedAt DESC);
END;
";
}
