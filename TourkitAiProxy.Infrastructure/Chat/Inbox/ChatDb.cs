// Services/Chat/Inbox/ChatDb.cs
using Dapper;
using Npgsql;
using TourkitAiProxy.Infrastructure.Security;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

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
            _log.LogInformation("ChatDb schema OK (chat_contacts/chat_conversations/chat_messages/chat_reactions/chat_outbox)");
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
    -- Khoá hội thoại PHẢI có account_id. Thiếu nó thì hai tài khoản cùng kênh của cùng công ty
    -- gộp nhầm hội thoại của cùng một khách, và câu trả lời đi ra SAI tài khoản.
    --
    -- Không phải kênh nào cũng lộ ra như nhau, nên rất dễ tưởng là an toàn:
    --   • Messenger — PSID cấp theo TỪNG Trang, hai Trang cho hai id khác nhau → tình cờ không sao;
    --   • Zalo      — user id cấp theo TỪNG OA → cũng không sao;
    --   • Telegram  — chat.id của chat riêng CHÍNH LÀ id người dùng, GIỐNG HỆT ở mọi bot. Một khách
    --     nhắn bot A rồi nhắn bot B sẽ rơi vào cùng một hội thoại, và bot B trả lời tin của bot A.
    --
    -- Thứ tự CỐ Ý: tạo chỉ mục mới TRƯỚC rồi mới bỏ cái cũ. Nếu dữ liệu đang có trùng thì lệnh tạo
    -- hỏng, cả khối SQL dừng, và chỉ mục CŨ vẫn còn nguyên — vẫn chống trùng. Bỏ trước tạo sau thì
    -- lúc hỏng sẽ không còn chỉ mục duy nhất nào cả, mất luôn lớp chống trùng ở tầng CSDL.
    -- ALTER phải chạy TRƯỚC chỉ mục bên dưới, vì chỉ mục dùng chính cột này. Với CSDL đã tồn tại
    -- từ trước 24/08 thì CREATE TABLE IF NOT EXISTS ở trên là no-op — cột account_id chưa có, và
    -- CREATE INDEX ... (account_id) sẽ hỏng "column does not exist", kéo cả khối SQL dừng theo.
    -- Đặt sau chỉ mục thì chỉ CSDL nào ĐÃ có sẵn cột mới chạy được, tức là hỏng đúng ở máy chưa
    -- nâng cấp — chỗ cần nó chạy nhất.
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS account_id text NOT NULL DEFAULT '';

    -- KHÁCH ĐẾN TỪ ĐÂU. Meta chỉ nói MỘT LẦN, ngay lúc khách mở cuộc trò chuyện; không ghi lại
    -- lúc đó là mất vĩnh viễn, không có API nào tra ngược được.
    --
    -- Ghi MỘT LẦN rồi thôi (COALESCE lúc cập nhật): khách quay lại qua một quảng cáo khác thì
    -- nguồn ĐẦU TIÊN mới là cái đã kéo họ tới, đè lên là hỏng số liệu quy công quảng cáo.
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS referral_source text;   -- ADS | SHORTLINK | CUSTOMER_CHAT_PLUGIN…
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS referral_ref    text;   -- tham số ref mình tự đặt trên liên kết/QR
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS referral_ad_id  text;   -- id quảng cáo, khi source = ADS

    CREATE UNIQUE INDEX IF NOT EXISTS ux_conv_scope_acc
      ON chat_conversations (tenant_id, channel, account_id, contact_external_id);
    DROP INDEX IF EXISTS ux_conv_scope;
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_status
      ON chat_conversations (tenant_id, status, last_activity_at DESC);
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_assignee
      ON chat_conversations (tenant_id, assigned_username, last_activity_at DESC);
    -- Phân trang con trỏ: khớp ORDER BY last_activity_at DESC, id DESC của ListConversationsAsync.
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_hoatdong
      ON chat_conversations (tenant_id, last_activity_at DESC, id DESC);

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
    -- Nút bấm ĐÃ GỬI kèm tin. Lưu lại để hộp thư vẽ đúng thứ khách nhìn thấy khi đọc lại hội
    -- thoại — không lưu thì dòng tin chỉ còn chữ, và không ai biết khách đã được mời chọn gì.
    -- Dạng: [{"chu":"Xem tour","url":"https://…"},{"chu":"Gọi lại cho tôi"}]
    ALTER TABLE chat_messages ADD COLUMN IF NOT EXISTS buttons jsonb;

    -- Chống trùng đặt ở TẦNG CSDL, không chỉ kiểm trong code: webhook của kênh gửi lại khi không
    -- nhận được 200, hai lần gửi song song thì kiểm trong code vẫn lọt, chỉ mục thì không.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_msg_external
      ON chat_messages (tenant_id, channel, external_msg_id) WHERE external_msg_id IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_msg_conv ON chat_messages (conversation_id, created_utc);

    -- Cảm xúc khách thả lên một tin. BẢNG RIÊNG, không phải cột trên chat_messages và cũng
    -- không phải một dòng trong chat_messages:
    --   * nhiều người thả lên cùng một tin (nhóm chat), một cột không chứa nổi;
    --   * thả rồi GỠ là chuyện thường, xoá một dòng dễ hơn sửa JSON trong cột;
    --   * ghi thành tin mới thì "❤️" hiện như một câu khách nói, và mọi thứ đếm theo tin
    --     (chưa đọc, xem trước, cửa sổ trả lời) đều lệch.
    -- Khoá theo external_msg_id chứ không theo id nội bộ: cảm xúc có thể tới TRƯỚC khi tin được
    -- ghi xong (hai worker khác nhịp), tham chiếu khoá ngoại lúc đó sẽ ném.
    CREATE TABLE IF NOT EXISTS chat_reactions (
      tenant_id       text     NOT NULL,
      channel         smallint NOT NULL,
      external_msg_id text     NOT NULL,
      actor_external_id text   NOT NULL,        -- ai thả (mã người dùng của kênh)
      emoji           text,
      reaction_name   text,                     -- tên nhà cung cấp đặt: love, like, wow…
      created_utc     timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, channel, external_msg_id, actor_external_id)
    );

    -- Hàng đợi gửi RIÊNG cho chat. KHÔNG dùng dbo.OutboundMails: khác máy chủ, và khác vòng đời
    -- (thông báo có mẫu + lịch gửi; chat gửi ngay, chữ tự do, có cửa sổ thời gian theo kênh).
    -- Sự kiện webhook ĐÃ NHẬN, chưa xử lý. Webhook chỉ ghi vào đây rồi trả 200; xử lý là việc
    -- của ChatInboundWorker. Trước đây webhook trả 200 rồi mới `Task.Run` xử lý — đã trả 200
    -- nghĩa là kênh coi như giao xong và không gửi lại, nên app chết trong vài giây đó là mất
    -- hẳn tin của khách, không dấu vết.
    CREATE TABLE IF NOT EXISTS chat_inbound_events (
      id                bigserial PRIMARY KEY,
      tenant_id         text     NOT NULL,
      channel           smallint NOT NULL,
      account_id        text     NOT NULL,
      provider_event_id text,             -- id sự kiện phía kênh, dùng chống trùng
      raw_body          text     NOT NULL,
      status            smallint NOT NULL DEFAULT 0,  -- 0=chờ 1=xong 2=hỏng 3=đang xử lý
      retry_count       integer  NOT NULL DEFAULT 0,
      error_message     text,
      created_utc       timestamptz NOT NULL DEFAULT now(),
      processed_utc     timestamptz
    );
    -- Chống trùng ở TẦNG CSDL. Kiểm-rồi-ghi trong code vẫn lọt khi kênh gửi lại đồng thời.
    -- Partial index: sự kiện không có id thì không chống trùng được, cứ nhận.
    CREATE UNIQUE INDEX IF NOT EXISTS ux_inbound_event
      ON chat_inbound_events (tenant_id, channel, provider_event_id)
      WHERE provider_event_id IS NOT NULL;
    CREATE INDEX IF NOT EXISTS ix_inbound_cho
      ON chat_inbound_events (created_utc) WHERE status = 0;

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
    -- Nút bấm gắn kèm mẫu trả lời nhanh. Cột THÊM chứ không phải bảng mới: nút không sống
    -- độc lập với câu trả lời, và tách bảng thì mọi lượt đọc mẫu phải join thêm một lần.
    ALTER TABLE chat_quick_replies ADD COLUMN IF NOT EXISTS buttons jsonb;

    CREATE UNIQUE INDEX IF NOT EXISTS ux_quickreply_trigger
      ON chat_quick_replies (tenant_id, lower(trigger));

    -- Đã đọc theo TỪNG NGƯỜI. Trước đây chỉ có chat_conversations.agent_last_read_at — MỘT cột
    -- cho cả công ty, nên A mở hội thoại là B cũng mất dấu chưa đọc. Hộp thư một người thì không
    -- lộ ra; hai người trở lên là sai ngay, mà sai im lặng: không có lỗi nào hiện, chỉ có tin của
    -- khách trôi qua mắt người thứ hai.
    --
    -- Cột cũ VẪN GIỮ, làm mốc ban đầu cho người chưa có dòng nào ở đây. Xoá nó là mọi hội thoại
    -- cũ bật lại thành "chưa đọc" cho tất cả mọi người ngay sau khi deploy.
    CREATE TABLE IF NOT EXISTS chat_conversation_reads (
      tenant_id       text        NOT NULL,
      conversation_id bigint      NOT NULL REFERENCES chat_conversations(id) ON DELETE CASCADE,
      username        text        NOT NULL,
      last_read_at    timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, conversation_id, username)
    );

    -- Nhật ký thao tác. Khi khách khiếu nại "ai nói câu này với tôi", hoặc một hội thoại bị
    -- đóng nhầm, thì đây là chỗ duy nhất tra được.
    --
    -- chi_tiet KHÔNG chứa nội dung tin: tin đã nằm ở chat_messages, chép lại là nhân đôi dữ
    -- liệu khách VÀ nhân đôi chỗ phải xoá khi khách yêu cầu xoá dữ liệu — sót một chỗ là vẫn
    -- còn lưu trái ý khách.
    CREATE TABLE IF NOT EXISTS chat_audit (
      id              bigserial PRIMARY KEY,
      tenant_id       text        NOT NULL,
      conversation_id bigint,
      username        text        NOT NULL,
      hanh_dong       text        NOT NULL,   -- nhan-viec | nha-viec | chuyen-viec | doi-trang-thai | tam-dung-bot | go-ket-noi
      chi_tiet        jsonb,
      created_utc     timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS ix_audit_conv
      ON chat_audit (tenant_id, conversation_id, created_utc DESC);

    -- Nhãn theo KHÁCH (không theo hội thoại): khách nhắn lại sau ba tháng vẫn còn nhãn cũ,
    -- còn gắn theo hội thoại thì mỗi lần mở hội thoại mới là mất hết.
    --
    -- tag đã CHUẨN HOÁ (bỏ dấu, gạch nối) trước khi ghi — xem ChatRules.NormalizeSlug. Ghi thô
    -- thì "Khách VIP" và "khach vip" thành hai nhãn khác nhau, lọc ra rỗng mà không ai hiểu.
    CREATE TABLE IF NOT EXISTS chat_contact_tags (
      tenant_id   text NOT NULL,
      channel     smallint NOT NULL,
      external_id text NOT NULL,
      tag         text NOT NULL,
      created_utc timestamptz NOT NULL DEFAULT now(),
      PRIMARY KEY (tenant_id, channel, external_id, tag)
    );

    -- Ghi chú nội bộ về khách. KHÁCH KHÔNG BAO GIỜ THẤY — chỉ nhân viên đọc, nên đây là chỗ
    -- ghi "khách khó tính, đừng gọi trước 9h" mà không sợ lộ.
    CREATE TABLE IF NOT EXISTS chat_contact_notes (
      id          bigserial PRIMARY KEY,
      tenant_id   text NOT NULL,
      channel     smallint NOT NULL,
      external_id text NOT NULL,
      username    text NOT NULL,
      noi_dung    text NOT NULL,
      created_utc timestamptz NOT NULL DEFAULT now()
    );
    CREATE INDEX IF NOT EXISTS ix_note_contact
      ON chat_contact_notes (tenant_id, channel, external_id, created_utc DESC);
    """;
}
