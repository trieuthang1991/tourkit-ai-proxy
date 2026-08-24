// Services/Chat/Inbox/ChatDb.cs
using Dapper;
using Npgsql;
using TourkitAiProxy.Services.Security;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Kết nối + dựng bảng cho CSDL hộp thư chat — <b>PostgreSQL RIÊNG</b>, không phải SQL Server.
///
/// <para><b>Vì sao tách khỏi <c>PushDb</c>:</b> CSDL chat chạy PostgreSQL để dùng được
/// <c>pgvector</c> (tìm hội thoại theo ngữ nghĩa) — thứ SQL Server 2022 không có, phải tới bản 2025.
/// Cái giá: <b>không JOIN được</b> với khách hàng/tour bên SQL Server, và <b>không có giao dịch
/// chung</b>. Xem <c>docs/superpowers/plans/2026-08-20-omnichannel-chat-dot1.md</c>.</para>
///
/// <para><b>Thiếu khoá thì cụm chat tự tắt, KHÔNG làm sập app.</b> Chat là tính năng thêm; hệ đang
/// chạy (bản tin, hộp thư mail, trợ lý) không được chết chỉ vì chưa khai chuỗi kết nối chat.</para>
/// </summary>
public class ChatDb
{
    private readonly string? _connStr;
    private readonly ILogger<ChatDb> _log;

    /// Có cấu hình hay không. Chỗ nào dùng ChatDb đều PHẢI hỏi cái này trước.
    public bool Configured => _connStr != null;

    static ChatDb()
    {
        // Cột PostgreSQL đặt snake_case, thuộc tính C# đặt PascalCase. Không bật cái này thì Dapper
        // map ra null cho MỌI thuộc tính mà KHÔNG BÁO LỖI — kiểu hỏng im lặng tệ nhất để lần ra.
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public ChatDb(IConfiguration cfg, ILogger<ChatDb> log)
    {
        _log = log;
        var conn = cfg.GetConnectionString("Chat");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _log.LogInformation("Chưa khai ConnectionStrings:Chat — cụm hộp thư chat tắt");
            _connStr = null;
            return;
        }
        // Chuỗi có thể ở dạng ENC: (Crypton) như PushDb, hoặc dạng rõ.
        _connStr = conn.StartsWith("ENC:") ? Crypton.Decrypt(conn[4..]) : conn;
    }

    /// Mở kết nối mới. Caller dispose.
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken ct = default)
    {
        if (_connStr == null)
            throw new InvalidOperationException("Chưa khai ConnectionStrings:Chat — kiểm Configured trước khi gọi");
        var c = new NpgsqlConnection(_connStr);
        await c.OpenAsync(ct);
        return c;
    }

    /// Dựng bảng nếu chưa có. Chạy 1 lần lúc khởi động, an toàn chạy lại nhiều lần.
    public async Task InitAsync(CancellationToken ct = default)
    {
        if (!Configured) return;
        try
        {
            await using var c = await OpenAsync(ct);
            await using var cmd = c.CreateCommand();
            cmd.CommandText = SchemaSql;
            await cmd.ExecuteNonQueryAsync(ct);
            _log.LogInformation("ChatDb schema OK (chat_contacts/chat_conversations/chat_messages/chat_outbox)");
        }
        catch (Exception ex)
        {
            // Không ném: chat hỏng thì phần còn lại của hệ vẫn phải chạy.
            _log.LogError(ex, "ChatDb InitAsync thất bại — cụm hộp thư chat sẽ không dùng được");
        }
    }

    /// <summary>
    /// Schema hộp thư chat. Cố ý viết SQL thuần thay vì migration nhị phân: đọc được, sửa được,
    /// và chạy lại bao nhiêu lần cũng không sao.
    ///
    /// <para><b>timestamptz chứ không phải timestamp.</b> Loại không mang múi giờ sẽ khiến giờ VN và
    /// giờ UTC lẫn vào nhau — đúng kiểu lỗi đã ghi ở docs/datetime-convention.md.</para>
    /// </summary>
    private const string SchemaSql = """
    -- Danh tính khách theo TỪNG KÊNH. KHÔNG phải danh bạ thứ hai: crm_customer_id trỏ về khách
    -- trong CRM (SQL Server, khác máy chủ nên không đặt khoá ngoại được). Chưa nhận ra là ai thì
    -- để NULL — gộp nhầm hai khách thành một còn tệ hơn không gộp.
    CREATE TABLE IF NOT EXISTS chat_contacts (
      tenant_id       text        NOT NULL,
      channel         smallint    NOT NULL,
      external_id     text        NOT NULL,
      display_name    text,
      avatar_url      text,
      phone           text,
      email           text,
      crm_customer_id integer,
      created_utc     timestamptz NOT NULL DEFAULT now(),
      updated_utc     timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, channel, external_id)
    );

    -- Một luồng chat với một khách trên một kênh.
    CREATE TABLE IF NOT EXISTS chat_conversations (
      id                   bigserial PRIMARY KEY,
      tenant_id            text     NOT NULL,
      channel              smallint NOT NULL,
      contact_external_id  text     NOT NULL,
      -- TÀI KHOẢN nào của công ty nhận tin này (Trang Facebook / OA Zalo / bot Telegram cụ thể) —
      -- một công ty giờ có thể nối NHIỀU tài khoản/kênh, nên phải nhớ cuộc trò chuyện này thuộc
      -- tài khoản nào để trả lời đúng danh nghĩa, không lẫn sang tài khoản khác.
      account_id           text     NOT NULL DEFAULT '',
      status               smallint NOT NULL DEFAULT 0,   -- 0=mới 1=đang xử lý 2=đã đóng
      assigned_username    text,
      -- MỐC THỜI GIAN, không phải cờ bật/tắt: nhân viên nhảy vào trả lời thì bot câm CÓ THỜI HẠN,
      -- hết hạn tự nói lại. Làm thành cờ thì sẽ có hội thoại tắt bot vĩnh viễn chỉ vì hôm đó có
      -- người lỡ nhắn một câu, và không ai nhớ để bật lại.
      bot_resume_at        timestamptz,
      contact_replied_at   timestamptz,   -- mốc tính CỬA SỔ GỬI của kênh (Zalo 48h, Messenger 24h)
      agent_replied_at     timestamptz,
      contact_last_read_at timestamptz,
      agent_last_read_at   timestamptz,
      last_activity_at     timestamptz NOT NULL DEFAULT now(),
      last_preview         text,
      archived_at          timestamptz,
      created_utc          timestamptz NOT NULL DEFAULT now()
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_conv_scope
      ON chat_conversations (tenant_id, channel, contact_external_id);
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_status
      ON chat_conversations (tenant_id, status, last_activity_at DESC);
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_assignee
      ON chat_conversations (tenant_id, assigned_username, last_activity_at DESC);
    -- account_id thêm sau (24/08, đa tài khoản/kênh) — bảng đã tồn tại từ trước thì CREATE TABLE
    -- IF NOT EXISTS ở trên là no-op, phải ALTER riêng mới thấy cột mới.
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS account_id text NOT NULL DEFAULT '';

    CREATE TABLE IF NOT EXISTS chat_messages (
      id              bigserial PRIMARY KEY,
      tenant_id       text     NOT NULL,
      conversation_id bigint   NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
      channel         smallint NOT NULL,
      direction       smallint NOT NULL,            -- 0=khách gửi 1=mình gửi
      sender_kind     smallint NOT NULL,            -- 0=khách 1=AI 2=nhân viên 3=hệ thống
      sender_username text,
      kind            smallint NOT NULL DEFAULT 0,  -- 0=chữ 1=ảnh 2=tệp 3=âm thanh 4=sticker 5=vị trí
      body            text,
      attachment      jsonb,
      external_msg_id text,
      state           smallint NOT NULL DEFAULT 0,  -- 0=chờ 1=đã gửi 2=đã nhận 3=đã xem 4=hỏng
      error_message   text,
      created_utc     timestamptz NOT NULL DEFAULT now(),
      processed_utc   timestamptz
    );
    -- Chống trùng đặt ở TẦNG CSDL, không chỉ kiểm trong code: webhook của kênh gửi lại khi không
    -- nhận được 200, hai lần gửi song song thì kiểm trong code vẫn lọt, chỉ mục thì không.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_msg_external
      ON chat_messages (tenant_id, channel, external_msg_id) WHERE external_msg_id IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_msg_conv ON chat_messages (conversation_id, created_utc);

    -- Hàng đợi gửi RIÊNG cho chat. KHÔNG dùng dbo.OutboundMails: khác máy chủ, và khác vòng đời
    -- (thông báo có mẫu + lịch gửi; chat gửi ngay, chữ tự do, có cửa sổ thời gian theo kênh).
    CREATE TABLE IF NOT EXISTS chat_outbox (
      id              bigserial PRIMARY KEY,
      tenant_id       text     NOT NULL,
      conversation_id bigint   NOT NULL,
      message_id      bigint   NOT NULL,
      status          smallint NOT NULL DEFAULT 0,  -- 0=chờ 1=đã gửi 2=hỏng 4=bỏ qua
      retry_count     integer  NOT NULL DEFAULT 0,
      error_message   text,
      created_utc     timestamptz NOT NULL DEFAULT now(),
      processed_utc   timestamptz
    );
    -- Chỉ mục CÓ ĐIỀU KIỆN: worker chỉ hỏi dòng đang chờ. Không có nó thì mỗi vài giây lại quét
    -- cả bảng, mà bảng này chỉ phình chứ không co lại.
    CREATE INDEX IF NOT EXISTS ix_outbox_cho ON chat_outbox (created_utc) WHERE status = 0;

    -- Mẫu trả lời nhanh, theo TỪNG CÔNG TY (KHÔNG theo từng nhân viên) — cả đội trực chat cùng
    -- dùng một bộ câu, đổi một mẫu là cả đội thấy ngay, không phải dạy lại từng người.
    CREATE TABLE IF NOT EXISTS chat_quick_replies (
      id          bigserial PRIMARY KEY,
      tenant_id   text        NOT NULL,
      trigger     text        NOT NULL,   -- gõ sau dấu "/", vd "gia" cho lệnh "/gia"
      body        text        NOT NULL,
      created_utc timestamptz NOT NULL DEFAULT now(),
      updated_utc timestamptz NOT NULL DEFAULT now()
    );
    CREATE UNIQUE INDEX IF NOT EXISTS ux_quickreply_trigger
      ON chat_quick_replies (tenant_id, lower(trigger));
    """;
}
