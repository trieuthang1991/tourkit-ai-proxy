// Services/Reviews/Agents/ReviewPrompt.cs
using System.Text.Json;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.Reviews.Agents;

/// <summary>
/// Shared prompt + JSON schema + tolerant parser cho 2 agent (Json + Native).
/// SYSTEM_PROMPT, ranking criteria, output JSON shape duy nhất 1 nguồn — đổi ngành = đổi ở đây.
/// </summary>
public static class ReviewPrompt
{
    /// <summary>
    /// System prompt cho JSON-prompt agent: chỉ thị KHÔNG markdown, ngữ cảnh ngành du lịch.
    /// Native-tool agent KHÔNG cần phần "Output ONLY raw JSON" vì schema tự enforce, dùng <see cref="SystemForNativeTool"/>.
    /// </summary>
    public const string SystemForJsonPrompt =
        "Bạn là CHUYÊN GIA CSKH & sales du lịch nhiều năm kinh nghiệm, đang review nhanh 1 khách cho đồng nghiệp. " +
        "Giọng văn: TỰ NHIÊN, sắc sảo, có nhận định con người — như đang trao đổi miệng, KHÔNG máy móc, KHÔNG liệt kê " +
        "công thức, KHÔNG lộ quy tắc/nhãn nội bộ (không nhắc 'BƯỚC', 'theo luật', 'cap'). Nói thẳng điều quan trọng nhất trước. " +
        "Output ONLY raw JSON theo schema, KHÔNG markdown fences, KHÔNG giải thích, KHÔNG thinking. " +
        "Bắt đầu output bằng dấu `{` ngay. KHÔNG suy diễn ngoài dữ liệu. " +
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Đề xuất hành động phải gắn với dữ liệu thực tế.";

    /// <summary>
    /// System prompt cho native-tool agent: schema do tool enforce, chỉ cần hướng dẫn cách viết.
    /// Bỏ phần "Output JSON" vì AI phải gọi tool submit_customer_review thay vì in JSON ra text.
    /// </summary>
    public const string SystemForNativeTool =
        "Bạn là CHUYÊN GIA CSKH & sales du lịch nhiều năm kinh nghiệm, review nhanh 1 khách cho đồng nghiệp. " +
        "Giọng văn TỰ NHIÊN, sắc sảo, có nhận định con người — KHÔNG máy móc/liệt kê công thức, KHÔNG lộ quy tắc " +
        "hay nhãn nội bộ (không nhắc 'BƯỚC', 'theo luật', 'cap'). Nói thẳng điều quan trọng nhất trước. " +
        "Phân tích dữ liệu khách hàng → gọi tool submit_customer_review với kết quả. " +
        "KHÔNG suy diễn ngoài dữ liệu; thiếu data thì ghi 'Chưa đủ dữ liệu để đánh giá'. " +
        "Tiếng Việt tự nhiên, ngắn gọn, thực dụng. Mọi đề xuất phải gắn với data cụ thể " +
        "(vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây'). " +
        "BẮT BUỘC gọi submit_customer_review đúng 1 lần — KHÔNG trả text giải thích thêm.";

    /// <summary>
    /// Build user prompt chứa data + ranking criteria + JSON schema (dùng cho JSON-prompt agent).
    /// </summary>
    public static string BuildUserPromptForJson(Customer c)
    {
        var dataJson = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });

        return $@"NHIỆM VỤ: Phân tích khách hàng tour dưới đây và trả về JSON review cho Sale/CSKH dùng quyết định chăm sóc.

DỮ LIỆU KHÁCH HÀNG:
{dataJson}

QUY TẮC:
1. CHỈ dựa trên data trên, KHÔNG bịa thông tin không có
2. Nếu thiếu data ở mục nào, ghi 'Chưa đủ dữ liệu để đánh giá'
3. Văn phong cụ thể, hành động được, tiếng Việt tự nhiên
4. Mọi đề xuất phải gắn với data cụ thể (vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây')

═══ TUYỆT ĐỐI KHÔNG DÙNG TÊN FIELD TIẾNG ANH TRONG OUTPUT ═══
Sale/CSKH đọc review này — họ KHÔNG BIẾT tên field code. PHẢI dùng thuật ngữ tự nhiên tiếng Việt.

Bảng dịch BẮT BUỘC (áp dụng cho MỌI text output — rankReason, portrait, strengths, concerns, alert.message, preferences, actionNow.reason, action30Days, productSuggestions, summaryLine):
  • totalSpent / TotalSpent → 'tổng chi tiêu' (VD: 'tổng chi tiêu 25 triệu')
  • aov / Aov → 'chi tiêu trung bình mỗi tour' hoặc 'giá tour trung bình'
  • totalTours / TotalTours → 'số tour đã đi' hoặc 'số lần đi tour'
  • lastPurchaseDate / LastPurchaseDaysAgo → 'ngày mua gần nhất' hoặc 'lần mua cuối'
  • lastCareDate / LastCareDaysAgo → 'ngày chăm sóc gần nhất' hoặc 'lần liên hệ cuối'
  • avgDaysBetweenOrders → 'khoảng cách giữa các lần mua'
  • cancelCount → 'số lần hủy tour'
  • complaintCount → 'số lần khiếu nại'
  • careInteractions → 'lần được chăm sóc'
  • purchases / Purchases → 'đơn hàng' hoặc 'lịch sử mua tour'
  • amount / Amount → 'giá trị' hoặc 'số tiền'
  • pax / Pax → 'số khách' hoặc 'số người đi'
  • nights / Nights → 'số đêm' hoặc 'số ngày lưu trú'
  • destination / Destination → 'điểm đến' hoặc 'nơi đi tour'
  • channel / Channel → 'kênh mua' hoặc 'nguồn khách'
  • segment / Segment → 'phân khúc khách' hoặc 'nhóm khách'
  • phone / Phone / phoneNumber → 'số điện thoại'
  • email / Email → 'email'
  • gender / Gender → 'giới tính'
  • age / Age → 'tuổi'
  • location / Location / address → 'địa chỉ' hoặc 'vùng miền'
  • source / Source → 'nguồn' hoặc 'kênh tiếp cận'
  • note / Note → 'ghi chú' hoặc 'nhu cầu ban đầu'
  • rankName → 'hạng' (VD 'hạng A', 'hạng VIP')
  • createdAt → 'ngày tạo hồ sơ'
  • statusName → 'trạng thái'
  • customerTypeName → 'loại khách'
  • customerSourceName → 'nguồn khách'
  • groupName → 'nhóm khách'

Ví dụ SAI (không được dùng):
  ✗ 'Chưa hoàn tất bất kỳ giao dịch nào (totalSpent=0)'
  ✗ '8/10 đơn hàng có Amount=0đ'
  ✗ 'aov thấp 688k'
  ✗ 'lastPurchaseDaysAgo = null'

Ví dụ ĐÚNG (viết như vậy):
  ✓ 'Chưa từng mua tour nào'
  ✓ '8/10 đơn hàng có giá trị 0đ'
  ✓ 'Chi tiêu trung bình mỗi tour thấp (688 nghìn)'
  ✓ 'Chưa có lịch sử mua tour'

Số tiền: 'X triệu', 'X nghìn', 'X đồng' (KHÔNG '688000', '20000000'). Ngày: 'X ngày trước', 'Y tháng trước'.

{RankingCriteria}

OUTPUT JSON (KHÔNG markdown fences, KHÔNG giải thích thêm):
{{
  ""rank"": ""A|B|C|D"",
  ""rankReason"": ""1 câu ngắn"",
  ""alert"": {{ ""level"": ""high|medium|none"", ""message"": ""trống nếu không có vấn đề"" }},
  ""portrait"": ""Chân dung KH 1-2 câu (tuổi, vùng, tần suất đi tour, phong cách)"",
  ""strengths"": [""điểm tích cực 1"", ""điểm tích cực 2""],
  ""concerns"": [""điểm cần lưu ý 1"", ""điểm cần lưu ý 2""],
  ""preferences"": ""Sở thích du lịch rút từ lịch sử (vd: thích tour biển cao cấp, đi cùng gia đình 4-5 người, AOV 20tr)"",
  ""actionNow"": {{ ""task"": ""Việc cần làm hôm nay, cụ thể"", ""reason"": ""Lý do dựa data nào"" }},
  ""action30Days"": [""ý tưởng chăm sóc 1"", ""ý tưởng 2"", ""ý tưởng 3""],
  ""productSuggestions"": [""tour/dịch vụ phù hợp 1"", ""tour 2""],
  ""summaryLine"": ""1 dòng ≤80 ký tự: Hạng + tình trạng + hành động ưu tiên""
}}

Bắt đầu trả JSON ngay:";
    }

    /// <summary>
    /// Build user prompt cho native-tool agent: data + ranking criteria, KHÔNG có JSON shape (schema lo).
    /// </summary>
    public static string BuildUserPromptForNative(Customer c)
    {
        var dataJson = JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true });
        return $@"NHIỆM VỤ: Phân tích khách hàng tour dưới đây và GỌI TOOL submit_customer_review với kết quả.

DỮ LIỆU KHÁCH HÀNG:
{dataJson}

QUY TẮC:
1. CHỈ dựa trên data trên, KHÔNG bịa thông tin không có
2. Nếu thiếu data ở mục nào, ghi 'Chưa đủ dữ liệu để đánh giá'
3. Văn phong cụ thể, hành động được, tiếng Việt tự nhiên
4. Mọi đề xuất phải gắn với data cụ thể (vd 'KH mua Phú Quốc 4 lần → gợi ý tour biển miền Tây')

═══ TUYỆT ĐỐI KHÔNG DÙNG TÊN FIELD TIẾNG ANH TRONG OUTPUT ═══
Sale/CSKH đọc review này — họ KHÔNG BIẾT tên field code. PHẢI dùng thuật ngữ tự nhiên tiếng Việt.

Bảng dịch BẮT BUỘC (áp dụng cho MỌI text output — rankReason, portrait, strengths, concerns, alert.message, preferences, actionNow.reason, action30Days, productSuggestions, summaryLine):
  • totalSpent / TotalSpent → 'tổng chi tiêu' (VD: 'tổng chi tiêu 25 triệu')
  • aov / Aov → 'chi tiêu trung bình mỗi tour' hoặc 'giá tour trung bình'
  • totalTours / TotalTours → 'số tour đã đi' hoặc 'số lần đi tour'
  • lastPurchaseDate / LastPurchaseDaysAgo → 'ngày mua gần nhất' hoặc 'lần mua cuối'
  • lastCareDate / LastCareDaysAgo → 'ngày chăm sóc gần nhất' hoặc 'lần liên hệ cuối'
  • avgDaysBetweenOrders → 'khoảng cách giữa các lần mua'
  • cancelCount → 'số lần hủy tour'
  • complaintCount → 'số lần khiếu nại'
  • careInteractions → 'lần được chăm sóc'
  • purchases / Purchases → 'đơn hàng' hoặc 'lịch sử mua tour'
  • amount / Amount → 'giá trị' hoặc 'số tiền'
  • pax / Pax → 'số khách' hoặc 'số người đi'
  • nights / Nights → 'số đêm' hoặc 'số ngày lưu trú'
  • destination / Destination → 'điểm đến' hoặc 'nơi đi tour'
  • channel / Channel → 'kênh mua' hoặc 'nguồn khách'
  • segment / Segment → 'phân khúc khách' hoặc 'nhóm khách'
  • phone / Phone / phoneNumber → 'số điện thoại'
  • email / Email → 'email'
  • gender / Gender → 'giới tính'
  • age / Age → 'tuổi'
  • location / Location / address → 'địa chỉ' hoặc 'vùng miền'
  • source / Source → 'nguồn' hoặc 'kênh tiếp cận'
  • note / Note → 'ghi chú' hoặc 'nhu cầu ban đầu'
  • rankName → 'hạng' (VD 'hạng A', 'hạng VIP')
  • createdAt → 'ngày tạo hồ sơ'
  • statusName → 'trạng thái'
  • customerTypeName → 'loại khách'
  • customerSourceName → 'nguồn khách'
  • groupName → 'nhóm khách'

Ví dụ SAI (không được dùng):
  ✗ 'Chưa hoàn tất bất kỳ giao dịch nào (totalSpent=0)'
  ✗ '8/10 đơn hàng có Amount=0đ'
  ✗ 'aov thấp 688k'
  ✗ 'lastPurchaseDaysAgo = null'

Ví dụ ĐÚNG (viết như vậy):
  ✓ 'Chưa từng mua tour nào'
  ✓ '8/10 đơn hàng có giá trị 0đ'
  ✓ 'Chi tiêu trung bình mỗi tour thấp (688 nghìn)'
  ✓ 'Chưa có lịch sử mua tour'

Số tiền: 'X triệu', 'X nghìn', 'X đồng' (KHÔNG '688000', '20000000'). Ngày: 'X ngày trước', 'Y tháng trước'.

{RankingCriteria}

Gọi submit_customer_review NGAY với 11 field. KHÔNG trả text giải thích.";
    }

    /// <summary>Schema JSON cho terminal tool submit_customer_review (native-tool agent).</summary>
    public static JsonElement BuildSubmitReviewToolSchema()
    {
        // Anthropic tools format: {name, description, input_schema}
        var schema = new
        {
            name = "submit_customer_review",
            description = "Nộp kết quả phân tích khách hàng. Gọi DUY NHẤT 1 lần khi đã có đủ 11 field.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    rank = new
                    {
                        type = "string",
                        @enum = new[] { "A", "B", "C", "D" },
                        description = "Hạng KH: A=VIP trung thành, B=tốt có tiềm năng, C=bình thường, D=rủi ro rời bỏ"
                    },
                    rankReason = new { type = "string", description = "1 câu ngắn giải thích vì sao hạng đó" },
                    alert = new
                    {
                        type = "object",
                        properties = new
                        {
                            level = new
                            {
                                type = "string",
                                @enum = new[] { "high", "medium", "none" },
                                description = "Mức cảnh báo cho Sale/CSKH"
                            },
                            message = new { type = "string", description = "Mô tả vấn đề (rỗng nếu level=none)" }
                        },
                        required = new[] { "level" }
                    },
                    portrait = new { type = "string", description = "Chân dung KH 1-2 câu (tuổi, vùng, phong cách)" },
                    strengths = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 điểm tích cực của KH"
                    },
                    concerns = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 điểm cần lưu ý / rủi ro"
                    },
                    preferences = new { type = "string", description = "Sở thích du lịch rút từ lịch sử" },
                    actionNow = new
                    {
                        type = "object",
                        properties = new
                        {
                            task = new { type = "string", description = "Việc cần làm hôm nay, cụ thể" },
                            reason = new { type = "string", description = "Lý do dựa data nào" }
                        },
                        required = new[] { "task", "reason" }
                    },
                    action30Days = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "2-4 ý tưởng chăm sóc trong 30 ngày tới"
                    },
                    productSuggestions = new
                    {
                        type = "array",
                        items = new { type = "string" },
                        description = "1-3 tour/dịch vụ phù hợp"
                    },
                    summaryLine = new
                    {
                        type = "string",
                        description = "1 dòng ≤80 ký tự: Hạng + tình trạng + hành động ưu tiên"
                    }
                },
                required = new[]
                {
                    "rank", "rankReason", "alert", "portrait", "strengths", "concerns",
                    "preferences", "actionNow", "action30Days", "productSuggestions", "summaryLine"
                }
            }
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private const string RankingCriteria = @"═══ QUY TẮC XẾP HẠNG (theo THỨ TỰ ưu tiên — dừng ở luật ĐẦU TIÊN khớp) ═══

BƯỚC 1 — Kiểm tra cấp cứu (Hạng D):
  • Đã hủy tour ≥ 2 lần HOẶC đã khiếu nại ≥ 2 lần chưa giải quyết → D (nguy cơ khiếu nại/hủy)
  • Đã mua ít nhất 1 tour NHƯNG lần mua cuối cách đây hơn 180 ngày → D (nguy cơ rời bỏ)

BƯỚC 2 — Khách chưa mua:
  • Chưa có tour nào → C (khách mới cần kích hoạt)

BƯỚC 3 — VIP (Hạng A):
  • Đã đi ≥ 5 tour VÀ giá tour trung bình ≥ 20 triệu VÀ mua trong vòng 180 ngày qua VÀ chưa khiếu nại lần nào
    → A (VIP thân thiết)

BƯỚC 4 — Tiềm năng (Hạng B):
  • Đã đi 2-4 tour → B (khách tiềm năng)
  • Đã đi ≥ 5 tour nhưng thiếu 1 điều kiện A (VD giá tour thấp, hoặc im lặng) → B (khách tiềm năng nâng cấp)

BƯỚC 5 — Mặc định → C (khách bình thường)

BƯỚC 6 — GIỚI HẠN CHẤT LƯỢNG DỮ LIỆU (áp SAU cùng, có thể HẠ rank A/B xuống C):
  Kiểm tra 4 lỗi hồ sơ:
    (a) Giá tour trung bình dưới 500 nghìn (quá thấp so với tour thật)
    (b) Trên 50% đơn hàng có giá trị 0đ (đơn không hợp lệ)
    (c) Số điện thoại có dấu hiệu giả:
        - ⚠️ SĐT DI ĐỘNG VN CHUẨN = 10 chữ số bắt đầu 03/05/07/08/09 (VD '0982385108', '0912345678',
          '0387654321') → HỢP LỆ TUYỆT ĐỐI, KHÔNG BAO GIỜ tính là lỗi (c), KHÔNG ghi 'cần xác minh SĐT'.
          SĐT bàn VN 10-11 số bắt đầu 02 cũng hợp lệ.
        - CHỈ tính lỗi khi: chuỗi lặp bất thường (VD '1111111', '9999999', >6 số giống nhau liền);
          ngắn quá (dưới 9 chữ số) hoặc dài quá (trên 15 chữ số); toàn số ngẫu nhiên KHÔNG có prefix hợp lệ.
        - CHẤP NHẬN thêm: '+1234567890' (US), '00841234567890' (VN quốc tế), '84987654321', '+8412345678'
    (d) Email không hợp lệ (thiếu '@', domain không có '.', hoặc chuỗi ngẫu nhiên 'asdsad'/'test123'/'a')

  NẾU rank vừa tính (A hoặc B) MÀ có ≥ 2 lỗi trong (a)(b)(c)(d)
    → DOWNGRADE về C, và nói tự nhiên trong rankReason rằng hồ sơ còn điểm chưa đáng tin nên tạm để hạng C.

═══ CÁCH VIẾT rankReason (QUAN TRỌNG — GIỌNG CHUYÊN GIA, KHÔNG MÁY MÓC) ═══
  • Các 'BƯỚC 1..6' ở trên CHỈ là logic nội bộ để bạn CHỌN hạng — TUYỆT ĐỐI KHÔNG nhắc 'BƯỚC 1/2/3',
    'theo luật', 'DOWNGRADE', 'cap', 'quy tắc' trong output. Người đọc là Sale/CSKH, họ chỉ cần NHẬN ĐỊNH.
  • Viết rankReason như một chuyên gia CSKH lâu năm nói nhanh với đồng nghiệp: 1-2 câu, tự nhiên, sắc,
    nêu đúng lý do cốt lõi từ dữ liệu — KHÔNG liệt kê khô khan, KHÔNG mở đầu bằng cụm cứng lặp lại.
    VD tốt: 'Khách VIP thực thụ — đi 5 tour, chi trung bình 22 triệu, vừa đặt tháng trước, phải giữ chặt.'
    VD tốt: 'Tạm để hạng C: hồ sơ mới toanh, đơn duy nhất lại 0đ nên chưa đủ tin để xếp cao hơn.'
    VD XẤU (cấm): 'BƯỚC 3 — VIP: ...', 'Giới hạn hạng C do dữ liệu đáng ngờ: ...'
  • Dấu hiệu chất lượng dữ liệu đưa vào concerns/alert.message — cũng viết tự nhiên, tiếng Việt.
  • alert.level:
    - high: có khiếu nại chưa xử, hoặc khách VIP/B tier mà im lặng trên 90 ngày, hoặc dữ liệu lỗi nặng khiến tụt xuống C
    - medium: im lặng 30-90 ngày, hoặc dữ liệu có 1 lỗi
    - none: bình thường";

    // ─── JSON parser tolerant (dùng chung 2 agent — JsonAgent từ raw text, NativeAgent từ tool input) ───
    public record ParsedReview(
        string? Rank,
        string? RankReason,
        ReviewAlert? Alert,
        string? Portrait,
        List<string>? Strengths,
        List<string>? Concerns,
        string? Preferences,
        ReviewAction? ActionNow,
        List<string>? Action30Days,
        List<string>? ProductSuggestions,
        string? SummaryLine
    );

    /// <summary>
    /// Parse raw text từ AI (có thể có markdown fences, prose lẫn JSON).
    /// Gỡ fences → trim đến balanced top-level object → parse case-insensitive.
    /// </summary>
    public static ParsedReview ParseRawText(string raw)
    {
        var cleaned = raw.Replace("```json", "").Replace("```", "").Trim();
        var start = cleaned.IndexOf('{');
        if (start < 0) throw new InvalidOperationException("Output không có JSON object");
        cleaned = cleaned.Substring(start);

        int depth = 0, end = -1; bool inStr = false, esc = false;
        for (int i = 0; i < cleaned.Length; i++)
        {
            var ch = cleaned[i];
            if (esc) { esc = false; continue; }
            if (ch == '\\') { esc = true; continue; }
            if (ch == '"') { inStr = !inStr; continue; }
            if (inStr) continue;
            if (ch == '{') depth++;
            else if (ch == '}') { depth--; if (depth == 0) { end = i; break; } }
        }
        if (end > 0) cleaned = cleaned.Substring(0, end + 1);

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            return ParseElement(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Parse review JSON failed: {ex.Message}. Raw đầu: {raw[..Math.Min(raw.Length, 200)]}", ex);
        }
    }

    /// <summary>
    /// Parse JsonElement (vd input của tool_use block từ native agent — đã structured rồi).
    /// </summary>
    public static ParsedReview ParseElement(JsonElement root)
    {
        return new ParsedReview(
            Rank:               GetString(root, "rank")?.Trim().ToUpperInvariant().FirstOrDefault().ToString(),
            RankReason:         GetString(root, "rankReason") ?? GetString(root, "rank_reason"),
            Alert:              ParseAlert(root),
            Portrait:           GetString(root, "portrait"),
            Strengths:          GetStringList(root, "strengths"),
            Concerns:           GetStringList(root, "concerns"),
            Preferences:        GetString(root, "preferences"),
            ActionNow:          ParseAction(root, "actionNow") ?? ParseAction(root, "action_now"),
            Action30Days:       GetStringList(root, "action30Days") ?? GetStringList(root, "action_30days"),
            ProductSuggestions: GetStringList(root, "productSuggestions") ?? GetStringList(root, "product_suggestions"),
            SummaryLine:        GetString(root, "summaryLine") ?? GetString(root, "summary_line")
        );
    }

    /// <summary>
    /// Compose CustomerReview từ ParsedReview + meta — dùng chung 2 agent để tránh drift schema.
    /// </summary>
    public static CustomerReview Compose(
        ParsedReview parsed, Customer customer, string fingerprint,
        string aiProvider, string aiModel, int tokensIn, int tokensOut)
    {
        return new CustomerReview(
            Id:                  Guid.NewGuid().ToString("N"),
            CustomerId:          customer.Id,
            Rank:                parsed.Rank ?? "C",
            RankReason:          parsed.RankReason ?? "",
            Alert:               parsed.Alert ?? new ReviewAlert("none", null),
            Portrait:            parsed.Portrait ?? "",
            Strengths:           parsed.Strengths ?? new(),
            Concerns:            parsed.Concerns ?? new(),
            Preferences:         parsed.Preferences ?? "",
            ActionNow:           parsed.ActionNow ?? new ReviewAction("Liên hệ thăm hỏi", "Chưa đủ dữ liệu để đề xuất cụ thể"),
            Action30Days:        parsed.Action30Days ?? new(),
            ProductSuggestions:  parsed.ProductSuggestions ?? new(),
            SummaryLine:         parsed.SummaryLine ?? BuildFallbackSummary(parsed.Rank ?? "C", customer),
            DataFingerprint:     fingerprint,
            AiModel:             aiModel,
            AiProvider:          aiProvider,
            TokensIn:            tokensIn,
            TokensOut:           tokensOut,
            GeneratedAt:         DateTime.UtcNow.ToString("o"),
            Feedback:            null
        );
    }

    // ─── private helpers ────────────────────────────────────────────────────
    private static bool TryGet(JsonElement el, string name, out JsonElement value)
    {
        value = default;
        if (el.ValueKind != JsonValueKind.Object) return false;
        foreach (var p in el.EnumerateObject())
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            { value = p.Value; return true; }
        return false;
    }

    private static string? GetString(JsonElement el, string name)
        => TryGet(el, name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static List<string>? GetStringList(JsonElement el, string name)
    {
        if (!TryGet(el, name, out var p) || p.ValueKind != JsonValueKind.Array) return null;
        var list = new List<string>();
        foreach (var item in p.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!);
            }
        return list;
    }

    private static ReviewAlert? ParseAlert(JsonElement root)
    {
        if (!TryGet(root, "alert", out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var level = GetString(a, "level") ?? "none";
        var msg = GetString(a, "message");
        return new ReviewAlert(level.ToLowerInvariant(), string.IsNullOrWhiteSpace(msg) ? null : msg);
    }

    private static ReviewAction? ParseAction(JsonElement root, string name)
    {
        if (!TryGet(root, name, out var a) || a.ValueKind != JsonValueKind.Object) return null;
        var task = GetString(a, "task") ?? "";
        var reason = GetString(a, "reason") ?? "";
        if (string.IsNullOrWhiteSpace(task)) return null;
        return new ReviewAction(task, reason);
    }

    private static string BuildFallbackSummary(string rank, Customer c)
        => $"Hạng {rank} · {c.Metrics.TotalTours} tour · {c.Metrics.LastPurchaseDaysAgo?.ToString() ?? "?"}d trước";
}
