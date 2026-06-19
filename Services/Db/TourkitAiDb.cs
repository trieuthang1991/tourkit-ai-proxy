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
            _log.LogInformation("TourkitAiDb schema OK (Reviews/DealScores/AiHistory/MailAccounts/Mails/MailSyncState/VisaAssessments/TourQuotes đã có/đã tạo)");
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
        -- IsSync: cờ cho worker đồng bộ sang bảng chính. 0 = mới/đổi (cần sync); 1 = đã sync.
        -- Mọi INSERT/UPDATE từ Save() đều reset = 0 → worker pick lên lại nếu review thay đổi.
        IsSync         BIT             NOT NULL CONSTRAINT DF_Reviews_IsSync DEFAULT 0,
        CONSTRAINT PK_Reviews PRIMARY KEY CLUSTERED (TenantId, CustomerId)
    );
    CREATE INDEX IX_Reviews_TenantId_Rank   ON dbo.Reviews(TenantId, [Rank]);
    CREATE INDEX IX_Reviews_GeneratedAt     ON dbo.Reviews(GeneratedAt DESC);
END;

-- Idempotent ADD cột IsSync cho install cũ (Reviews đã có sẵn từ trước).
-- Default 0 → mọi row hiện tại nhận giá trị 0 → worker sẽ sync lần đầu toàn bộ history.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.Reviews') AND name = 'IsSync')
BEGIN
    ALTER TABLE dbo.Reviews ADD IsSync BIT NOT NULL CONSTRAINT DF_Reviews_IsSync DEFAULT 0;
END;

-- Filtered index cho worker query (chỉ rows chưa sync — index nhỏ, hot path).
-- Dùng sp_executesql để deferred compile: nếu trong cùng batch vừa ALTER TABLE ADD IsSync,
-- statement này được parse RUNTIME (sau ALTER) thay vì compile-time → tránh lỗi Invalid column name.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Reviews_Unsynced' AND object_id = OBJECT_ID('dbo.Reviews'))
BEGIN
    EXEC sp_executesql N'CREATE INDEX IX_Reviews_Unsynced ON dbo.Reviews(TenantId, GeneratedAt) WHERE IsSync = 0';
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
        -- IsSync: cùng pattern Reviews — worker đồng bộ điểm deal sang bảng chính. Reset về 0 mỗi lần re-score.
        IsSync         BIT             NOT NULL CONSTRAINT DF_DealScores_IsSync DEFAULT 0,
        CONSTRAINT PK_DealScores PRIMARY KEY CLUSTERED (TenantId, DealId)
    );
    CREATE INDEX IX_DealScores_TenantId_Level ON dbo.DealScores(TenantId, [Level]);
END;

-- Idempotent ADD cột IsSync cho DealScores install cũ.
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.DealScores') AND name = 'IsSync')
BEGIN
    ALTER TABLE dbo.DealScores ADD IsSync BIT NOT NULL CONSTRAINT DF_DealScores_IsSync DEFAULT 0;
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_DealScores_Unsynced' AND object_id = OBJECT_ID('dbo.DealScores'))
BEGIN
    EXEC sp_executesql N'CREATE INDEX IX_DealScores_Unsynced ON dbo.DealScores(TenantId, GeneratedAt) WHERE IsSync = 0';
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

IF OBJECT_ID('dbo.MailAccounts', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MailAccounts (
        TenantId        NVARCHAR(128)   NOT NULL,
        Username        NVARCHAR(128)   NOT NULL CONSTRAINT DF_MailAccounts_Username DEFAULT '',
        Address         NVARCHAR(256)   NOT NULL,
        AppPasswordEnc  NVARCHAR(512)   NOT NULL,
        Signature       NVARCHAR(MAX)   NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_MailAccounts PRIMARY KEY CLUSTERED (TenantId, Username)
    );
END;

-- Idempotent ADD cột Username cho install cũ (mô hình cũ: 1 mailbox / tenant share cho mọi NV).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.MailAccounts') AND name = 'Username')
BEGIN
    ALTER TABLE dbo.MailAccounts ADD Username NVARCHAR(128) NOT NULL CONSTRAINT DF_MailAccounts_Username DEFAULT '';
END;

-- Idempotent UPGRADE PK_MailAccounts từ (TenantId) → (TenantId, Username) cho install đã chạy bản cũ.
-- Detect: PK hiện tại chỉ có 1 cột → drop + recreate composite. Legacy row Username='' giữ nguyên
-- (chỉ 1 row/tenant, không vi phạm composite key); user mới add mailbox sẽ tạo row riêng theo Username.
IF EXISTS (
    SELECT 1
    FROM sys.key_constraints kc
    WHERE kc.parent_object_id = OBJECT_ID('dbo.MailAccounts')
      AND kc.[type] = 'PK'
      AND (SELECT COUNT(*) FROM sys.index_columns ic
           WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id) = 1
)
BEGIN
    DECLARE @pkName SYSNAME = (
        SELECT name FROM sys.key_constraints
        WHERE parent_object_id = OBJECT_ID('dbo.MailAccounts') AND [type] = 'PK'
    );
    EXEC('ALTER TABLE dbo.MailAccounts DROP CONSTRAINT ' + @pkName);
    ALTER TABLE dbo.MailAccounts
        ADD CONSTRAINT PK_MailAccounts PRIMARY KEY CLUSTERED (TenantId, Username);
END;

IF OBJECT_ID('dbo.Mails', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.Mails (
        TenantId        NVARCHAR(128)   NOT NULL,
        Id              NVARCHAR(256)   NOT NULL,
        FromName        NVARCHAR(256)   NULL,
        FromEmail       NVARCHAR(256)   NULL,
        Subject         NVARCHAR(1024)  NULL,
        Body            NVARCHAR(MAX)   NULL,
        BodyHtml        NVARCHAR(MAX)   NULL,
        ReceivedAt      DATETIME2       NOT NULL,
        IsRead          BIT             NOT NULL,
        Category        NVARCHAR(32)    NULL,
        Status          NVARCHAR(32)    NOT NULL,
        AiSummary       NVARCHAR(MAX)   NULL,
        DraftJson       NVARCHAR(MAX)   NULL,
        CONSTRAINT PK_Mails PRIMARY KEY CLUSTERED (TenantId, Id)
    );
    CREATE INDEX IX_Mails_Tenant_Received ON dbo.Mails(TenantId, ReceivedAt DESC);
END;

IF OBJECT_ID('dbo.MailSyncState', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.MailSyncState (
        TenantId        NVARCHAR(128)   NOT NULL,
        Address         NVARCHAR(256)   NOT NULL,
        UidValidity     BIGINT          NOT NULL,
        LastUid         BIGINT          NOT NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_MailSyncState PRIMARY KEY CLUSTERED (TenantId, Address)
    );
END;

-- Báo giá tour (Tour GIT/FIT) — user lưu nháp/sửa nhiều lần, cần persist không phải localStorage.
-- Schema giống Reviews/DealScores: per-tenant composite PK + IsSync cho worker đồng bộ.
IF OBJECT_ID('dbo.TourQuotes', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.TourQuotes (
        TenantId        NVARCHAR(128)   NOT NULL,
        Id              NVARCHAR(64)    NOT NULL,
        Title           NVARCHAR(512)   NULL,
        CustomerName    NVARCHAR(256)   NULL,
        CustomerPhone   NVARCHAR(64)    NULL,
        MarketName      NVARCHAR(128)   NULL,
        TourType        NVARCHAR(64)    NULL,
        StartDate       NVARCHAR(32)    NULL,        -- ISO date string (đỡ TZ headache)
        EndDate         NVARCHAR(32)    NULL,
        AdultCount      INT             NOT NULL,
        ChildCount      INT             NOT NULL,
        TotalNet        BIGINT          NOT NULL,     -- tổng giá vốn (sum services)
        TotalRevenue    BIGINT          NOT NULL,     -- tổng doanh thu (sum expenses sau VAT + child 75%)
        Profit          BIGINT          NOT NULL,
        MarginPercent   DECIMAL(6,2)    NULL,         -- % margin derived/override (Bug B B3-Hybrid)
        DataJson        NVARCHAR(MAX)   NOT NULL,     -- full form (expenses/services/note/warnings...) — raw passthrough
        CreatedBy       NVARCHAR(256)   NULL,         -- username từ session
        CreatedAt       DATETIME2       NOT NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        IsSync          BIT             NOT NULL CONSTRAINT DF_TourQuotes_IsSync DEFAULT 0,
        CONSTRAINT PK_TourQuotes PRIMARY KEY CLUSTERED (TenantId, Id)
    );
    CREATE INDEX IX_TourQuotes_Tenant_Created ON dbo.TourQuotes(TenantId, CreatedAt DESC);
END;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_TourQuotes_Unsynced' AND object_id = OBJECT_ID('dbo.TourQuotes'))
BEGIN
    EXEC sp_executesql N'CREATE INDEX IX_TourQuotes_Unsynced ON dbo.TourQuotes(TenantId, CreatedAt) WHERE IsSync = 0';
END;

IF OBJECT_ID('dbo.VisaAssessments', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.VisaAssessments (
        TenantId        NVARCHAR(128)   NOT NULL,
        Id              NVARCHAR(64)    NOT NULL,
        ApplicantName   NVARCHAR(256)   NULL,
        Country         NVARCHAR(64)    NULL,
        Status          NVARCHAR(32)    NOT NULL,
        ExtractionJson  NVARCHAR(MAX)   NOT NULL,
        ResultJson      NVARCHAR(MAX)   NULL,
        FileCount       INT             NOT NULL,
        FilesPurged     BIT             NOT NULL,
        CreatedAt       DATETIME2       NOT NULL,
        UpdatedAt       DATETIME2       NOT NULL,
        CONSTRAINT PK_VisaAssessments PRIMARY KEY CLUSTERED (TenantId, Id)
    );
    CREATE INDEX IX_VisaAssessments_Tenant_Created ON dbo.VisaAssessments(TenantId, CreatedAt DESC);
END;

-- Đơn nạp quota AI: user click chip → chọn gói → tạo order pending + VietQR.
-- Webhook IPN của Tingee về → match (TingeeRefId hoặc Memo=Id) → UPDATE atomic pending→paid + TopUp tenant.
-- TenantId không phải PK clustered (Id đủ unique global TKAI-{hash6}-{ts}-{rand4}) — webhook không có TenantId,
-- tra theo Id qua index. Nhưng giữ TenantId NOT NULL để ownership-check + report doanh thu.
IF OBJECT_ID('dbo.QuotaOrders', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.QuotaOrders (
        Id              NVARCHAR(64)    NOT NULL,
        TenantId        NVARCHAR(128)   NOT NULL,
        TierId          NVARCHAR(32)    NOT NULL,    -- starter | growth | enterprise
        AmountVnd       BIGINT          NOT NULL,
        QuotaUnits      INT             NOT NULL,
        Status          NVARCHAR(16)    NOT NULL,    -- pending | paid | expired | cancelled
        QrPayload       NVARCHAR(MAX)   NULL,        -- chuỗi EMV VietQR (NAPAS)
        BankBin         NVARCHAR(16)    NULL,
        AccountNumber   NVARCHAR(64)    NULL,
        AccountName     NVARCHAR(256)   NULL,
        Memo            NVARCHAR(128)   NOT NULL,    -- nội dung CK = Id (cho webhook match)
        ExpiresAt       DATETIME2       NOT NULL,
        CreatedAt       DATETIME2       NOT NULL,
        PaidAt          DATETIME2       NULL,
        TingeeRefId     NVARCHAR(128)   NULL,        -- ref từ webhook (audit)
        TingeeRaw       NVARCHAR(MAX)   NULL,        -- payload webhook raw (debug)
        CreatedBy       NVARCHAR(256)   NULL,
        CONSTRAINT PK_QuotaOrders PRIMARY KEY CLUSTERED (Id)
    );
    CREATE INDEX IX_QuotaOrders_Tenant_Created ON dbo.QuotaOrders(TenantId, CreatedAt DESC);
    CREATE INDEX IX_QuotaOrders_Status_Expires ON dbo.QuotaOrders(Status, ExpiresAt);
END;

-- Widget Chat tokens: tenant gen token paste vào <script data-token=""> ở site khách.
-- Token unique PK; mỗi tenant có thể tạo nhiều token (1 cho mỗi site/môi trường).
-- Greeting/SystemPrompt/BotName/Color: tham số config bot (custom per token).
-- AllowedOrigins: JSON array domain whitelist (null = cho phép mọi nơi). Daily/TotalMessages: counter.
IF OBJECT_ID('dbo.WidgetTokens', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.WidgetTokens (
        Token            NVARCHAR(64)    NOT NULL,
        TenantId         NVARCHAR(128)   NOT NULL,
        BotName          NVARCHAR(128)   NOT NULL,
        Greeting         NVARCHAR(1024)  NOT NULL,
        SystemPrompt     NVARCHAR(MAX)   NOT NULL,
        Color            NVARCHAR(16)    NOT NULL,        -- hex #RRGGBB
        Enabled          BIT             NOT NULL CONSTRAINT DF_WidgetTokens_Enabled DEFAULT 1,
        AllowedOrigins   NVARCHAR(MAX)   NULL,            -- JSON array (null = wildcard)
        TotalMessages    INT             NOT NULL CONSTRAINT DF_WidgetTokens_Total DEFAULT 0,
        CreatedAt        DATETIME2       NOT NULL,
        UpdatedAt        DATETIME2       NOT NULL,
        CONSTRAINT PK_WidgetTokens PRIMARY KEY CLUSTERED (Token)
    );
    CREATE INDEX IX_WidgetTokens_Tenant ON dbo.WidgetTokens(TenantId, CreatedAt DESC);
END;

-- Mở rộng widget cho phép cắm CRM TourKit (Phase 2):
--   TourKitSessionId  → ref TkSessionStore (lưu JWT + Crypton-encrypted password, tự re-login)
--   AllowedTools      → JSON array, vd ['tours','markets','booking_tickets']. null/empty = chỉ FAQ
--   CacheTtlSeconds   → cache CRM-data response (default 300 = 5 phút)
-- Idempotent ADD — install cũ nhận default an toàn (null + 300).
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WidgetTokens') AND name = 'TourKitSessionId')
BEGIN
    ALTER TABLE dbo.WidgetTokens ADD TourKitSessionId NVARCHAR(64) NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WidgetTokens') AND name = 'AllowedTools')
BEGIN
    ALTER TABLE dbo.WidgetTokens ADD AllowedTools NVARCHAR(MAX) NULL;
END;
IF NOT EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.WidgetTokens') AND name = 'CacheTtlSeconds')
BEGIN
    ALTER TABLE dbo.WidgetTokens ADD CacheTtlSeconds INT NOT NULL CONSTRAINT DF_WidgetTokens_CacheTtl DEFAULT 300;
END;

-- Visa wizard: cấu hình câu hỏi per-tenant. Trống = dùng default embedded ở frontend.
-- Tenant admin có thể PUT JSON khác (vd: bỏ Q3 rủi ro cao, thêm câu mới về visa Schengen cũ).
IF OBJECT_ID('dbo.VisaQuestionSets', 'U') IS NULL
BEGIN
    CREATE TABLE dbo.VisaQuestionSets (
        TenantId       NVARCHAR(128) NOT NULL,
        QuestionsJson  NVARCHAR(MAX) NOT NULL,   -- JSON array, schema giống DEFAULT_QUESTIONS frontend
        UpdatedBy      NVARCHAR(256) NULL,
        UpdatedAt      DATETIME2     NOT NULL,
        CONSTRAINT PK_VisaQuestionSets PRIMARY KEY CLUSTERED (TenantId)
    );
END;
";
}
