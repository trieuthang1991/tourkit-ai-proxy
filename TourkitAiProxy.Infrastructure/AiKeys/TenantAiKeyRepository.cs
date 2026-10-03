using Dapper;
using TourkitAiProxy.Domain.AiKeys;
using TourkitAiProxy.Infrastructure.Db;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.AiKeys;

/// <summary>
/// Đọc/ghi <c>dbo.TenantAiKeys</c> — key AI riêng của từng công ty (BYO, mở lại 03/10/2026).
///
/// <para><b>Key thô không bao giờ ra khỏi lớp này theo đường trả cho giao diện.</b> Lưu thì mã hoá
/// Crypton + tính sẵn bản che; đọc cho giao diện (<see cref="GetAsync"/>) chỉ trả bản che. Bản giải
/// mã chỉ đi theo <see cref="LoadAllAsync"/>, cho bộ nhớ đệm phía máy chủ dùng để gọi AI.</para>
///
/// <para><b>Các hàm nằm trên đường đi của lệnh AI KHÔNG BAO GIỜ ném</b> (<see cref="LoadAllAsync"/>,
/// <see cref="RecordFailAsync"/>, <see cref="RecordOkAsync"/>): lỗi thì ghi log rồi trả kết quả rỗng.
/// Tính năng mới không được phép làm hỏng lệnh AI đang chạy — chủ dự án dặn rõ.</para>
/// </summary>
public class TenantAiKeyRepository
{
    private readonly TourkitAiDb _db;
    private readonly ILogger<TenantAiKeyRepository> _log;

    public TenantAiKeyRepository(TourkitAiDb db, ILogger<TenantAiKeyRepository> log)
    {
        _db = db; _log = log;
    }

    private const string Columns = """
        TenantId, Provider, Model, ApiKeyEnc, Masked, Enabled, ValidatedAtUtc, UpdatedBy,
        UpdatedAtUtc, LastFailAtUtc, LastFailReason, FailCountSinceOk
        """;

    private sealed class Row
    {
        public string TenantId { get; set; } = "";
        public string Provider { get; set; } = "";
        public string? Model { get; set; }
        public string ApiKeyEnc { get; set; } = "";
        public string Masked { get; set; } = "";
        public bool Enabled { get; set; }
        public DateTime? ValidatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime? LastFailAtUtc { get; set; }
        public string? LastFailReason { get; set; }
        public int FailCountSinceOk { get; set; }

        // CSDL trả DateTime Kind=Unspecified — gắn UTC ngay đây, xem docs/datetime-convention.md.
        public TenantAiKey ToDomain() => new(TenantId, Provider, Model, ApiKeyEnc, Masked, Enabled,
            Utc(ValidatedAtUtc), UpdatedBy, DateTime.SpecifyKind(UpdatedAtUtc, DateTimeKind.Utc),
            Utc(LastFailAtUtc), LastFailReason, FailCountSinceOk);

        private static DateTime? Utc(DateTime? d)
            => d is { } v ? DateTime.SpecifyKind(v, DateTimeKind.Utc) : null;
    }

    /// <summary>Một dòng kèm key đã giải mã — CHỈ dùng nội bộ máy chủ, không bao giờ serialize.</summary>
    public record Loaded(TenantAiKey Key, string? RawApiKey);

    /// <summary>
    /// Nạp TOÀN BỘ cấu hình đang bật, kèm key đã giải mã — cho bộ nhớ đệm làm mới định kỳ.
    ///
    /// <para>Giải mã MỘT LẦN ở đây thay vì mỗi lượt gọi AI: Crypton dẫn khoá qua PasswordDeriveBytes
    /// mỗi lần gọi, đặt trên đường đi của 32 chỗ gọi AI là thêm độ trễ vô ích. Giải mã hỏng thì trả
    /// key rỗng — <see cref="TenantAiKeyRules.Decide"/> coi đó là không có key, chạy như cũ.</para>
    ///
    /// <para>KHÔNG ném. Lỗi thì trả <c>null</c> — <b>KHÁC</b> danh sách rỗng ("không công ty nào khai
    /// key"). Chỗ gọi phải giữ nguyên bản cũ khi nhận <c>null</c>: thay bằng rỗng thì một nhịp CSDL
    /// chập chờn là mọi công ty đang dùng key riêng bỗng bị trừ lượt cho tới lần nạp sau.</para>
    /// </summary>
    public async Task<IReadOnlyList<Loaded>?> LoadAllAsync(CancellationToken ct = default)
        => await LoadAllSealedAsync(ct) is { } rows ? rows.Select(Unseal).ToList() : null;

    /// <summary>
    /// Như <see cref="LoadAllAsync"/> nhưng KHÔNG giải mã — key vẫn ở dạng Crypton. Đây là dạng duy nhất
    /// được phép đưa vào Redis: key thô của khách không bao giờ nằm ngoài RAM của máy chủ.
    /// KHÔNG ném; lỗi thì trả <c>null</c> (khác rỗng — xem <see cref="LoadAllAsync"/>).
    /// </summary>
    public async Task<IReadOnlyList<TenantAiKey>?> LoadAllSealedAsync(CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            var rows = await c.QueryAsync<Row>(new CommandDefinition(
                $"SELECT {Columns} FROM dbo.TenantAiKeys WHERE Enabled = 1", cancellationToken: ct));
            return rows.Select(r => r.ToDomain()).ToList();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] nạp cấu hình key hỏng — giữ nguyên bản đã nạp lần trước");
            return null;
        }
    }

    /// <summary>
    /// Giải mã key của một dòng để đem đi gọi AI. Giải mã hỏng thì key rỗng —
    /// <c>TenantAiKeyRules.Decide</c> coi đó là không có key, công ty đó chạy bằng key hệ thống.
    /// </summary>
    public Loaded Unseal(TenantAiKey k)
    {
        var raw = Crypton.Decrypt(k.ApiKeyEnc);
        if (string.IsNullOrEmpty(raw))
            _log.LogWarning("[byo-key] giải mã key của {Tenant} hỏng — công ty này dùng key hệ thống", k.TenantId);
        return new Loaded(k, string.IsNullOrEmpty(raw) ? null : raw);
    }

    /// <summary>Cấu hình của một công ty cho trang cài đặt — KHÔNG kèm key thô.</summary>
    public async Task<TenantAiKey?> GetAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        var r = await c.QueryFirstOrDefaultAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM dbo.TenantAiKeys WHERE TenantId = @t",
            new { t = tenantId }, cancellationToken: ct));
        return r?.ToDomain();
    }

    /// <summary>
    /// Lưu cấu hình. Mã hoá key + tính bản che ở đây để không chỗ nào khác phải cầm key thô.
    /// Lưu xong thì xoá trạng thái lỗi cũ: key mới vừa được kiểm, lỗi của key cũ không còn đúng.
    /// </summary>
    /// <param name="validatedAtUtc">Mốc kiểm thành công. Chỗ gọi PHẢI kiểm trước rồi mới lưu.</param>
    public async Task UpsertAsync(string tenantId, string provider, string? model, string rawApiKey,
        bool enabled, DateTime validatedAtUtc, string? updatedBy, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync(new CommandDefinition("""
            MERGE dbo.TenantAiKeys WITH (HOLDLOCK) AS t
            USING (SELECT @TenantId AS TenantId) AS s ON t.TenantId = s.TenantId
            WHEN MATCHED THEN UPDATE SET
                Provider = @Provider, Model = @Model, ApiKeyEnc = @ApiKeyEnc, Masked = @Masked,
                Enabled = @Enabled, ValidatedAtUtc = @ValidatedAtUtc, UpdatedBy = @UpdatedBy,
                UpdatedAtUtc = SYSUTCDATETIME(),
                LastFailAtUtc = NULL, LastFailReason = NULL, FailCountSinceOk = 0
            WHEN NOT MATCHED THEN INSERT
                (TenantId, Provider, Model, ApiKeyEnc, Masked, Enabled, ValidatedAtUtc, UpdatedBy)
                VALUES (@TenantId, @Provider, @Model, @ApiKeyEnc, @Masked, @Enabled, @ValidatedAtUtc, @UpdatedBy);
            """,
            new
            {
                TenantId = tenantId,
                Provider = provider.Trim().ToLowerInvariant(),
                Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
                ApiKeyEnc = Crypton.Encrypt(rawApiKey),
                Masked = TenantAiKey.MaskOf(rawApiKey),
                Enabled = enabled,
                ValidatedAtUtc = validatedAtUtc,
                UpdatedBy = updatedBy,
            }, cancellationToken: ct));
    }

    /// <summary>Bật/tắt mà không phải nhập lại key.</summary>
    public async Task<bool> SetEnabledAsync(string tenantId, bool enabled, string? updatedBy,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition("""
            UPDATE dbo.TenantAiKeys
               SET Enabled = @enabled, UpdatedBy = @updatedBy, UpdatedAtUtc = SYSUTCDATETIME()
             WHERE TenantId = @tenantId
            """, new { tenantId, enabled, updatedBy }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> DeleteAsync(string tenantId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync(new CommandDefinition(
            "DELETE FROM dbo.TenantAiKeys WHERE TenantId = @t",
            new { t = tenantId }, cancellationToken: ct)) > 0;
    }

    /// <summary>
    /// Ghi lại một lần key riêng hỏng và hệ thống đã lùi về key chung. KHÔNG ném.
    /// Chỉ gọi khi thật sự lùi — không phải mỗi lượt gọi.
    /// </summary>
    public async Task RecordFailAsync(string tenantId, string reason, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.TenantAiKeys
                   SET LastFailAtUtc = SYSUTCDATETIME(),
                       LastFailReason = LEFT(@reason, 256),
                       FailCountSinceOk = FailCountSinceOk + 1
                 WHERE TenantId = @tenantId
                """, new { tenantId, reason }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] ghi trạng thái lỗi key của {Tenant} hỏng", tenantId);
        }
    }

    /// <summary>
    /// Key riêng chạy lại được → xoá cờ đang lỗi. KHÔNG ném.
    ///
    /// <para>⚠️ <b>Chỗ gọi CHỈ gọi khi đang có lỗi</b> (<see cref="TenantAiKey.IsFailing"/>). Gọi ở mọi
    /// lượt thành công là thêm một lần ghi CSDL vào đường đi của mọi lệnh AI — đúng thứ không được
    /// làm. Mệnh đề <c>WHERE … &gt; 0</c> chỉ là lớp chặn thứ hai, không thay được lớp thứ nhất.</para>
    /// </summary>
    public async Task RecordOkAsync(string tenantId, CancellationToken ct = default)
    {
        try
        {
            await using var c = await _db.OpenAsync(ct);
            await c.ExecuteAsync(new CommandDefinition("""
                UPDATE dbo.TenantAiKeys SET FailCountSinceOk = 0
                 WHERE TenantId = @tenantId AND FailCountSinceOk > 0
                """, new { tenantId }, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[byo-key] xoá trạng thái lỗi key của {Tenant} hỏng", tenantId);
        }
    }
}
