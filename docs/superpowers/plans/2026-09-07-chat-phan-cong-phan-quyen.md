# Phân công & phân quyền xem hộp thư chat — Kế hoạch thi công

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Hộp thư chat gán được người phụ trách (thủ công hoặc xoay vòng), và người không phải admin chỉ đọc được hội thoại đã giao cho mình.

**Architecture:** Khoá định danh là `user_id` của CRM, đã có sẵn trên phiên (`TkSession.CrmUserId`). Quyền xem chặn ở **một cửa duy nhất** — đổi chữ ký `ChatRepository.GetConversationAsync`, nên 26 endpoint hiện có được phủ mà không sửa nhánh nào, và endpoint mới quên truyền thì **lỗi biên dịch**. Vòng quay là **một câu SQL** có điều kiện `assigned_user_id IS NULL` nằm trong chính câu đó.

**Tech Stack:** ASP.NET Core 8 Minimal API · Dapper · PostgreSQL 18 (CSDL chat riêng) · xUnit · React qua Babel (không bundler lúc dev).

**Spec:** [docs/superpowers/specs/2026-09-07-chat-phan-cong-phan-quyen-design.md](../specs/2026-09-07-chat-phan-cong-phan-quyen-design.md)

## Global Constraints

- **Chữ hiển thị, log, chú thích viết tiếng Việt.** Tên định danh theo file đang sửa.
- **Ngày giờ là UTC, luôn kèm `Z`** — xem `docs/datetime-convention.md`.
- **Không có CI chạy PostgreSQL.** Toàn bộ test là **logic thuần** + **guard đọc mã nguồn dạng văn bản** (mẫu: `TourkitAiProxy.Tests/Chat/ChatClaimGuardTests.cs`). Đừng viết test cần kết nối CSDL.
- **Chạy TOÀN BỘ test, đừng lọc:** `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` — guard kiến trúc nằm rải trong đó.
- **Schema là SQL thuần trong `ChatDb.SchemaSql`**, chạy mỗi lần khởi động, phải **idempotent** (`IF NOT EXISTS`). Không có migration tool.
- **Chỉ THÊM cột/bảng.** Không sửa, không xoá cột cũ — `assigned_username` phải còn nguyên.
- **`assigned_user_id IS NULL` = chưa ai phụ trách.** Đây là điều kiện duy nhất để vòng quay được phép gán.
- **Luật xem:** admin xem tất cả · không admin **chỉ** xem hội thoại `assigned_user_id = mình`. Hội thoại chưa gán **KHÔNG** hiện với người không phải admin — đây là quyết định của chủ dự án 07/09/2026, không phải sơ suất.
- **Cờ `Features:ChatAssign` mặc định TẮT.** Thiếu key = tắt.
- **Chưa có dòng `chat_assign_settings` = giữ nguyên hành vi hôm nay** (mọi người xem tất cả).
- **Commit sau mỗi task.** Nhánh làm việc tạo từ `dev`, không commit thẳng `main`.
- **`tourkit/` là dự án tham khảo — tuyệt đối không sửa.**

---

## File Structure

**Tạo mới**

| File | Trách nhiệm |
|---|---|
| `TourkitAiProxy.Domain/Chat/ChatAssign.cs` | `ChatAssignSettings`, `NguoiXem`, hằng `CheDoPhanCong` |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs` | Đọc/ghi cấu hình phân công, đội trực, và câu SQL xoay vòng |
| `TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs` | Guard schema + guard tính nguyên tử của câu xoay vòng |
| `TourkitAiProxy.Tests/Chat/ChatScopeGuardTests.cs` | Guard kiến trúc: mọi route `/conversations/{id…}` đi qua cửa chung |
| `wwwroot/pages/chat-assign-settings.jsx` | Màn hình cấu hình phân công |

**Sửa**

| File | Sửa gì |
|---|---|
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` | Thêm cột + 2 bảng vào `SchemaSql` |
| `TourkitAiProxy.Infrastructure/TourKit/JwtClaims.cs` | Thêm `TryGetIsAdmin` |
| `TourkitAiProxy.Infrastructure/TourKit/TkSessionStore.cs` | Thêm `IsAdmin` vào `TkSession`, nạp lúc login |
| `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs` | Đổi chữ ký `GetConversationAsync` / `ListConversationsAsync`; thêm `ClaimConversationAsync` bản có id |
| `TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs` | Kẹp người xem trong bus |
| `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs` | Gọi xoay vòng sau khi có hội thoại |
| `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` | Truyền `NguoiXem`; kiểm đội trực khi giao việc; API cấu hình |
| `TourkitAiProxy.Endpoints/SessionAuth.cs` | Thêm `ReadNguoiXemAsync` |
| `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs` | Thêm `ChatAssign` |
| `wwwroot/pages/chat-inbox.jsx` | Ô chọn người phụ trách, nhãn nút, tên đầy đủ |
| `CHANGELOG.md` · `docs/features/chat-inbox.md` | Bắt buộc |

---

## Task 1: Schema + kho dữ liệu phân công

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs` (trong hằng `SchemaSql`)
- Create: `TourkitAiProxy.Domain/Chat/ChatAssign.cs`
- Create: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs`
- Test: `TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs`

**Interfaces:**
- Produces: `ChatAssignSettings(string TenantId, short Mode, bool ScopeOwnOnly, bool AutoAssignOnReply, int[] MemberIds, int? RotationLastUserId)` · `NguoiXem(int? CrmUserId, bool XemTatCa)` · `ChatAssignRepository.LayCauHinhAsync(string tenant, CancellationToken ct) → Task<ChatAssignSettings?>` · `.LuuCauHinhAsync(string tenant, short mode, bool scopeOwnOnly, bool autoAssignOnReply, int[] memberIds, CancellationToken ct) → Task`

- [ ] **Step 1: Viết guard schema (test hỏng trước)**

Tạo `TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs`:

```csharp
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh schema phân công. Không có CI chạy PostgreSQL nên đây là lớp duy nhất: đọc thẳng
/// câu SQL trong ChatDb.cs.
/// </summary>
public class ChatAssignSchemaGuardTests
{
    private static string Sql() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");

    [Fact]
    public void Them_cot_nguoi_phu_trach_bang_ma_so()
    {
        // Cột cũ assigned_username giữ nguyên; cột mới mới là khoá so quyền.
        Assert.Contains("ADD COLUMN IF NOT EXISTS assigned_user_id integer", Sql());
        Assert.Contains("assigned_username", Sql());
    }

    [Fact]
    public void Doi_truc_la_MOT_COT_chu_khong_phai_bang_rieng()
    {
        Assert.Contains("CREATE TABLE IF NOT EXISTS chat_assign_settings", Sql());
        // Sau khi khoá định danh đổi sang mã người thì mỗi thành viên đội trực chỉ còn đúng
        // MỘT con số — tách bảng cho một cột số là thêm một lượt đọc mà không được gì.
        Assert.Contains("member_ids integer[]", Sql());
        Assert.DoesNotContain("chat_assign_members", Sql());
    }

    [Fact]
    public void Ma_nguoi_phu_trach_phai_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper đổi NULL thành 0, KHÔNG báo lỗi,
        // và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        var model = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Domain/Chat/ChatModels.cs");
        Assert.Contains("int? AssignedUserId", model);
    }

    [Fact]
    public void Con_tro_xoay_vong_luu_MA_NGUOI_chu_khong_phai_vi_tri()
    {
        // Lưu số thứ tự thì thêm/bớt một người là cả vòng lệch — im lặng.
        Assert.Contains("rotation_last_user_id", Sql());
        Assert.DoesNotContain("rotation_index", Sql());
    }

    [Fact]
    public void Moi_lenh_schema_deu_idempotent()
    {
        // SchemaSql chạy MỖI LẦN khởi động. Một lệnh không IF NOT EXISTS là app chết ở lần
        // khởi động thứ hai.
        var sql = Sql();
        foreach (var manh in new[] { "chat_assign_settings" })
            Assert.Contains($"CREATE TABLE IF NOT EXISTS {manh}", sql);
    }
}
```

- [ ] **Step 2: Chạy test cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatAssignSchemaGuardTests`
Expected: FAIL — không thấy `assigned_user_id`, `chat_assign_settings`, `member_ids`.

- [ ] **Step 3: Thêm schema vào `ChatDb.SchemaSql`**

Chèn vào cuối hằng `SchemaSql` trong `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs`:

```sql
    -- NGƯỜI PHỤ TRÁCH theo MÃ, không theo tên đăng nhập.
    --
    -- Cột cũ assigned_username GIỮ NGUYÊN: nó là thứ đã ghi trong chat_audit, bỏ đi là mọi
    -- dòng nhật ký cũ mất nghĩa. Ghi mới điền CẢ HAI — cột mới để so quyền, cột cũ để hiện.
    ALTER TABLE chat_conversations ADD COLUMN IF NOT EXISTS assigned_user_id integer;
    CREATE INDEX IF NOT EXISTS ix_conv_tenant_nguoi_phutrach
      ON chat_conversations (tenant_id, assigned_user_id, last_activity_at DESC);

    -- Cấu hình phân công, MỘT dòng mỗi công ty.
    --
    -- ⚠️ CHƯA CÓ DÒNG = giữ nguyên hành vi hôm nay (mọi người xem tất cả, không gán tự động).
    -- Đây là đường lùi cho khách đang chạy: nâng cấp mà không ai mất hộp thư sáng hôm sau.
    CREATE TABLE IF NOT EXISTS chat_assign_settings (
      tenant_id             text        PRIMARY KEY,
      mode                  smallint    NOT NULL DEFAULT 1,   -- 1 = thủ công, 2 = xoay vòng
      scope_own_only        boolean     NOT NULL DEFAULT false,
      auto_assign_on_reply  boolean     NOT NULL DEFAULT false,
      -- ĐỘI TRỰC CHAT — mã những người được nhận hội thoại. Một danh sách cho hai việc: vòng
      -- quay chia theo nó, và ô chọn người phụ trách đổ ra nó.
      --
      -- MỘT CỘT chứ không phải bảng riêng: khoá định danh là mã người, nên mỗi thành viên chỉ
      -- còn đúng một con số. Tên hiển thị lấy từ danh sách nhân viên của ERP mà giao diện vốn
      -- đã nạp — chép vào đây là nhân đôi chỗ phải sửa khi ai đó đổi tên.
      member_ids            integer[]   NOT NULL DEFAULT '{}',
      -- MÃ NGƯỜI vừa nhận lượt, KHÔNG phải vị trí trong danh sách. Lưu vị trí thì thêm hoặc
      -- bớt một người là cả vòng lệch: chị A nhận gấp đôi, anh B không nhận cái nào, và không
      -- ai biết vì sao.
      rotation_last_user_id integer,
      updated_utc           timestamptz NOT NULL DEFAULT now()
    );

    -- CSDL nào đã tạo bảng từ bản trước thì CREATE TABLE ở trên là no-op — cột thêm sau phải
    -- có lệnh riêng, y như lần thêm account_id cho chat_conversations.
    ALTER TABLE chat_assign_settings
      ADD COLUMN IF NOT EXISTS member_ids integer[] NOT NULL DEFAULT '{}';
```

- [ ] **Step 4: Chạy lại guard**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatAssignSchemaGuardTests`
Expected: PASS (5/5). Test `Ma_nguoi_phu_trach_phai_la_int_CO_THE_NULL` chỉ xanh sau Step 7
(thêm property `AssignedUserId`) — chạy lại sau bước đó.

- [ ] **Step 5: Tạo model miền**

Tạo `TourkitAiProxy.Domain/Chat/ChatAssign.cs`:

```csharp
namespace TourkitAiProxy.Domain.Chat;

/// Chế độ chia hội thoại cho nhân viên.
public static class CheDoPhanCong
{
    /// Máy không gán gì. Người mở được hội thoại thì tự nhận hoặc giao cho người khác.
    /// Theo luật xem, hội thoại chưa gán chỉ admin mở được — nên trên thực tế đây là
    /// "admin giao xuống", không phải "nhân viên tự bốc".
    public const short ThuCong = 1;

    /// Hội thoại nào chưa có người phụ trách thì gán người kế tiếp trong đội trực.
    public const short XoayVong = 2;
}

/// Cấu hình phân công của MỘT công ty. Chưa có dòng trong CSDL = giữ nguyên hành vi cũ.
///
/// <para><c>MemberIds</c> là ĐỘI TRỰC CHAT — mã những người được nhận hội thoại. Chỉ có mã:
/// tên hiển thị lấy từ danh sách nhân viên của ERP, chép vào đây là nhân đôi chỗ phải sửa khi
/// ai đó đổi tên.</para>
public record ChatAssignSettings(
    string TenantId,
    short  Mode,
    bool   ScopeOwnOnly,
    bool   AutoAssignOnReply,
    int[]  MemberIds,
    int?   RotationLastUserId);

/// Ai đang xem, và được xem tới đâu.
///
/// <para><b>Dựng ở tầng endpoint, KHÔNG dựng trong kho dữ liệu</b> — kho không biết gì về phiên
/// đăng nhập, và để nó tự đoán là mở đường cho một lần đoán sai thành lỗ hổng.</para>
public record NguoiXem(int? CrmUserId, bool XemTatCa)
{
    /// Dùng cho worker và webhook — chỗ không có người dùng nào đứng sau.
    public static readonly NguoiXem HeThong = new(null, true);
}
```

- [ ] **Step 6: Tạo kho dữ liệu**

Tạo `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs`. Dùng `ChatDb` y như `ChatRepository` (đọc `ChatRepository.cs` đầu file để chép đúng cách nhận `ChatDb` qua hàm dựng và cờ `Configured`):

```csharp
using Dapper;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Infrastructure.Chat.Inbox;

/// <summary>
/// Cấu hình phân công + đội trực chat.
///
/// <para>Tách khỏi <see cref="ChatRepository"/> vì đó đã 1.000 dòng và đây là chuyện khác:
/// kia là hội thoại và tin nhắn, đây là luật chia việc.</para>
/// </summary>
public class ChatAssignRepository
{
    private readonly ChatDb _db;
    public ChatAssignRepository(ChatDb db) => _db = db;
    public bool Configured => _db.Configured;

    /// Trả null khi công ty chưa cấu hình — chỗ gọi phải hiểu null là "giữ nguyên hành vi cũ",
    /// KHÔNG phải "chế độ thủ công". Hai thứ khác nhau ở luật xem.
    public async Task<ChatAssignSettings?> LayCauHinhAsync(string tenant, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatAssignSettings>("""
            SELECT tenant_id, mode, scope_own_only, auto_assign_on_reply,
                   member_ids, rotation_last_user_id
              FROM chat_assign_settings WHERE tenant_id = @tenant
            """, new { tenant });
    }

    /// Ghi ĐÈ cả cấu hình lẫn đội trực trong MỘT lệnh. Tách hai lượt ghi thì có khoảnh khắc
    /// chế độ đã là xoay vòng mà đội trực còn rỗng — và trong khoảnh khắc đó mọi hội thoại tới
    /// đều rơi về hàng chờ, im lặng.
    public async Task LuuCauHinhAsync(string tenant, short mode, bool scopeOwnOnly,
        bool autoAssignOnReply, int[] memberIds, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        await c.ExecuteAsync("""
            INSERT INTO chat_assign_settings
                   (tenant_id, mode, scope_own_only, auto_assign_on_reply, member_ids, updated_utc)
            VALUES (@tenant, @mode, @scopeOwnOnly, @autoAssignOnReply, @memberIds, now())
            ON CONFLICT (tenant_id) DO UPDATE
               SET mode = EXCLUDED.mode,
                   scope_own_only = EXCLUDED.scope_own_only,
                   auto_assign_on_reply = EXCLUDED.auto_assign_on_reply,
                   member_ids = EXCLUDED.member_ids,
                   updated_utc = now()
            """, new { tenant, mode, scopeOwnOnly, autoAssignOnReply, memberIds });
        // ⚠️ KHÔNG đụng rotation_last_user_id: sửa cấu hình mà đặt lại con trỏ thì mỗi lần
        // quản trị bấm Lưu là vòng quay bắt đầu lại từ người đầu danh sách.
    }
}
```

- [ ] **Step 7: Thêm `AssignedUserId` vào model hội thoại**

`ChatConversation` là **class có property đặt được** (không phải record — đừng dùng `with { }`).
Trong `TourkitAiProxy.Domain/Chat/ChatModels.cs:235`, ngay dưới `AssignedUsername`, thêm:

```csharp
    // MÃ người phụ trách — khoá thật để so quyền xem. AssignedUsername ngay trên giữ nguyên vì
    // nhật ký cũ (chat_audit) ghi theo tên đăng nhập; bỏ nó là mọi dòng nhật ký mất nghĩa.
    // Dòng cũ tạo trước 07/09/2026 để null: chưa gán bằng mã, không phải chưa có người.
    public int? AssignedUserId { get; set; }
```

⚠️ Không thêm cột này thì `SELECT v.*` vẫn chạy nhưng Dapper **bỏ qua** cột mới, và mọi chỗ đọc
`v.AssignedUserId` ở Task 5/6 không biên dịch được.

- [ ] **Step 8: Đăng ký DI**

Tìm chỗ đăng ký `ChatRepository` (grep `AddSingleton<ChatRepository>` hoặc `AddScoped<ChatRepository>` trong `TourkitAiProxy.Services/Bootstrap/`) và thêm `ChatAssignRepository` **cùng vòng đời đó**.

- [ ] **Step 9: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS toàn bộ. Nếu `ChatSchemaGuardTests` đỏ vì đếm số bảng, cập nhật con số ở đó.

- [ ] **Step 9: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs TourkitAiProxy.Domain/Chat/ChatAssign.cs TourkitAiProxy.Domain/Chat/ChatModels.cs TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs
git commit -m "feat(chat): schema và kho dữ liệu phân công hội thoại"
```

---

## Task 2: Biết ai là admin

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/TourKit/JwtClaims.cs`
- Modify: `TourkitAiProxy.Infrastructure/TourKit/TkSessionStore.cs`
- Modify: `TourkitAiProxy.Endpoints/SessionAuth.cs`
- Test: `TourkitAiProxy.Tests/Digest/JwtClaimsTests.cs`

**Interfaces:**
- Consumes: `NguoiXem` (Task 1)
- Produces: `JwtClaims.TryGetIsAdmin(string jwt) → bool` · `TkSession.IsAdmin { get; set; }` · `SessionAuth.ReadNguoiXemAsync(HttpContext, TkSessionStore, ChatAssignRepository, CancellationToken) → Task<(Ctx, NguoiXem)?>`

- [ ] **Step 1: Viết test hỏng cho `TryGetIsAdmin`**

Thêm vào `TourkitAiProxy.Tests/Digest/JwtClaimsTests.cs` (chép cách dựng JWT giả từ hàm `MakeJwt` đã có trong file):

```csharp
    [Fact]
    public void Doc_duoc_is_admin_dang_chuoi_True()
        // ERP ghi claim bằng bool.ToString() → "True"/"False", KHÔNG phải "true"/"false".
        // So sánh phân biệt hoa thường là hỏng im lặng: admin thành nhân viên thường.
        => Assert.True(JwtClaims.TryGetIsAdmin(MakeJwt("{\"is_admin\":\"True\"}")));

    [Fact]
    public void Doc_duoc_is_admin_dang_bool()
        => Assert.True(JwtClaims.TryGetIsAdmin(MakeJwt("{\"is_admin\":true}")));

    [Fact]
    public void Thieu_claim_thi_KHONG_phai_admin()
        // Sai theo hướng an toàn: thiếu claim mà đoán là admin thì cả công ty xem được hết.
        => Assert.False(JwtClaims.TryGetIsAdmin(MakeJwt("{\"user_id\":1}")));

    [Fact]
    public void Jwt_rac_thi_KHONG_phai_admin()
        => Assert.False(JwtClaims.TryGetIsAdmin("khong-phai-jwt"));
```

- [ ] **Step 2: Chạy cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter JwtClaimsTests`
Expected: FAIL — `TryGetIsAdmin` chưa tồn tại (lỗi biên dịch).

- [ ] **Step 3: Thêm `TryGetIsAdmin`**

Trong `JwtClaims.cs`, thêm sau `TryGetUserId`:

```csharp
    /// <summary>
    /// Claim <c>is_admin</c> của CRM (AuthService.cs: <c>user.IsFullPermission</c>).
    ///
    /// <para><b>Thiếu claim = KHÔNG phải admin.</b> Sai theo hướng an toàn: đoán nhầm thành
    /// admin thì cả công ty đọc được hộp thư của nhau.</para>
    ///
    /// <para>ERP ghi bằng <c>bool.ToString()</c> nên giá trị là <c>"True"</c>/<c>"False"</c> —
    /// so sánh phân biệt hoa thường là admin âm thầm rớt xuống nhân viên thường.</para>
    /// </summary>
    public static bool TryGetIsAdmin(string jwt)
    {
        try
        {
            var parts = (jwt ?? "").Split('.');
            if (parts.Length < 2) return false;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!doc.RootElement.TryGetProperty("is_admin", out var v)) return false;
            return v.ValueKind switch
            {
                JsonValueKind.True  => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false
            };
        }
        catch { return false; }
    }
```

Đồng thời **sửa chú thích đầu lớp**: câu *"KHÔNG dùng hàm này để quyết định quyền truy cập"* phải nói rõ ranh giới, không thì người sau đọc thấy mâu thuẫn rồi tự nới ra. Thay đoạn `<summary>` bằng:

```csharp
/// <summary>
/// Đọc claim từ JWT TourKit — KHÔNG verify chữ ký.
///
/// <para>An toàn vì chỉ dùng cho JWT do CHÍNH proxy vừa lấy được sau khi login thành công
/// (TkSessionStore giữ), không phải token do client gửi lên.</para>
///
/// <para><b>Ranh giới:</b> tuyệt đối KHÔNG gọi mấy hàm này trên token lấy từ thân yêu cầu hay
/// header của trình duyệt — không kiểm chữ ký thì ai cũng tự khai mình là admin. Quyết định
/// quyền phải đọc từ <c>TkSession.IsAdmin</c> / <c>TkSession.CrmUserId</c>, tức giá trị đã chốt
/// trên máy chủ lúc đăng nhập.</para>
/// </summary>
```

- [ ] **Step 4: Chạy lại**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter JwtClaimsTests`
Expected: PASS.

- [ ] **Step 5: Nạp `IsAdmin` vào phiên**

Trong `TkSessionStore.cs`, cạnh `public int? CrmUserId { get; set; }` (dòng ~33) thêm:

```csharp
    // is_admin của CRM, decode từ claim JWT lúc login/relogin — dùng cho luật xem hộp thư chat.
    public bool IsAdmin { get; set; }
```

Rồi ở **cả ba** chỗ đang gán `CrmUserId` (dòng ~134, ~170, ~379) thêm dòng song song `IsAdmin = JwtClaims.TryGetIsAdmin(login.Token)`. Bỏ sót một chỗ là admin đăng nhập lại thì mất quyền xem, mà không có lỗi nào hiện ra.

- [ ] **Step 6: Thêm `ReadNguoiXemAsync` vào `SessionAuth`**

```csharp
    /// <summary>
    /// Đọc phiên + tính luôn phạm vi xem hộp thư chat.
    ///
    /// <para>Trả <c>null</c> khi phiên hỏng — chỗ gọi trả <see cref="Unauthorized"/> như cũ.</para>
    ///
    /// <para><b>Chưa có dòng cấu hình, hoặc <c>scope_own_only</c> tắt ⇒ xem tất cả</b> — giữ
    /// nguyên hành vi của khách đang chạy. Bật rồi thì admin xem tất cả, còn lại chỉ xem hội
    /// thoại đã giao cho mình.</para>
    /// </summary>
    public static async Task<(Ctx Phien, NguoiXem Xem)?> ReadNguoiXemAsync(
        HttpContext ctx, TkSessionStore sessions, ChatAssignRepository assign,
        CancellationToken ct = default)
    {
        var a = Read(ctx, sessions);
        if (a == null) return null;

        var cauHinh = assign.Configured ? await assign.LayCauHinhAsync(a.TenantId, ct) : null;
        if (cauHinh is null or { ScopeOwnOnly: false })
            return (a, new NguoiXem(null, XemTatCa: true));

        var s = sessions.Get(a.SessionId);
        // Tự lấp CrmUserId nếu phiên cũ chưa có — KHÔNG bắt người dùng đăng nhập lại.
        var maNguoi = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
        return (a, new NguoiXem(maNguoi, XemTatCa: s?.IsAdmin ?? false));
    }
```

- [ ] **Step 7: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add TourkitAiProxy.Infrastructure/TourKit/JwtClaims.cs TourkitAiProxy.Infrastructure/TourKit/TkSessionStore.cs TourkitAiProxy.Endpoints/SessionAuth.cs TourkitAiProxy.Tests/Digest/JwtClaimsTests.cs
git commit -m "feat(chat): phiên biết tài khoản có phải admin không"
```

---

## Task 3: Chặn quyền xem ở một cửa

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs:270` (`GetConversationAsync`), `:341` (`ListConversationsAsync`)
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` (26 chỗ gọi)
- Test: `TourkitAiProxy.Tests/Chat/ChatScopeGuardTests.cs`

**Interfaces:**
- Consumes: `NguoiXem` (Task 1), `SessionAuth.ReadNguoiXemAsync` (Task 2)
- Produces: `GetConversationAsync(string tenant, long id, NguoiXem xem, CancellationToken ct = default, string? nguoiDung = null)` · `ListConversationsAsync(..., NguoiXem xem, ...)`

> ⚠️ **Chạy `codegraph impact GetConversationAsync` TRƯỚC khi sửa** và sửa hết trong một lần — đây là 26 chỗ gọi.

- [ ] **Step 1: Viết guard kiến trúc (hỏng trước)**

Tạo `TourkitAiProxy.Tests/Chat/ChatScopeGuardTests.cs`:

```csharp
using System.Text.RegularExpressions;
using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Mọi endpoint đụng MỘT hội thoại cụ thể đều phải đi qua cửa chung.
///
/// <para>Trước 07/09/2026 ChatInboxEndpoints dài 2.527 dòng và KHÔNG có một dòng kiểm quyền
/// nào: ai đăng nhập được là đọc được hội thoại của mọi đồng nghiệp. Lọc ở danh sách thôi thì
/// vẫn gõ thẳng /conversations/123 là đọc được — nên cửa phải nằm ở nơi lấy hội thoại ra.</para>
/// </summary>
public class ChatScopeGuardTests
{
    private static string Endpoint() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs");
    private static string Repo() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs");

    [Fact]
    public void Cua_chung_bat_buoc_nhan_nguoi_xem()
    {
        // Tham số BẮT BUỘC, không phải tuỳ chọn có mặc định: có mặc định thì quên truyền cũng
        // biên dịch được, và lỗ hổng đi thẳng lên bản chạy thật.
        Assert.Matches(@"GetConversationAsync\(\s*string tenant,\s*long id,\s*NguoiXem ", Repo());
    }

    [Fact]
    public void Danh_sach_hoi_thoai_cung_loc_theo_nguoi_xem()
    {
        var m = Regex.Match(Repo(), "ListConversationsAsync(.{0,3000})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy ListConversationsAsync");
        // Điều kiện phải nằm TRONG câu SQL, không phải lọc trong C# sau khi đã đọc hết về.
        Assert.Contains("assigned_user_id", m.Groups[1].Value);
    }

    [Fact]
    public void Moi_route_mot_hoi_thoai_deu_doc_nguoi_xem_tu_phien()
    {
        var src = Endpoint();
        // Đếm route dạng /conversations/{id...}
        var soRoute = Regex.Matches(src, @"g\.Map(?:Get|Post|Put|Patch|Delete)\(""/conversations/\{id").Count;
        Assert.True(soRoute >= 20, $"Chỉ thấy {soRoute} route — biểu thức đã lạc khỏi cách viết thật");

        var soCua = Regex.Matches(src, @"ReadNguoiXemAsync").Count;
        Assert.True(soCua >= soRoute,
            $"Có {soRoute} route đụng một hội thoại nhưng chỉ {soCua} lượt đọc người xem — " +
            "route nào đó đang bỏ qua cửa chung.");
    }

    [Fact]
    public void Hoi_thoai_CHUA_GAN_khong_hien_voi_nguoi_khong_phai_admin()
    {
        // ĐÂY LÀ QUYẾT ĐỊNH, KHÔNG PHẢI SƠ SUẤT (chủ dự án, 07/09/2026). Bản thiết kế đầu có
        // thêm vế "OR assigned_user_id IS NULL" để nhân viên nhìn thấy hàng chờ mà tự nhận;
        // quyết định cuối là BỎ vế đó. Test này khoá lại để đợt sau không ai "sửa" nó.
        var m = Regex.Match(Repo(), "GetConversationAsync(.{0,1500})", RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy GetConversationAsync");
        var than = m.Groups[1].Value;
        Assert.Contains("@xemTatCa OR v.assigned_user_id = @maNguoi", than);
        Assert.DoesNotContain("assigned_user_id IS NULL", than);
    }

    [Fact]
    public void Khong_duoc_xem_thi_tra_404_chu_khong_403()
    {
        // 403 nghĩa là "có hội thoại này nhưng anh không được xem" — tức xác nhận đúng cái
        // đang giấu. Dò tuần tự theo id là biết công ty có bao nhiêu khách.
        Assert.DoesNotContain("Bạn không được xem hội thoại này", Endpoint());
    }
}
```

- [ ] **Step 2: Chạy cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatScopeGuardTests`
Expected: FAIL cả năm.

- [ ] **Step 3: Đổi chữ ký `GetConversationAsync`**

Trong `ChatRepository.cs:270`, đổi thành:

```csharp
    public async Task<ChatConversation?> GetConversationAsync(string tenant, long id, NguoiXem xem,
        CancellationToken ct = default, string? nguoiDung = null)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.QuerySingleOrDefaultAsync<ChatConversation>("""
            SELECT v.*, ct.display_name, ct.avatar_url, ct.blocked_utc,
                   EXISTS (SELECT 1 FROM chat_conversation_follows f
                            WHERE f.tenant_id = v.tenant_id AND f.conversation_id = v.id
                              AND f.username = @nguoiDung) AS followed
              FROM chat_conversations v
              LEFT JOIN chat_contacts ct
                ON ct.tenant_id = v.tenant_id AND ct.channel = v.channel
               AND ct.external_id = v.contact_external_id
             WHERE v.id = @id AND v.tenant_id = @tenant
               -- Luật xem. Không được xem thì KHÔNG có dòng nào → chỗ gọi trả 404 sẵn có,
               -- không phải viết nhánh 403 mới (403 là xác nhận hội thoại tồn tại).
               AND (@xemTatCa OR v.assigned_user_id = @maNguoi)
            """, new { id, tenant, nguoiDung, xemTatCa = xem.XemTatCa, maNguoi = xem.CrmUserId });
    }
```

⚠️ Tham số `xem` đặt **trước** `ct` và **không có giá trị mặc định** — có mặc định thì quên truyền vẫn biên dịch được, tức mất đúng cái lợi của việc đổi chữ ký.

- [ ] **Step 4: Lọc danh sách theo cùng luật**

Trong `ListConversationsAsync` (`ChatRepository.cs:341`), thêm `NguoiXem xem` vào tham số (sau `tenant`) và thêm vào mệnh đề `WHERE` của câu SQL:

```sql
               AND (@xemTatCa OR v.assigned_user_id = @maNguoi)
```

kèm hai tham số `xemTatCa = xem.XemTatCa, maNguoi = xem.CrmUserId` vào đối tượng tham số.

- [ ] **Step 5: Sửa 26 chỗ gọi trong endpoint**

Ở **mỗi** route `/conversations/{id…}`, đổi hai dòng đầu từ:

```csharp
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
```

thành:

```csharp
            var p = await SessionAuth.ReadNguoiXemAsync(ctx, sessions, assign, ct);
            if (p == null) return SessionAuth.Unauthorized();
            var (a, xem) = p.Value;
```

và mọi lượt gọi `repo.GetConversationAsync(a.TenantId, id, ct)` thành `repo.GetConversationAsync(a.TenantId, id, xem, ct)`. Thêm `ChatAssignRepository assign` vào danh sách tham số của mỗi handler (minimal API tự tiêm).

Ba chỗ **không** đi qua cửa người dùng — dùng `NguoiXem.HeThong`:
- `GetConversationByMessageAsync` (proxy tệp, đã kiểm tenant riêng)
- Đường webhook (`ChatInboundService`) — không có người dùng nào đứng sau
- `ChatOutboxWorker` — chạy nền

- [ ] **Step 6: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS. `ChatScopeGuardTests` xanh cả bốn.

- [ ] **Step 7: Chạy thử tay**

```bash
dotnet run --project TourkitAiProxy.csproj
```
Đăng nhập tài khoản admin → mở hộp thư → thấy đủ hội thoại như trước (vì `chat_assign_settings` chưa có dòng nào).

- [ ] **Step 8: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/ChatScopeGuardTests.cs
git commit -m "feat(chat): chặn quyền xem hội thoại ở một cửa chung"
```

---

## Task 4: Luồng sự kiện kẹp theo người xem

**Files:**
- Modify: `TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs`
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs:514` (`/events`)
- Test: `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs`

**Interfaces:**
- Consumes: `NguoiXem` (Task 1)
- Produces: `ChatEventBus.SubscribeAsync(string tenantId, NguoiXem xem, ...)` · `ChatEvent` thêm trường `int? AssignedUserId`

- [ ] **Step 1: Viết test hỏng**

Thêm vào `TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs` (chép cách dựng bus từ test đã có trong file):

```csharp
    [Fact]
    public async Task Nguoi_bi_gioi_han_khong_nhan_su_kien_cua_hoi_thoai_nguoi_khac()
    {
        // Không kẹp ở đây thì nhân viên vẫn nhận chuông báo tin mới của hội thoại họ bấm vào
        // ra 404: vừa lộ đang có việc xảy ra, vừa trông như app hỏng.
        var bus = new ChatEventBus(null, NullLogger<ChatEventBus>.Instance);
        var nhan = new List<ChatEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("t1", new NguoiXem(7, XemTatCa: false), cts.Token))
                nhan.Add(e);
        });

        bus.Publish(new ChatEvent("t1", 100, "tin-moi", null) { AssignedUserId = 9 });
        bus.Publish(new ChatEvent("t1", 101, "tin-moi", null) { AssignedUserId = 7 });
        await Task.Delay(200);
        cts.Cancel();
        try { await doc; } catch (OperationCanceledException) { }

        Assert.Single(nhan);
        Assert.Equal(101, nhan[0].ConversationId);
    }

    [Fact]
    public async Task Admin_nhan_moi_su_kien_cua_cong_ty_minh()
    {
        var bus = new ChatEventBus(null, NullLogger<ChatEventBus>.Instance);
        var nhan = new List<ChatEvent>();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var doc = Task.Run(async () =>
        {
            await foreach (var e in bus.SubscribeAsync("t1", NguoiXem.HeThong, cts.Token))
                nhan.Add(e);
        });

        bus.Publish(new ChatEvent("t1", 100, "tin-moi", null) { AssignedUserId = 9 });
        bus.Publish(new ChatEvent("t1", 101, "tin-moi", null) { AssignedUserId = null });
        await Task.Delay(200);
        cts.Cancel();
        try { await doc; } catch (OperationCanceledException) { }

        Assert.Equal(2, nhan.Count);
    }
```

- [ ] **Step 2: Chạy cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatEventBusTests`
Expected: FAIL — `SubscribeAsync` chưa nhận `NguoiXem`, `ChatEvent` chưa có `AssignedUserId`.

- [ ] **Step 3: Thêm trường vào `ChatEvent`**

Trong `TourkitAiProxy.Domain/Chat/` (tìm bằng `grep -rn "record ChatEvent"`), thêm:

```csharp
    /// Người phụ trách hội thoại LÚC PHÁT sự kiện — để bus kẹp người nghe. null = chưa ai nhận.
    public int? AssignedUserId { get; init; }
```

- [ ] **Step 4: Kẹp trong bus**

Trong `ChatEventBus.cs`, đổi `SubscribeAsync(string tenantId, ...)` thành `SubscribeAsync(string tenantId, NguoiXem xem, ...)`, lưu `xem` cùng kênh trong `_nghe`, và trước khi đẩy một sự kiện cho người nghe thì kiểm:

```csharp
    /// <summary>
    /// Người nghe này có được thấy sự kiện của hội thoại đó không.
    ///
    /// <para>Đặt TRONG bus, y như chỗ kẹp tenant ngay bên trên và vì đúng lý do đó: lọc ở
    /// endpoint thì một lần quên là rò rỉ.</para>
    /// </summary>
    private static bool DuocThay(NguoiXem xem, ChatEvent e)
        => xem.XemTatCa || (e.AssignedUserId is not null && e.AssignedUserId == xem.CrmUserId);
```

- [ ] **Step 5: Điền `AssignedUserId` ở mọi chỗ `Publish`**

`grep -n "bus.Publish" TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Services/Chat/Inbox/*.cs` — mỗi chỗ đã có hội thoại trong tay thì gán `AssignedUserId = v.AssignedUserId`. Chỗ nào không có thì đọc lại hội thoại bằng `NguoiXem.HeThong` trước khi phát.

- [ ] **Step 6: Sửa endpoint `/events`**

Tại `ChatInboxEndpoints.cs:514`, dùng `ReadNguoiXemAsync` rồi truyền `xem` vào `SubscribeAsync`.

- [ ] **Step 7: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS.

- [ ] **Step 8: Commit**

```bash
git add TourkitAiProxy.Services/Chat/Inbox/ChatEventBus.cs TourkitAiProxy.Domain/Chat TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/ChatEventBusTests.cs
git commit -m "feat(chat): luồng sự kiện kẹp theo người xem"
```

---

## Task 5: Chia xoay vòng

**Files:**
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs`
- Modify: `TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs`
- Test: `TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs` (thêm guard)

**Interfaces:**
- Consumes: `ChatAssignRepository` (Task 1)
- Produces: `ChatAssignRepository.GanXoayVongAsync(string tenant, long conversationId, CancellationToken ct) → Task<int?>` — mã người vừa được gán, `null` khi không gán (chế độ khác, đội trực rỗng, hoặc hội thoại đã có người)

- [ ] **Step 1: Viết guard tính nguyên tử (hỏng trước)**

Thêm vào `ChatAssignSchemaGuardTests.cs`:

```csharp
    private static string Kho() => ChatSchemaGuardTests.DocFile(
        "TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs");

    [Fact]
    public void Xoay_vong_quay_con_tro_va_gan_trong_MOT_cau_lenh()
    {
        // Đọc rồi ghi thì hai tin tới cùng lúc sẽ gán hai người khác nhau, cái sau đè cái
        // trước, và khách nhận hai lời chào.
        var m = System.Text.RegularExpressions.Regex.Match(
            Kho(), "GanXoayVongAsync(.{0,2500})",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.True(m.Success, "Không thấy GanXoayVongAsync");

        var than = m.Groups[1].Value;
        Assert.Contains("WITH", than);                          // CTE ghi dữ liệu
        Assert.Contains("UPDATE chat_assign_settings", than);   // quay con trỏ
        Assert.Contains("UPDATE chat_conversations", than);     // gán người
        // Điều kiện phải nằm TRONG câu UPDATE để CSDL quyết định người thắng.
        Assert.Contains("assigned_user_id IS NULL", than);
    }

    [Fact]
    public void Xoay_vong_chi_chia_cho_doi_truc()
    {
        // Không đọc member_ids thì máy chia hội thoại cho CẢ công ty — khách hỏi tour rơi vào
        // kế toán, ngồi đó không ai trả lời.
        var m = System.Text.RegularExpressions.Regex.Match(
            Kho(), "GanXoayVongAsync(.{0,2500})",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        Assert.Contains("member_ids", m.Groups[1].Value);
    }
```

- [ ] **Step 2: Chạy cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatAssignSchemaGuardTests`
Expected: FAIL — chưa có `GanXoayVongAsync`.

- [ ] **Step 3: Viết `GanXoayVongAsync`**

Thêm vào `ChatAssignRepository.cs`:

```csharp
    /// <summary>
    /// Gán hội thoại cho người kế tiếp trong đội trực — MỘT câu lệnh.
    ///
    /// <para><b>Điều kiện là "chưa ai phụ trách", KHÔNG phải "vừa tạo mới".</b>
    /// <c>GetOrCreateConversationAsync</c> chạy lại ở mỗi tin khách gửi, và hội thoại đóng rồi
    /// khách nhắn lại vẫn phải có người. Điều kiện này bao cả hai, và chạy bao nhiêu lần cũng ra
    /// một kết quả.</para>
    ///
    /// <para><b>Vì sao một câu lệnh:</b> đọc con trỏ rồi mới ghi thì hai tin tới cùng lúc sẽ gán
    /// hai người khác nhau, cái sau đè cái trước, khách nhận hai lời chào. Ở đây lượt thứ hai
    /// chặn trên khoá dòng của <c>chat_assign_settings</c>, và <c>assigned_user_id IS NULL</c>
    /// nằm trong chính câu UPDATE nên CSDL quyết định ai thắng.</para>
    ///
    /// <para>⚠️ Trường hợp hai lượt xảy ra đúng cùng lúc trên CÙNG một hội thoại: lượt thua vẫn
    /// đẩy con trỏ lên một nấc dù không gán được ai — tức một người bị bỏ lượt. Chấp nhận: hậu
    /// quả là lệch một lượt trong vòng, KHÔNG bao giờ là hai người cùng một hội thoại.</para>
    ///
    /// <para>Trả <c>null</c> khi: chế độ không phải xoay vòng · đội trực rỗng · hội thoại đã có
    /// người. Ba trường hợp đều là "không làm gì", chỗ gọi không cần phân biệt.</para>
    /// </summary>
    public async Task<int?> GanXoayVongAsync(
        string tenant, long conversationId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteScalarAsync<int?>("""
            WITH doi AS (
                SELECT unnest(member_ids) AS user_id
                  FROM chat_assign_settings
                 WHERE tenant_id = @tenant
            ),
            chon AS (
                UPDATE chat_assign_settings s
                   SET rotation_last_user_id = COALESCE(
                         (SELECT MIN(user_id) FROM doi
                           WHERE user_id > COALESCE(s.rotation_last_user_id, 0)),
                         (SELECT MIN(user_id) FROM doi)),
                       updated_utc = now()
                 WHERE s.tenant_id = @tenant
                   AND s.mode = 2
                   AND EXISTS (SELECT 1 FROM doi)
                   AND EXISTS (SELECT 1 FROM chat_conversations v
                                WHERE v.id = @id AND v.tenant_id = @tenant
                                  AND v.assigned_user_id IS NULL)
                RETURNING s.rotation_last_user_id AS user_id
            )
            UPDATE chat_conversations v
               SET assigned_user_id = chon.user_id,
                   status = CASE WHEN v.status = 2 THEN v.status ELSE 1 END
              FROM chon
             WHERE v.id = @id AND v.tenant_id = @tenant AND v.assigned_user_id IS NULL
            RETURNING v.assigned_user_id
            """, new { tenant, id = conversationId });
    }
```

- [ ] **Step 4: Chạy lại guard**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatAssignSchemaGuardTests`
Expected: PASS (7/7).

- [ ] **Step 5: Móc vào đường nhận tin**

Trong `ChatInboundService.cs`, ngay sau lượt gọi `GetOrCreateConversationAsync` (grep để tìm), thêm:

```csharp
        // Chia xoay vòng. Đặt SAU khi có hội thoại và TRƯỚC khi ghi tin: gán xong mới ghi thì
        // sự kiện "tin mới" bắn ra đã mang đúng người phụ trách, nên bus kẹp đúng ngay lượt đầu.
        // Hàm tự bỏ qua khi chế độ khác, đội trực rỗng, hoặc hội thoại đã có người.
        if (_assign.Configured)
        {
            var maNguoiNhan = await _assign.GanXoayVongAsync(hoiThoai.TenantId, hoiThoai.Id, ct);
            if (maNguoiNhan is not null)
            {
                // ChatConversation là CLASS có property đặt được, không phải record — gán thẳng,
                // đừng dùng `with { }` (không biên dịch được).
                //
                // AssignedUsername để NGUYÊN (null): đội trực chỉ lưu mã người, không lưu tên
                // đăng nhập. Giao diện hiện tên bằng cách tra mã trong danh sách nhân viên ERP
                // mà nó vốn đã nạp — xem Task 8.
                hoiThoai.AssignedUserId = maNguoiNhan;
                await _repo.AppendAuditAsync(hoiThoai.TenantId, hoiThoai.Id, "he-thong",
                    "xoay-vong", $"{{\"cho\":{maNguoiNhan}}}", ct);
            }
        }
```

Thêm `ChatAssignRepository _assign` vào hàm dựng của `ChatInboundService`.

- [ ] **Step 6: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatAssignRepository.cs TourkitAiProxy.Services/Chat/Inbox/ChatInboundService.cs TourkitAiProxy.Tests/Chat/ChatAssignSchemaGuardTests.cs
git commit -m "feat(chat): chia hội thoại xoay vòng cho đội trực"
```

---

## Task 6: Giao việc có kiểm, và tự nhận khi trả lời

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs:982` (`/assign`), `:813` (`/send`)
- Modify: `TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs:555` (`ClaimConversationAsync`)
- Test: `TourkitAiProxy.Tests/Chat/ChatClaimGuardTests.cs` (thêm)

**Interfaces:**
- Consumes: `ChatAssignRepository.LayCauHinhAsync` (Task 1)
- Produces: `ClaimConversationAsync(string tenant, long id, string username, int? userId, CancellationToken ct = default) → Task<int>` · `AssignAsync(string tenant, long id, string? username, int? userId, CancellationToken ct = default) → Task`

- [ ] **Step 1: Viết test hỏng**

Thêm vào `ChatClaimGuardTests.cs`:

```csharp
    [Fact]
    public void Nhan_viec_ghi_CA_HAI_cot()
    {
        // Chỉ ghi tên đăng nhập thì luật xem (so theo assigned_user_id) không thấy hội thoại
        // vừa nhận — người nhận việc xong là mất luôn hội thoại khỏi màn hình.
        var m = Regex.Match(Repo(), "ClaimConversationAsync(.{0,900})", RegexOptions.Singleline);
        Assert.Contains("assigned_username = @username", m.Groups[1].Value);
        Assert.Contains("assigned_user_id = @userId", m.Groups[1].Value);
    }

    [Fact]
    public void Tham_so_ma_nguoi_phai_la_int_CO_THE_NULL()
    {
        // NULL = chưa ai phụ trách. Khai `int` trần thì Dapper/C# đổi NULL thành 0, KHÔNG báo
        // lỗi, và hai thứ chết theo:
        //   • vòng quay ngừng hẳn — điều kiện gán là `assigned_user_id IS NULL`, ghi 0 thì nó
        //     không bao giờ đúng nữa;
        //   • nhả việc không trả hội thoại về hàng chờ — nó thành "của" người mã 0 không tồn tại.
        var repo = Repo();
        Assert.Contains("int? userId", repo);
        Assert.DoesNotContain("int userId,", repo);
    }

    [Fact]
    public void Giao_viec_kiem_nguoi_nhan_co_trong_doi_truc()
    {
        // Endpoint cũ nhận BẤT KỲ chuỗi tên đăng nhập nào: gõ sai một ký tự là hội thoại gán
        // vào hư không, không ai thấy nó nữa và không có lỗi nào hiện ra.
        Assert.Contains("MemberIds.Contains", Endpoint());
    }
```

- [ ] **Step 2: Chạy cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatClaimGuardTests`
Expected: FAIL cả ba test mới.

- [ ] **Step 3: `ClaimConversationAsync` ghi cả hai cột**

```csharp
    public async Task<int> ClaimConversationAsync(string tenant, long id, string username,
        int? userId, CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        return await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET assigned_username = @username,
                   assigned_user_id  = @userId,
                   status = CASE WHEN status = 2 THEN status ELSE 1 END
             WHERE id = @id AND tenant_id = @tenant
               AND (assigned_username IS NULL OR assigned_username = @username)
            """, new { id, tenant, username, userId });
    }
```

- [ ] **Step 3b: `AssignAsync` cũng ghi cả hai cột**

`ChatRepository.cs:625` — đường **giao cho người khác** và **nhả việc**, tách khỏi đường nhận
nguyên tử ở trên. Nó cũng phải điền mã người, không thì giao xong người nhận không thấy hội thoại:

```csharp
    public async Task AssignAsync(string tenant, long id, string? username, int? userId,
        CancellationToken ct = default)
    {
        await using var c = await _db.OpenAsync(ct);
        // Giao việc thì đẩy trạng thái sang "đang xử lý" — trừ khi đã đóng, vì gán người cho việc
        // đã đóng không có nghĩa mở lại nó.
        //
        // Nhả việc (username = null) xoá CẢ HAI cột: còn sót mã người thì hội thoại vẫn "của"
        // người vừa nhả, và nó không bao giờ quay lại hàng chờ.
        await c.ExecuteAsync("""
            UPDATE chat_conversations
               SET assigned_username = @username,
                   assigned_user_id  = @userId,
                   status = CASE WHEN status = 2 THEN status ELSE 1 END
             WHERE id = @id AND tenant_id = @tenant
            """, new { id, tenant, username, userId });
    }
```

- [ ] **Step 3c: Nhánh NHẬN VIỆC CHO CHÍNH MÌNH cũng phải đổi**

`ChatInboxEndpoints.cs:995` đang rẽ nhánh bằng `if (body?.Username is null)`. Sau khi `AssignReq`
đổi sang `int? UserId` thì thuộc tính `Username` không còn — quên chỗ này là **không biên dịch
được**, và sửa cho qua bằng `body?.UserId is null` thì **sai nghĩa**: yêu cầu "nhả việc"
(`{"userId":null}`) sẽ bị hiểu thành "nhận việc cho tôi".

Rẽ nhánh theo **thân yêu cầu có hay không**, không theo giá trị bên trong:

```csharp
            // KHÔNG có thân yêu cầu = NHẬN VIỆC cho chính mình. Tên và mã lấy từ PHIÊN, không
            // lấy từ thân: để client tự khai thì ai cũng gán việc cho người khác được.
            if (body is null)
            {
                var maToi = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
                var soDong = await repo.ClaimConversationAsync(a.TenantId, id, a.Username, maToi, ct);
                if (soDong == 0)
                {
                    // 200 im lặng là kiểu hỏng tệ nhất: giao diện người thua vẫn hiện "của tôi",
                    // rồi hai người cùng trả lời một khách.
                    var dangGiu = await repo.AssigneeOfAsync(a.TenantId, id, ct);
                    return Results.Json(new { error = $"{dangGiu} đang xử lý hội thoại này", assignedTo = dangGiu },
                        statusCode: StatusCodes.Status409Conflict);
                }
                await repo.AppendAuditAsync(a.TenantId, id, a.Username, "nhan-viec", null, ct);
                // GIỮ NGUYÊN phần đọc lại + cảnh báo mà Task 4 đã thêm — đừng thay bằng `maToi`.
                // Đọc lại là bằng chứng lệnh ghi ĐÃ vào CSDL; dùng thẳng biến vừa tính là tin
                // rằng nó vào, mà không có gì bảo đảm.
                var saoKhiNhan = await repo.GetConversationAsync(a.TenantId, id, NguoiXem.HeThong, ct);
                if (saoKhiNhan is null)
                    log.LogWarning("[chat/assign] đọc lại hội thoại {H} sau khi NHẬN VIỆC ra null " +
                        "— sự kiện phát đi mang AssignedUserId=null", id);
                bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null) { AssignedUserId = saoKhiNhan?.AssignedUserId });
                return Results.Json(new { ok = true, assignedTo = a.Username, assignedUserId = maToi }, Web);
            }
```

⚠️ Giao diện phải gửi kèm: nút "Nhận chăm sóc" gọi `/assign` **không có thân**, còn ô chọn người
gửi `{"userId": …}`. Task 8 đã viết đúng vậy.

- [ ] **Step 4: `/assign` kiểm đội trực**

Trong nhánh "có tên = chuyển việc cho người đó" của `/assign` (`ChatInboxEndpoints.cs:1013`), trước khi gọi `repo.AssignAsync`:

```csharp
            // Thân yêu cầu nay mang MÃ người, không mang tên đăng nhập: gõ sai một ký tự tên
            // là hội thoại gán vào hư không — không ai thấy nó nữa (luật xem so theo mã), và
            // không có lỗi nào hiện ra.
            var ch = await assign.LayCauHinhAsync(a.TenantId, ct);
            var ma = body!.UserId;
            if (ma is not null && ch?.MemberIds.Contains(ma.Value) != true)
                return Results.Json(new { error = "Người này không có trong đội trực chat" },
                    statusCode: StatusCodes.Status400BadRequest);

            // ma = null nghĩa là NHẢ VIỆC — trả hội thoại về hàng chờ.
            await repo.AssignAsync(a.TenantId, id, username: null, userId: ma, ct);
            await repo.AppendAuditAsync(a.TenantId, id, a.Username,
                ma is null ? "nha-viec" : "chuyen-viec",
                ma is null ? null : $"{{\"cho\":{ma}}}", ct);
            // GIỮ NGUYÊN phần đọc lại + cảnh báo của Task 4 (xem chú thích ở nhánh nhận việc).
            var saoKhiGiao = await repo.GetConversationAsync(a.TenantId, id, NguoiXem.HeThong, ct);
            if (saoKhiGiao is null)
                log.LogWarning("[chat/assign] đọc lại hội thoại {H} sau khi CHUYỂN/NHẢ VIỆC ra null " +
                    "— sự kiện phát đi mang AssignedUserId=null", id);
            bus.Publish(new(a.TenantId, id, "doi-hoi-thoai", null) { AssignedUserId = saoKhiGiao?.AssignedUserId });
            return Results.Json(new { ok = true, assignedUserId = ma }, Web);
```

Đổi bản ghi yêu cầu ở cuối file từ `AssignReq(string? Username)` thành:

```csharp
    /// Thân RỖNG = nhận việc cho chính mình (tên + mã lấy từ PHIÊN, không tin thân yêu cầu —
    /// để client tự khai thì ai cũng gán việc cho người khác được).
    /// Có thân, UserId = null → NHẢ việc. Có thân, UserId = số → giao cho người đó.
    public record AssignReq(int? UserId);
```

- [ ] **Step 5: Tự nhận khi trả lời**

Trong `/conversations/{id:long}/send` (`:813`), sau khi gửi thành công:

```csharp
            // "Ai trả lời trước thì thành người phụ trách". Dùng lại đường nhận việc nguyên tử
            // nên hai người cùng gõ vẫn chỉ một người thành chủ.
            //
            // ⚠️ Trên thực tế chỉ admin chạm được nhánh này: theo luật xem, hội thoại chưa gán
            // không hiện với nhân viên thường nên họ không mở ra để trả lời được.
            // `v` là hội thoại đã đọc ở đầu handler (ChatInboxEndpoints.cs:827).
            var ch = assign.Configured ? await assign.LayCauHinhAsync(a.TenantId, ct) : null;
            if (ch?.AutoAssignOnReply == true && v.AssignedUserId is null)
            {
                var maNguoi = await sessions.EnsureCrmUserIdAsync(a.SessionId, ct);
                if (await repo.ClaimConversationAsync(a.TenantId, id, a.Username, maNguoi, ct) > 0)
                    await repo.AppendAuditAsync(a.TenantId, id, a.Username, "tu-nhan-khi-tra-loi", null, ct);
            }
```

- [ ] **Step 6: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add TourkitAiProxy.Infrastructure/Chat/Inbox/ChatRepository.cs TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs TourkitAiProxy.Tests/Chat/ChatClaimGuardTests.cs
git commit -m "feat(chat): giao việc có kiểm đội trực, tự nhận khi trả lời"
```

---

## Task 7: API cấu hình phân công

**Files:**
- Modify: `TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs` (thêm 4 route + `OwnedPaths`)
- Test: `TourkitAiProxy.Tests/Chat/ChatFeatureFlagCoverageTests.cs` (tự xanh lại sau khi thêm `OwnedPaths`)

**Interfaces:**
- Consumes: `ChatAssignRepository` (Task 1), `SessionAuth.CanConfigSystemAsync` (đã có), `TourKitApiClient.GetAsync` (đã có)
- Produces: `GET /api/v1/chat/assign-settings` · `PUT /api/v1/chat/assign-settings`

- [ ] **Step 1: Thêm tiền tố vào `OwnedPaths`**

Trong `ChatInboxEndpoints.cs:43`, thêm một dòng vào mảng:

```csharp
        "/api/v1/chat/assign-settings",
```

- [ ] **Step 2: Chạy test coverage cho hỏng**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj --filter ChatFeatureFlagCoverageTests`
Expected: PASS (mảng đã khai trước, route thêm sau — thứ tự này giữ test luôn xanh).

- [ ] **Step 3: Viết 2 route**

Một cặp đọc/ghi cho cả cấu hình lẫn đội trực — chúng nằm chung một dòng CSDL, tách API ra hai
cặp là bắt giao diện tự giữ cho hai lượt ghi khớp nhau.

```csharp
        // ── Cấu hình phân công ───────────────────────────────────────────────
        // ĐỌC thì ai cũng được: giao diện cần biết chế độ và đội trực để dựng ô chọn người phụ
        // trách, mà giấu hai thứ đó đi không bảo vệ gì cả. GHI mới cần quyền Cấu hình hệ thống.
        g.MapGet("/assign-settings", async (HttpContext ctx, TkSessionStore sessions,
            ChatAssignRepository assign, TourKitApiClient api, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!assign.Configured) return NotConfigured();

            var ch = await assign.LayCauHinhAsync(a.TenantId, ct);
            var s = sessions.Get(a.SessionId);

            // Tên nhân viên lấy từ ERP, KHÔNG lưu trong CSDL chat: lưu là nhân đôi chỗ phải sửa
            // khi ai đó đổi tên. Best-effort — upstream hỏng thì trả danh sách rỗng, hộp thư vẫn
            // chạy, chỉ mất ô chọn người.
            var nhanVien = new List<object>();
            try
            {
                var r = await api.GetAsync(s!.Jwt, "/api/ai/reference", ct);
                if (r.TryGetProperty("lookups", out var lk)
                    && lk.TryGetProperty("sellers", out var sellers)
                    && sellers.ValueKind == JsonValueKind.Array)
                    foreach (var it in sellers.EnumerateArray())
                    {
                        // ⚠️ Khoá số có thể là "value" HOẶC "id" tuỳ enum — DealEndpoints.BuildDealLookups
                        // đã phải xử cả hai. Gọi thẳng GetProperty("id") là NÉM khi payload dùng "value",
                        // và cả lượt gọi rơi vào catch bên dưới → danh sách nhân viên rỗng, im lặng.
                        var ma = it.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Number
                                     ? v.GetInt32()
                                 : it.TryGetProperty("id", out var i2) && i2.ValueKind == JsonValueKind.Number
                                     ? i2.GetInt32() : 0;
                        var ten = it.TryGetProperty("name", out var n) ? n.GetString() : null;
                        if (ma > 0 && !string.IsNullOrWhiteSpace(ten))
                            nhanVien.Add(new { id = ma, name = ten });
                    }
            }
            catch { /* để rỗng */ }

            // Chưa cấu hình → mặc định "thủ công, không kẹp quyền" = đúng hành vi hôm nay.
            return Results.Json(new
            {
                mode              = ch?.Mode ?? CheDoPhanCong.ThuCong,
                scopeOwnOnly      = ch?.ScopeOwnOnly ?? false,
                autoAssignOnReply = ch?.AutoAssignOnReply ?? false,
                memberIds         = ch?.MemberIds ?? Array.Empty<int>(),
                isAdmin           = s?.IsAdmin ?? false,
                staffs            = nhanVien
            }, Web);
        });

        g.MapPut("/assign-settings", async (AssignSettingsReq body, HttpContext ctx,
            TkSessionStore sessions, ChatAssignRepository assign, CancellationToken ct) =>
        {
            var a = SessionAuth.Read(ctx, sessions);
            if (a == null) return SessionAuth.Unauthorized();
            if (!assign.Configured) return NotConfigured();
            if (!await SessionAuth.CanConfigSystemAsync(a.SessionId, sessions, ct))
                return SessionAuth.ForbiddenConfigSystem();
            if (body.Mode is not (CheDoPhanCong.ThuCong or CheDoPhanCong.XoayVong))
                return Results.BadRequest(new { error = "Chế độ không hợp lệ" });

            var doiTruc = (body.MemberIds ?? Array.Empty<int>()).Where(x => x > 0).Distinct().ToArray();

            // Bật xoay vòng mà đội trực rỗng thì MỌI hội thoại rơi về hàng chờ, trông y hệt chế
            // độ thủ công — không ai đoán được nguyên nhân. Chặn ngay tại chỗ lưu.
            if (body.Mode == CheDoPhanCong.XoayVong && doiTruc.Length == 0)
                return Results.BadRequest(new
                { error = "Chưa chọn ai vào đội trực chat — bật xoay vòng lúc này sẽ không gán được cho ai." });

            await assign.LuuCauHinhAsync(a.TenantId, body.Mode, body.ScopeOwnOnly,
                body.AutoAssignOnReply, doiTruc, ct);
            return Results.Json(new { ok = true, count = doiTruc.Length }, Web);
        });
```

Khai bản ghi yêu cầu cạnh các `record` khác cuối file:

```csharp
    public record AssignSettingsReq(short Mode, bool ScopeOwnOnly, bool AutoAssignOnReply,
                                    int[]? MemberIds);
```

- [ ] **Step 4: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Thử tay bằng curl**

```bash
curl -s -H "X-Session-Id: <sid>" http://localhost:5080/api/v1/chat/assign-settings
```
Expected: `mode:1`, `memberIds:[]`, `isAdmin:true`, và `staffs` là danh sách nhân viên của công
ty. `staffs` rỗng nghĩa là lượt gọi ERP hỏng — xem log, đừng bỏ qua: rỗng thì màn hình cấu hình
không tick chọn được ai.

- [ ] **Step 6: Commit**

```bash
git add TourkitAiProxy.Endpoints/ChatInboxEndpoints.cs
git commit -m "feat(chat): API cấu hình phân công và đội trực"
```

---

## Task 8: Giao diện hộp thư

**Files:**
- Modify: `wwwroot/pages/chat-inbox.jsx:951-952` (khối Thông tin), `:2513` (dòng tiêu đề), `:2521-2523` (nút nhận việc)

**Interfaces:**
- Consumes: `GET /api/v1/chat/assign-settings` (Task 7), `POST /conversations/{id}/assign` (Task 6)

- [ ] **Step 1: Nạp đội trực + cấu hình khi mở hộp thư**

Cạnh chỗ nạp `channels` (grep `'/api/v1/chat/channels'` trong file), thêm:

```jsx
  const [phanCong, setPhanCong] = _uC({ mode: 1, scopeOwnOnly: false, isAdmin: false,
                                        memberIds: [], staffs: [] });

  // Đội trực = giao của memberIds với danh sách nhân viên ERP. Máy chủ trả MÃ, tên thì tra ở
  // đây — không lưu tên trong CSDL chat để khỏi phải đồng bộ khi ai đó đổi tên.
  const doiTruc = (phanCong.staffs || [])
    .filter(nv => (phanCong.memberIds || []).includes(nv.id));

  // Nạp MỘT lần lúc mở hộp thư — cấu hình đổi rất thưa (chỉ khi quản trị sửa), hỏi lại mỗi lần
  // chọn hội thoại là một lượt gọi thừa cho mỗi cú bấm.
  _uE(() => {
    (async () => {
      try {
        const r = await authedFetch('/api/v1/chat/assign-settings');
        setPhanCong(await r.json());
      } catch { /* lỗi thì để rỗng — hộp thư vẫn chạy, chỉ mất ô chọn người */ }
    })();
  }, []);
```

- [ ] **Step 2: Hàm giao việc cho người khác**

Cạnh hàm `nhanViec` (dòng ~2108):

```jsx
  // Giao cho người khác. Gửi MÃ người, không gửi tên: tên là thứ đổi được và gõ được sai, mã
  // thì không. Chuỗi rỗng ở ô chọn = nhả việc → gửi userId null.
  const giaoCho = async (maNguoi) => {
    const r = await authedFetch('/api/v1/chat/conversations/' + chon + '/assign', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ userId: maNguoi ? Number(maNguoi) : null })
    });
    if (!r.ok) { const e = await r.json().catch(() => ({})); alert(e.error || 'Giao việc không xong'); return; }
    taiLaiChiTiet();
  };
```

(`taiLaiChiTiet` là hàm tải lại chi tiết hội thoại đã có trong file — grep `setChiTiet(` để lấy đúng tên.)

- [ ] **Step 3: Thay khối nút ở thanh tiêu đề**

Thay đoạn ở dòng 2521-2523:

```jsx
                    {/* Ô chọn người phụ trách đứng TRƯỚC nút nhận: thao tác hay dùng nhất của
                        quản lý là giao việc, còn nút nhận là của người trực. */}
                    {doiTruc.length > 0 && (
                      <select className="ci-chon-phutrach"
                              value={v.assignedUserId || ''}
                              onChange={e => giaoCho(e.target.value)}
                              title="Người phụ trách">
                        <option value="">— chưa ai phụ trách —</option>
                        {doiTruc.map(nv => (
                          <option key={nv.id} value={nv.id}>{nv.name}</option>
                        ))}
                      </select>
                    )}

                    {/* Ẩn với nhân viên thường khi đang kẹp quyền: hội thoại họ mở được thì đã
                        là của họ rồi, nút đó bấm không ra gì — để nguyên là trông như lỗi. */}
                    {(phanCong.isAdmin || !phanCong.scopeOwnOnly) && (
                      <button className={'ci-nut' + (v.assignedUsername ? ' da-nhan' : ' nhan')}
                              onClick={nhanViec}>
                        {v.assignedUsername ? 'Đã nhận chăm sóc' : 'Nhận chăm sóc'}
                      </button>
                    )}
```

- [ ] **Step 4: Thêm màu cho nút**

Trong file CSS của hộp thư (grep `.ci-nut` để tìm), thêm:

```css
/* Đỏ = việc chưa ai làm, cần bấm. Xanh = đã có người, chỉ để biết. */
.ci-nut.nhan     { background: #dc2626; color: #fff; border-color: #dc2626; }
.ci-nut.da-nhan  { background: #16a34a; color: #fff; border-color: #16a34a; }
.ci-chon-phutrach { height: 30px; max-width: 170px; border-radius: 8px; padding: 0 8px;
                    border: 1px solid var(--vien, #e2e8f0); font-size: 13px; }
```

- [ ] **Step 5: Hiện tên đầy đủ thay tên đăng nhập**

**BA chỗ, không phải hai** — kế hoạch bản đầu bỏ sót chỗ thứ ba:

| Dòng | Chỗ |
|---|---|
| ~952 | khối Thông tin bên phải |
| ~2513 | dòng gộp trạng thái ở thanh tiêu đề |
| **~2413** | **huy hiệu người phụ trách trên từng dòng danh sách hội thoại** (`c.assignedUsername`) — dùng biến `c`, không phải `v` |

Cả ba thay `assignedUsername` bằng:

```jsx
{(phanCong.staffs || []).find(nv => nv.id === v.assignedUserId)?.name
  || v.assignedUsername || 'chưa ai nhận'}
```

⚠️ Lùi về `assignedUsername` là **cố ý**, không phải thừa: hội thoại gán từ trước 07/09/2026 chỉ
có tên đăng nhập, và người tự nhận việc cũng ghi tên đăng nhập từ phiên. Bỏ vế đó là những dòng
ấy hiện "chưa ai nhận" dù đang có người phụ trách.

- [ ] **Step 6: Thử tay**

```bash
dotnet run --project TourkitAiProxy.csproj
```
Mở `http://localhost:5080` → Hộp thư chat → chọn một hội thoại. Kiểm:
1. Nút hiện chữ **"Nhận chăm sóc"** nền đỏ; bấm xong thành **"Đã nhận chăm sóc"** nền xanh.
2. Ô chọn người phụ trách chỉ hiện khi đội trực có người (chưa cấu hình thì không hiện — đúng).
3. Cột Phụ trách hiện tên đầy đủ.

- [ ] **Step 7: Commit**

```bash
git add wwwroot/pages/chat-inbox.jsx wwwroot/
git commit -m "feat(chat): ô chọn người phụ trách và nhãn nút nhận chăm sóc"
```

---

## Task 9: Màn hình cấu hình phân công

**Files:**
- Create: `wwwroot/pages/chat-assign-settings.jsx`
- Modify: nơi khai báo trang (grep `chat-inbox.jsx` trong `wwwroot/index.html` và file định tuyến để chép đúng cách đăng ký một trang mới)

**Interfaces:**
- Consumes: 4 route ở Task 7

- [ ] **Step 1: Viết trang**

Tạo `wwwroot/pages/chat-assign-settings.jsx` — chép khung trang (import, cách lấy `authedFetch`, cách khai `window.TrangX`) từ một trang cấu hình đã có, ví dụ trang Cấu hình trợ lý. Nội dung:

```jsx
// Cấu hình chia hội thoại cho nhân viên.
//
// HAI chế độ, cố ý. Ảnh mẫu của sản phẩm khác có bốn (thêm "theo nhóm" và "tuỳ chọn tài
// khoản") — chưa làm vì khái niệm Nhóm chưa tồn tại trong hộp thư chat, thêm sau không phải
// đập đi làm lại.
const CHE_DO = [
  { id: 1, ten: 'Phân công thủ công',
    mo: 'Máy không gán. Người mở được hội thoại thì tự nhận hoặc giao cho người khác.' },
  { id: 2, ten: 'Chia xoay vòng',
    mo: 'Hội thoại nào chưa có người phụ trách thì gán lần lượt cho đội trực.' }
];
```

Ba khối. Cả danh sách nhân viên lẫn đội trực hiện tại đều tới trong **một lượt gọi**
`GET /api/v1/chat/assign-settings` (`staffs` + `memberIds`) — không gõ tay gì cả.

```jsx
  return (
    <div className="tk-trang">
      <h2>Chia hội thoại cho nhân viên</h2>

      {/* 1 · Chế độ */}
      <div className="the-nhom">
        {CHE_DO.map(c => (
          <button key={c.id}
                  className={'the-che-do' + (mode === c.id ? ' on' : '')}
                  onClick={() => setMode(c.id)}>
            <b>{c.ten}</b><span>{c.mo}</span>
          </button>
        ))}
      </div>

      {/* 2 · Đội trực. Hiện cả khi đang ở chế độ thủ công: ô chọn người phụ trách trên hộp
          thư cũng đổ ra danh sách này, nên nó không phải chuyện riêng của xoay vòng. */}
      <h3>Đội trực chat</h3>
      <p className="mo-ta">Vừa là vòng quay chia việc, vừa là danh sách hiện ở ô chọn người
        phụ trách trên đầu khung chat.</p>
      {(staffs || []).map(nv => (
        <label key={nv.id} className="dong-nv">
          <input type="checkbox" checked={memberIds.includes(nv.id)}
                 onChange={e => bat(nv.id, e.target.checked)} />
          <span>{nv.name}</span>
        </label>
      ))}
      {(staffs || []).length === 0 && (
        <div className="canh-bao">
          Không lấy được danh sách nhân viên từ CRM. Không phải công ty chưa có ai — là lượt gọi
          hỏng; tải lại trang, còn lỗi thì xem log máy chủ.
        </div>
      )}

      {/* 3 · Hai công tắc */}
      <h3>Quyền xem</h3>
      <label className="dong-cong-tac">
        <input type="checkbox" checked={scopeOwnOnly}
               onChange={e => setScopeOwnOnly(e.target.checked)} />
        <span>Nhân viên chỉ xem được hội thoại đã giao cho mình</span>
      </label>
      <label className="dong-cong-tac">
        <input type="checkbox" checked={autoAssignOnReply}
               onChange={e => setAutoAssignOnReply(e.target.checked)} />
        <span>Ai trả lời tin đầu tiên thì thành người phụ trách</span>
      </label>
```

Hai hàm phụ:

```jsx
  // Tick/bỏ tick một người — đội trực chỉ là một danh sách số, thêm hoặc bớt là xong.
  const bat = (id, on) => setMemberIds(ds =>
    on ? (ds.includes(id) ? ds : [...ds, id]) : ds.filter(x => x !== id));
```

Cảnh báo bắt buộc, hiện **ngay trên nút Lưu**, không phải sau khi lưu:

```jsx
{mode === 2 && memberIds.length === 0 && (
  <div className="canh-bao">
    Chưa chọn ai vào đội trực. Bật xoay vòng lúc này thì mọi hội thoại rơi về hàng chờ,
    trông y hệt chế độ thủ công — không ai đoán được nguyên nhân.
  </div>
)}

{scopeOwnOnly && (
  <div className="canh-bao">
    Bật mục này thì nhân viên <b>chỉ còn thấy hội thoại đã giao cho mình</b>. Hội thoại chưa
    giao cho ai sẽ không hiện với họ — chỉ quản trị viên nhìn thấy và giao xuống.
  </div>
)}
```

Nút Lưu và hàm lưu. **Một lượt ghi duy nhất** — chế độ và đội trực nằm chung một dòng CSDL, nên
không có khoảnh khắc nào chế độ đã là xoay vòng mà đội trực còn rỗng:

```jsx
      <button className="tk-nut chinh" disabled={dangLuu} onClick={luu}>
        {dangLuu ? 'Đang lưu…' : 'Lưu cài đặt'}
      </button>
    </div>
  );

  async function luu() {
    setDangLuu(true);
    try {
      const r = await authedFetch('/api/v1/chat/assign-settings', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ mode, scopeOwnOnly, autoAssignOnReply, memberIds })
      });
      if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || 'Lưu không xong');
      alert('Đã lưu');
    } catch (e) {
      alert(e.message);
    } finally {
      setDangLuu(false);
    }
  }
```

- [ ] **Step 2: Đăng ký trang**

Thêm thẻ `<script type="text/babel" src="pages/chat-assign-settings.jsx">` vào `wwwroot/index.html` cạnh dòng của `chat-inbox.jsx`, và thêm mục vào menu điều hướng cạnh Cấu hình trợ lý.

- [ ] **Step 3: Thử tay**

```bash
dotnet run --project TourkitAiProxy.csproj
```
1. Mở trang cấu hình → chọn **Chia xoay vòng** khi chưa tick ai → thấy cảnh báo, bấm Lưu nhận lỗi 400 với đúng câu đó.
2. Tick hai nhân viên → Lưu → thành công.
3. Bật `scopeOwnOnly` → thấy cảnh báo về hậu quả.

- [ ] **Step 4: Commit**

```bash
git add wwwroot/pages/chat-assign-settings.jsx wwwroot/index.html
git commit -m "feat(chat): màn hình cấu hình phân công hội thoại"
```

---

## Task 10: Cờ tính năng, tài liệu, CHANGELOG

**Files:**
- Modify: `TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs`
- Modify: `appsettings.example.json`
- Modify: `docs/features/chat-inbox.md`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Thêm cờ**

Trong `FeatureFlags.cs`:

```csharp
    /// <summary>
    /// Phân công hội thoại và phân quyền xem trong hộp thư chat.
    ///
    /// <para>PHỤ THUỘC <see cref="Chat"/>: không có hộp thư thì không có gì để phân công.</para>
    ///
    /// <para><b>Cờ này chỉ ẩn/hiện giao diện.</b> Luật xem thật nằm ở
    /// <c>chat_assign_settings.scope_own_only</c> theo TỪNG CÔNG TY — chưa có dòng thì mọi người
    /// xem tất cả, y như trước. Tắt cờ mà công ty đã bật kẹp quyền thì luật vẫn chạy: bảo vệ dữ
    /// liệu không được phụ thuộc vào một cờ khai trong file cấu hình máy chủ.</para>
    /// </summary>
    public static bool ChatAssign(IConfiguration cfg)
        => Chat(cfg) && cfg.GetValue("Features:ChatAssign", false);
```

- [ ] **Step 2: Khai vào template cấu hình**

Trong `appsettings.example.json`, mục `Features`, thêm `"ChatAssign": false`.

- [ ] **Step 3: Chạy toàn bộ test**

Run: `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj`
Expected: PASS. `AppSettingsModelCoverageTests` có thể đòi khai cờ mới — đó chính là lý do có Step 2.

- [ ] **Step 4: Viết tài liệu tính năng**

Thêm mục vào `docs/features/chat-inbox.md`, sau mục "Bốn thao tác hộp thư":

```markdown
### Phân công và quyền xem

Hai chế độ: **Phân công thủ công** (máy không gán, người giao cho nhau) và **Chia xoay vòng**
(hội thoại chưa có người thì gán lần lượt cho đội trực).

⚠️ **Luật xem: admin xem tất cả, còn lại chỉ xem hội thoại đã giao cho mình.** Hội thoại **chưa
giao cho ai KHÔNG hiện** với nhân viên thường — đây là quyết định của chủ dự án 07/09/2026, có
test khoá lại, đừng "sửa" nó. Hệ quả là chế độ thủ công trên thực tế là **admin giao xuống**,
không phải nhân viên tự bốc việc.

⚠️ **Chặn ở chữ ký hàm, không bằng kỷ luật.** `GetConversationAsync` nhận `NguoiXem` bắt buộc, và
26 endpoint hội thoại đều đi qua nó — endpoint mới quên truyền thì **lỗi biên dịch**, không phải
lỗ hổng phát hiện sau sáu tháng. Không được xem thì hàm trả `null` → 404. **Không trả 403**: 403
xác nhận hội thoại tồn tại, dò tuần tự theo id là biết công ty có bao nhiêu khách.

⚠️ **Luồng sự kiện kẹp trong bus**, cùng chỗ kẹp tenant và cùng lý do. Lọc ở endpoint thì một lần
quên là nhân viên nhận chuông báo của hội thoại họ bấm vào ra 404.

⚠️ **Con trỏ xoay vòng lưu MÃ NGƯỜI, không lưu vị trí.** Lưu vị trí thì thêm hoặc bớt một người
là cả vòng lệch, im lặng: một người nhận gấp đôi, một người không nhận cái nào.

⚠️ **Chưa có dòng `chat_assign_settings` = giữ nguyên hành vi cũ.** Đường lùi cho khách đang chạy.
```

- [ ] **Step 5: Viết CHANGELOG**

Thêm vào `CHANGELOG.md`, viết cho người dùng cuối — **không** tên bảng, hàm, cột:

```markdown
### Hộp thư chat — giao việc cho nhân viên

- Mỗi cuộc trò chuyện nay có **người phụ trách**. Chọn người ngay trên đầu khung chat.
- Hai cách chia việc: **giao tay** hoặc **chia lần lượt** cho đội trực chat — cuộc nào chưa có
  người thì tự chia cho người kế tiếp.
- Bật được chế độ **nhân viên chỉ xem cuộc trò chuyện của mình**; quản trị viên vẫn xem tất cả.
- Nút "Nhận việc" đổi thành **"Nhận chăm sóc"**, và hiện tên đầy đủ của người phụ trách thay cho
  tên đăng nhập.
```

- [ ] **Step 6: Commit**

```bash
git add TourkitAiProxy.Services/Bootstrap/FeatureFlags.cs appsettings.example.json docs/features/chat-inbox.md CHANGELOG.md
git commit -m "docs(chat): tài liệu và cờ tính năng cho phân công hội thoại"
```

---

## Kiểm cuối trước khi gộp

- [ ] `dotnet test TourkitAiProxy.Tests/TourkitAiProxy.Tests.csproj` — toàn bộ xanh, chạy dưới 1 giây.
- [ ] Chạy thử với `chat_assign_settings` **rỗng**: hộp thư y hệt hôm nay, không ai mất hội thoại.
- [ ] Bật `scopeOwnOnly`, đăng nhập tài khoản **không phải admin**: chỉ thấy hội thoại của mình; gõ thẳng URL một hội thoại của người khác → **404**, không phải 403.
- [ ] Bật xoay vòng với hai người trong đội: hai hội thoại mới rơi vào hai người khác nhau.
- [ ] Bỏ tick một người khỏi đội trực: người đó không nhận lượt nữa, và vòng quay của những
      người còn lại **không lệch** (con trỏ lưu mã người, không lưu vị trí).
