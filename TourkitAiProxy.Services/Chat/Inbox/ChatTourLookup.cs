using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;
using TourkitAiProxy.Domain.Models;
using TourkitAiProxy.Infrastructure.Cache;
using TourkitAiProxy.Infrastructure.TourKit;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Providers;
using TourkitAiProxy.Services.Quota;
using TourkitAiProxy.Shared.Json;

namespace TourkitAiProxy.Services.Chat.Inbox;

/// <summary>
/// Tra dữ liệu tour thật để trợ lý hộp thư chat tư vấn khách — thay vì hẹn "để em kiểm rồi báo lại".
///
/// <para>Đây là bản dựng lại lối của <c>WidgetChatCrmService</c> (widget trên website khách hàng),
/// vì widget đã giải đúng bài toán này: nói chuyện với người ngoài công ty, tra kho CRM, giới hạn
/// bằng danh sách trắng. Không phát minh cơ chế mới — dùng lại <see cref="ChatTools"/> và
/// <see cref="TourKitApiClient"/> y như widget và Trợ lý số liệu đang dùng.</para>
///
/// <para><b>Mọi thất bại đều trả <c>null</c>, không ném.</b> Chỗ gọi hiểu null là "không có dữ
/// liệu" và trả lời đúng như trước khi có tính năng này. Đó là tính chất quan trọng nhất của lớp
/// này: bật nó lên không tạo ra ca nào tệ hơn hiện trạng.</para>
/// </summary>
public class ChatTourLookup
{
    private readonly ProviderRegistry _providers;
    private readonly AiModelRegistry _models;
    private readonly TourKitApiClient _api;
    private readonly TkSessionStore _sessions;
    private readonly TenantServiceAccountStore _serviceAccounts;
    private readonly ChatCache _cache;
    private readonly AiCallContext _aiCtx;
    private readonly IConfiguration _cfg;
    private readonly ILogger<ChatTourLookup> _log;

    public ChatTourLookup(ProviderRegistry providers, AiModelRegistry models, TourKitApiClient api,
        TkSessionStore sessions, TenantServiceAccountStore serviceAccounts, ChatCache cache,
        AiCallContext aiCtx, IConfiguration cfg, ILogger<ChatTourLookup> log)
    {
        _providers = providers; _models = models; _api = api; _sessions = sessions;
        _serviceAccounts = serviceAccounts; _cache = cache; _aiCtx = aiCtx; _cfg = cfg; _log = log;
    }

    /// <summary>
    /// Tool trợ lý chat được phép gọi — <b>hẹp hơn widget một cách có chủ đích</b>.
    ///
    /// <para>Widget mặc định cho ba tool: <c>tours</c>, <c>list_markets</c>, <c>booking_tickets</c>.
    /// Ở đây bỏ hai cái sau. Khác biệt nằm ở người đối diện: widget nói chuyện với khách vãng lai
    /// hỏi về chính họ, còn hộp thư chat thì người nhắn <b>có danh tính và có hội thoại riêng</b> —
    /// để lọt <c>booking_tickets</c> là mở đường cho khách A hỏi trúng cơ hội bán hàng của khách B.
    /// <c>list_markets</c> bỏ vì khách không cần biết công ty chia thị trường thế nào.</para>
    ///
    /// <para>Từ 18/09/2026 dùng <c>tour_catalog</c> + <c>tour_detail</c> thay cho <c>tours</c> và
    /// <c>departures</c>. Hai đường mới trả về object dựng RIÊNG cho việc tư vấn — không mang tên
    /// khách đã đặt, nhân viên bán, doanh thu hay hoa hồng, và có <c>pricePerSlot</c> mà
    /// <c>/api/ai/tours</c> thiếu. Nguồn cũ mặc định đóng thì an toàn hơn hẳn lọc trường ở đầu
    /// nhận: trường upstream thêm mới sau này không tự lọt qua.</para>
    ///
    /// <para>Đổi danh sách này là nới quyền đọc của một thứ nói thẳng với khách — đọc
    /// <c>ChatTourLookupGuardTests</c> trước khi thêm tên nào vào đây.</para>
    /// </summary>
    public static readonly string[] AllowedTools = { "tour_catalog", "tour_detail" };

    /// <summary>Bảng bơm vào lời nhắc không được dài quá, kẻo nuốt hết chỗ của chính đoạn hội thoại.</summary>
    private const int MaxDataChars = 3500;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Giữ NGUYÊN chữ tiếng Việt thay vì escape thành <c>ố</c>.
    ///
    /// <para>Mặc định của System.Text.Json escape mọi ký tự ngoài ASCII, làm mỗi chữ có dấu phình
    /// từ 1 lên 6 ký tự. Bảng bơm vào lời nhắc bị tính dài gấp ba, nên hàm cắt vứt đi phần lớn số
    /// tour mà đáng lẽ vừa chỗ — khách hỏi tour có thật mà bot bảo chưa thấy.</para>
    ///
    /// <para>Tên "Unsafe" nói về ngữ cảnh HTML (không escape <c>&lt;</c>, <c>&amp;</c>). Chuỗi này
    /// đi vào lời nhắc gửi cho mô hình, không đi vào trang web nào, nên không dính rủi ro đó.</para>
    /// </summary>
    private static readonly JsonSerializerOptions GiuTiengViet =
        new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="ToolTitle">Tên người-đọc-được của nguồn, để ghi vào lời nhắc.</param>
    /// <param name="Json">Gói dữ liệu đã rút gọn.</param>
    public record KetQua(string ToolTitle, string Json);

    /// <summary>
    /// Tra dữ liệu cho một câu hỏi của khách. Trả <c>null</c> nghĩa là "không có gì để bơm" — chỗ
    /// gọi cứ trả lời như cũ.
    /// </summary>
    /// <param name="nhanVienSessionId">Phiên TourKit của nhân viên đang bấm Gợi ý. <c>null</c> ở
    /// đường bot tự trả lời (không có ai online).</param>
    /// <param name="nguoiPhuTrach">Tên đăng nhập người phụ trách hội thoại. Chỉ dùng khi công ty
    /// bật "theo quyền nhân viên" VÀ đây là lượt bot tự trả lời.</param>
    public async Task<KetQua?> TraAsync(string tenantId, ChatBotSettings cfgBot, string cauHoi,
        string? nhanVienSessionId, string? nguoiPhuTrach, CancellationToken ct)
    {
        // Công tắc DUY NHẤT (cờ máy chủ Features:ChatTourLookup đã bỏ 18/09/2026). Chặn NGAY ĐÂY,
        // trước lượt AI chọn tool: công ty chưa bật thì không một lượt gọi AI nào phát ra.
        if (!cfgBot.TourLookup) return null;
        if (string.IsNullOrWhiteSpace(cauHoi)) return null;

        var (tool, thamSo) = await ChonToolAsync(tenantId, cauHoi, ct);
        if (tool is null) return null;

        // Chặn LẦN HAI, sau khi AI đã chọn. Lần một là lọc danh mục trước khi gửi (AI không thấy
        // tool cấm); lần này canh trường hợp AI bịa ra tên tool ngoài danh sách. Một lớp ở prompt
        // không phải một lớp bảo vệ — prompt thì khuyên, còn đây thì chặn.
        if (!AllowedTools.Contains(tool.Name, StringComparer.OrdinalIgnoreCase))
        {
            _log.LogWarning("[chat/tour] tenant={T} AI chọn tool NGOÀI danh sách: {Tool} — bỏ qua",
                tenantId, tool.Name);
            return null;
        }

        var (sessionId, phamVi) = await SessionAsync(tenantId, cfgBot, nhanVienSessionId, nguoiPhuTrach, ct);
        if (sessionId is null) return null;

        var duLieu = await GoiAsync(tenantId, sessionId, tool, thamSo, phamVi, ct);
        if (duLieu is null) return null;

        return new KetQua(tool.Title, ChuanBiBang(duLieu.Value, MaxDataChars));
    }

    // ── Chọn tool ───────────────────────────────────────────────────────────

    /// <summary>
    /// Một lượt AI rẻ để chọn tool. Trả tool <c>null</c> khi câu hỏi không liên quan tour (chào
    /// hỏi, hỏi đường, than phiền) — lúc đó trợ lý trả lời thường, không cần kho dữ liệu nào.
    ///
    /// <para>Tham số trả về KÈM tool chứ không cất vào field: lớp này chạy đồng thời cho nhiều
    /// hội thoại, một field dùng chung là hai khách giẫm lên tham số của nhau — mà hỏng kiểu đó
    /// không ném lỗi, chỉ lặng lẽ trả sai tour cho sai người.</para>
    /// </summary>
    private async Task<(ChatTool? Tool, JsonElement? Params)> ChonToolAsync(string tenantId,
        string cauHoi, CancellationToken ct)
    {
        var danhMuc = ChatTools.All
            .Where(t => AllowedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (danhMuc.Count == 0) return (null, null);

        var bang = new StringBuilder();
        foreach (var t in danhMuc)
        {
            var ps = t.Params.Length == 0 ? "(không tham số)" : string.Join(", ", t.Params);
            bang.Append("- ").Append(t.Name).Append(": ").Append(t.Description)
                .Append(" | params: ").Append(ps).Append('\n');
        }

        var sys = "Bạn là bộ chọn API cho câu hỏi của khách du lịch. "
                + "TRẢ VỀ ĐÚNG 1 JSON {\"tool\":\"<name>\",\"params\":{...}}. "
                + "Câu hỏi KHÔNG cần dữ liệu tour (chào hỏi, cảm ơn, hỏi đường, than phiền, hỏi "
                + "chính sách) → TRẢ VỀ {\"tool\":\"none\"}. KHÔNG giải thích.\n"
                // Khách nhắc tên nơi nào thì PHẢI lọc theo tên đó. Bỏ qua là rơi vào bẫy danh sách
                // cắt trang: API trả 20 dòng đầu, không có nơi khách hỏi, rồi bot kết luận chắc
                // nịch "không có tour" trong khi có. Phủ định sai nghe thuyết phục hơn im lặng.
                + "Khách nhắc ĐIỂM ĐẾN nào thì BẮT BUỘC đưa vào tourName. "
                + "Khách nhắc NGÀY/THÁNG thì đưa vào startDate/endDate (yyyy-MM-dd).";

        try
        {
            using var _ = _aiCtx.Push(AiFeatures.ChatInboxTourPlan, tenantId);
            var resolved = _models.Resolve(AiFeature.ChatInboxTourPlan);
            var provider = _providers.Resolve(resolved.Provider);
            var res = await provider.CompleteAsync(new CompleteRequest(
                Prompt: $"DANH SÁCH API:\n{bang}\nKHÁCH HỎI:\n{cauHoi.Trim()}\n\nChọn API phù hợp NHẤT:",
                Provider: provider.Id, Model: resolved.Model,
                MaxTokens: 300, Temperature: 0.1, System: sys, ApiKey: resolved.ApiKey), ct);

            var obj = LooseJson.ExtractFirstObject(res.Text);
            if (obj is null) return (null, null);
            using var doc = JsonDocument.Parse(obj);
            var ten = doc.RootElement.TryGetProperty("tool", out var t1) && t1.ValueKind == JsonValueKind.String
                ? t1.GetString() : null;
            if (string.IsNullOrEmpty(ten) || string.Equals(ten, "none", StringComparison.OrdinalIgnoreCase))
                return (null, null);

            var tool = ChatTools.Find(ten);
            if (tool is null) return (null, null);
            JsonElement? prms = doc.RootElement.TryGetProperty("params", out var p) ? p.Clone() : null;
            return (tool, prms);
        }
        catch (QuotaExhaustedException)
        {
            // Hết lượt AI thì KHÔNG đốt thêm lượt nào cho việc tra cứu — để dành cho chính câu trả
            // lời. Trợ lý vẫn nói được, chỉ là không có số.
            _log.LogWarning("[chat/tour] tenant={T} hết lượt AI ở bước chọn tool", tenantId);
            return (null, null);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/tour] tenant={T} chọn tool hỏng", tenantId);
            return (null, null);
        }
    }

    // ── Danh tính ───────────────────────────────────────────────────────────

    /// <summary>Danh tính dùng để gọi API kho tour.</summary>
    public enum DanhTinh
    {
        /// <summary>Phiên của chính nhân viên đang bấm Gợi ý.</summary>
        PhienNhanVien,
        /// <summary>Phiên của người phụ trách hội thoại (lượt bot tự trả lời).</summary>
        PhienNguoiPhuTrach,
        /// <summary>Tài khoản dịch vụ của công ty — xem cả kho.</summary>
        TaiKhoanDichVu,
    }

    /// <summary>
    /// Chọn danh tính. <b>Hàm THUẦN</b> — tách riêng khỏi phần đi lấy phiên để luật này test được
    /// mà không cần CSDL, vì nó quyết định công ty thấy dữ liệu nào.
    ///
    /// <para><b>Ô cấu hình quyết, không phải ngữ cảnh.</b> Tắt ô là cả hai đường (nhân viên bấm và
    /// bot tự trả lời) đều xem cả kho. Bản đầu suy từ ngữ cảnh nên cùng một câu hỏi ra hai kết quả
    /// khác nhau tuỳ ai bấm, trong khi công ty không khai gì cả.</para>
    /// </summary>
    public static DanhTinh ChonDanhTinh(bool theoQuyenNhanVien, bool coPhienNhanVien,
        bool coNguoiPhuTrach)
    {
        if (!theoQuyenNhanVien) return DanhTinh.TaiKhoanDichVu;
        if (coPhienNhanVien) return DanhTinh.PhienNhanVien;
        if (coNguoiPhuTrach) return DanhTinh.PhienNguoiPhuTrach;
        // Bật theo quyền mà hội thoại chưa gán ai: rơi về cả kho. Thà tư vấn rộng hơn ý muốn còn
        // hơn để bot im trước câu hỏi của khách thật.
        return DanhTinh.TaiKhoanDichVu;
    }

    /// <summary>
    /// Phiên TourKit dùng để gọi API — <b>quyết định lượt trả lời này thấy được dữ liệu nào</b>.
    ///
    /// <para>Không đẻ mô hình quyền mới: phạm vi thật nằm ở ERP. Bên này chỉ chọn đi bằng danh
    /// tính nào, còn chặn tới đâu là việc của ERP với chính quyền của danh tính đó.</para>
    ///
    /// <para>Người phụ trách chưa từng đăng nhập TravAi thì không có phiên để mượn → rơi về tài
    /// khoản dịch vụ, cùng lối với hội thoại chưa gán ai.</para>
    ///
    /// <para>Chưa khai tài khoản dịch vụ thì trả <c>null</c> → bot trả lời như cũ, không có số.</para>
    /// </summary>
    private async Task<(string? SessionId, string PhamVi)> SessionAsync(string tenantId,
        ChatBotSettings cfgBot, string? nhanVienSessionId, string? nguoiPhuTrach, CancellationToken ct)
    {
        var chon = ChonDanhTinh(cfgBot.TourLookupByUser,
            !string.IsNullOrWhiteSpace(nhanVienSessionId),
            !string.IsNullOrWhiteSpace(nguoiPhuTrach));

        if (chon == DanhTinh.PhienNhanVien) return (nhanVienSessionId, PhamViUser);

        if (chon == DanhTinh.PhienNguoiPhuTrach)
        {
            try
            {
                var phien = await _sessions.FindByUserAsync(tenantId, nguoiPhuTrach!, ct);
                if (phien is not null) return (phien.Id, PhamViUser);
                _log.LogInformation("[chat/tour] tenant={T} người phụ trách chưa có phiên — về cả kho", tenantId);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[chat/tour] tenant={T} tra phiên người phụ trách hỏng — về cả kho", tenantId);
            }
        }

        var svc = _serviceAccounts.Get(tenantId);
        if (svc is null || !svc.Enabled)
        {
            _log.LogInformation("[chat/tour] tenant={T} chưa khai tài khoản dịch vụ — bot trả lời không có số", tenantId);
            return (null, PhamViFull);
        }
        try
        {
            var sid = await _sessions.GetOrCreateServiceSessionAsync(tenantId, svc.Username, svc.Password, ct);
            return (sid, PhamViFull);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/tour] tenant={T} đăng nhập tài khoản dịch vụ hỏng", tenantId);
            return (null, PhamViFull);
        }
    }

    /// <summary>
    /// Nhãn phạm vi gắn vào lệnh gọi — <c>?scope=user</c> / <c>?scope=full</c>.
    ///
    /// <para>Không dùng để CHẶN (upstream vẫn lọc theo quyền của chính token gửi lên). Nó ở đây để
    /// <b>đọc lại được</b>: nhìn log hay nhìn đường dẫn là biết câu trả lời vừa rồi dựa trên kho
    /// tour của ai — của nhân viên đang trực, hay của tài khoản tự động. Thiếu nhãn này thì hai ca
    /// trông giống hệt nhau, và lúc khách phàn nàn "sao bạn kia báo còn chỗ mà bạn này bảo hết"
    /// thì không có cách nào truy.</para>
    /// </summary>
    private const string PhamViUser = "user";
    private const string PhamViFull = "full";

    // ── Gọi CRM ─────────────────────────────────────────────────────────────

    private async Task<JsonElement?> GoiAsync(string tenantId, string sessionId, ChatTool tool,
        JsonElement? thamSo, string phamVi, CancellationToken ct)
    {
        // BuildPath lọc tham số theo danh sách Params của chính tool — AI gửi thừa key thì bị bỏ,
        // không đi thẳng vào query. Khoá cache lấy CHÍNH đường đã dựng xong, nên hai câu hỏi khác
        // nhau (khác điểm đến, khác ngày) ra hai khoá khác nhau; và cache chỉ giữ GÓI DỮ LIỆU thô
        // chứ không giữ câu trả lời — câu trả lời luôn sinh mới. Xem e449b69 về lý do cache câu
        // trả lời đã bị bỏ hẳn còn cache dữ liệu thì giữ.
        var path = ChatTools.BuildPath(tool, thamSo);
        // Nhãn phạm vi nối vào đường gọi -> cũng vào luôn KHOÁ CACHE. Cần thế: cùng một câu hỏi
        // nhưng hỏi bằng quyền nhân viên và bằng tài khoản tự động cho ra hai danh sách khác nhau,
        // chung khoá là câu trả lời của người này rơi sang người kia.
        path += (path.Contains('?') ? "&" : "?") + "scope=" + phamVi;
        var khoa = "d|" + tenantId + "|" + path;

        if (_cache.TryGet<JsonElement>(khoa, out var sanCo)) return sanCo;

        try
        {
            var jwt = await _sessions.GetValidJwtAsync(sessionId, ct);
            JsonElement data;
            try
            {
                data = await _api.GetAsync(jwt, path, ct);
            }
            catch (TourKitApiException ex) when (ex.Status == 401)
            {
                // Phiên hết hạn giữa chừng — đăng nhập lại ĐÚNG MỘT LẦN rồi thử lại, giống widget.
                jwt = await _sessions.ForceReloginAsync(sessionId, ct);
                data = await _api.GetAsync(jwt, path, ct);
            }
            _cache.Set(khoa, data, CacheTtl);
            return data;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[chat/tour] tenant={T} gọi {Path} hỏng", tenantId, path);
            return null;
        }
    }

    // ── Dọn bảng trước khi đưa cho model ────────────────────────────────────

    /// <summary>Tính sẵn chỗ còn, bỏ trường nội bộ, rồi cắt cho vừa lời nhắc.</summary>
    internal static string ChuanBiBang(JsonElement el, int max)
    {
        if (el.ValueKind == JsonValueKind.Undefined) return "{}";
        if (JsonNode.Parse(el.GetRawText()) is not JsonObject goc) return JsonSerializer.Serialize(el, GiuTiengViet);
        ThemChoConLai(goc);
        LocTruongNoiBo(goc);
        return RutGon(goc, max);
    }

    /// <summary>
    /// Trường được phép lọt tới lời nhắc trả lời khách.
    ///
    /// <para><b>Danh sách TRẮNG, không phải danh sách đen.</b> <c>/api/ai/tours</c> là API dựng cho
    /// báo cáo nội bộ — nó trả về <c>customerName</c> (tên khách ĐÃ đặt tour), <c>sellerName</c>,
    /// <c>sellerUserName</c>, <c>revenue</c>, <c>actualRevenue</c>. Bơm nguyên gói vào lời nhắc là
    /// đặt tên khách khác và doanh thu công ty vào ngay ngữ cảnh mà trợ lý dùng để nói chuyện với
    /// một người lạ. Khung an toàn có dặn đừng tiết lộ, nhưng <b>dặn không phải là chặn</b>.</para>
    ///
    /// <para>Trắng chứ không đen vì API nội bộ sẽ còn thêm trường; danh sách đen thì mỗi trường mới
    /// là một lỗ mở ra trong im lặng, còn danh sách trắng thì trường mới bị bỏ cho tới khi có người
    /// cố ý thêm vào đây.</para>
    /// </summary>
    private static readonly HashSet<string> TruongChoKhach = new(StringComparer.OrdinalIgnoreCase)
    {
        "title", "tourName", "tourCode", "departureDate", "returnDate", "endDate",
        "tourType", "tourTypeLabel", "status", "statusText", "statusName", "duration", "itinerary",
        "destination", "slots", "choConLai",
        // id: cần để trợ lý gọi tiếp tour_detail. Là mã nội bộ, không phải thứ đọc cho khách.
        "id", "nights",
        // Phần nội dung tư vấn của tour_detail. KHÔNG có "services": cột tour_samples.Services
        // nghe như "dịch vụ bao gồm" nhưng đo trên staging 18/09/2026 nó là BẢNG KÊ MUA HÀNG —
        // provider_info, codeNCC, total_chi, vat, payment_term, tức tên NHÀ CUNG CẤP và GIÁ VỐN.
        // Đây là chốt chặn cuối; để tên đó trong danh sách trắng là chừa sẵn cửa cho giá vốn đi
        // thẳng tới màn hình khách nếu có nguồn nào trả nó về.
        "termsNote", "pickupPlace", "dropoffPlace", "discount",
        // Upstream ĐÃ tính sẵn, công thức trùng đúng của mình (TourService: Math.Max(0, slots -
        // booked - onHold)). Giữ lại để khỏi phải tin vào phép trừ của chính mình.
        "available",
        // FIT có thông tin chuyến bay ngay trên dòng tour — thứ khách hỏi thật.
        "flightItinerary", "flightItineraryGo", "flightItineraryBack",
    };

    /// <summary>
    /// Trường GIÁ thì giữ theo hình dạng tên, vì chưa gọi thật nên chưa biết upstream đặt tên gì.
    /// Cố ý KHÔNG khớp <c>revenue</c> / <c>actualRevenue</c> — hai cái đó là doanh thu của tour,
    /// không phải giá một khách, và nói ra là lộ chuyện làm ăn của công ty.
    /// </summary>
    private static bool LaTruongGia(string k)
        => (k.Contains("price", StringComparison.OrdinalIgnoreCase)
            || k.Contains("gia", StringComparison.OrdinalIgnoreCase))
        && !k.Contains("revenue", StringComparison.OrdinalIgnoreCase)
        && !k.Contains("cost", StringComparison.OrdinalIgnoreCase)
        && !k.Contains("profit", StringComparison.OrdinalIgnoreCase);

    /// <summary>Bỏ mọi trường không nằm trong danh sách trắng khỏi từng dòng.</summary>
    internal static void LocTruongNoiBo(JsonObject goc)
    {
        if (goc["items"] is not JsonArray ds) return;
        foreach (var it in ds)
        {
            if (it is not JsonObject o) continue;
            var bo = o.Select(p => p.Key)
                      .Where(k => !TruongChoKhach.Contains(k) && !LaTruongGia(k))
                      .ToList();
            foreach (var k in bo) o.Remove(k);
        }
    }

    /// <summary>
    /// Tính sẵn <c>choConLai</c> cho từng dòng — <b>không để model tự trừ</b>.
    ///
    /// <para>Hai lý do, cả hai đều đã cắn người khác trước đây. Một: mô hình ngôn ngữ làm tính
    /// không đáng tin, mà đây là con số đi thẳng tới khách. Hai, nặng hơn: công thức đúng là
    /// <c>slots − (booked + onHold)</c>, và CLAUDE.md ghi rõ nhãn <c>OnHold</c>/<c>Booked</c> bị
    /// <b>đảo cố ý</b> ở giao diện mobile. Một mô hình đọc tên trường rồi suy ra ý nghĩa sẽ suy ra
    /// sai — nó không đọc được CLAUDE.md.</para>
    ///
    /// <para>Cùng công thức với <c>TourReadinessRule.Available</c>. Đổi ở đây phải đổi cả bên đó.</para>
    /// </summary>
    internal static void ThemChoConLai(JsonObject goc)
    {
        if (goc["items"] is not JsonArray ds) return;
        foreach (var it in ds)
        {
            if (it is not JsonObject o) continue;

            // Nguồn đã tính sẵn thì ĐỪNG tính lại. Đường tour_catalog trả `available` nhưng KHÔNG
            // trả booked/onHold — tự trừ ở đây sẽ ra `slots - 0 - 0 = slots`, tức báo tour trống
            // trơn trong khi nó sắp đầy, rồi đặt cạnh `available` đúng cho mô hình chọn bừa một
            // trong hai. Hỏng kiểu này không ném lỗi, chỉ hứa với khách một chỗ không có.
            if (o["available"] is not null) continue;

            if (o["slots"] is null) continue;               // tour lẻ / dịch vụ: không có khái niệm chỗ
            var slots = So(o["slots"]);
            if (slots <= 0) continue;
            o["choConLai"] = Math.Max(0, slots - (So(o["booked"]) + So(o["onHold"])));
        }
    }

    private static int So(JsonNode? n)
    {
        if (n is null) return 0;
        try { return n.GetValue<int>(); }
        catch { return int.TryParse(n.ToString(), out var v) ? v : 0; }
    }

    /// <summary>
    /// Cắt gói dữ liệu cho vừa lời nhắc, và <b>nói ra là đã cắt</b>.
    ///
    /// <para>Khoá <c>_truncated</c> không phải để trang trí: thiếu nó thì mô hình đọc một danh sách
    /// cụt mà tưởng là danh sách đủ, rồi kết luận "công ty không có tour đi đó". Cắt im lặng là
    /// cách tạo ra phủ định sai — thứ nghe thuyết phục hơn hẳn một câu "em chưa thấy".</para>
    /// </summary>
    internal static string RutGon(JsonObject goc, int max)
    {
        var json = goc.ToJsonString(GiuTiengViet);
        if (json.Length <= max) return json;

        if (goc["items"] is not JsonArray ds) return json[..max] + "...[đã cắt]";

        var tong = ds.Count;
        var giu = new List<JsonNode?>();
        var dai = 0;
        foreach (var it in ds)
        {
            var s = it?.ToJsonString(GiuTiengViet) ?? "null";
            if (dai + s.Length > max - 500) break;
            giu.Add(it is null ? null : JsonNode.Parse(s));
            dai += s.Length;
        }

        var ra = new JsonObject();
        foreach (var p in goc)
            if (p.Key != "items" && p.Value is not null)
                ra[p.Key] = JsonNode.Parse(p.Value.ToJsonString());
        ra["items"] = new JsonArray(giu.ToArray());
        ra["_truncated"] = $"đang hiện {giu.Count}/{tong} mục — danh sách CHƯA đủ";
        return ra.ToJsonString(GiuTiengViet);
    }
}
