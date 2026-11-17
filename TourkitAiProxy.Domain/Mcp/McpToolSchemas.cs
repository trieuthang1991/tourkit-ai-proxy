using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using TourkitAiProxy.Domain.Chat;

namespace TourkitAiProxy.Domain.Mcp;

/// <summary>Kiểu của một tham số tool MCP — quyết định cả schema lẫn cách ép kiểu.</summary>
public enum McpParamType { Date, Integer, Boolean, Text }

/// <summary>Kiểu + mô tả ngắn tiếng Việt của một tham số (mô tả nằm trong schema, mô hình đọc nó).</summary>
public sealed record McpParamSpec(McpParamType Type, string Description);

/// <summary>
/// Schema đầu vào + ép kiểu tham số cho các tool MCP sinh từ <see cref="ChatTools.All"/>.
///
/// <para><b>Vì sao phải khai kiểu từng tham số.</b> <see cref="ChatTool.Params"/> chỉ là danh sách TÊN —
/// đủ cho planner nội bộ (nó tự viết query), nhưng máy khách MCP (Claude, Cursor…) đọc <c>inputSchema</c>
/// để biết gửi gì. Không có kiểu thì mô hình gửi "15/10/2026" vào ngày, "ba" vào số, và CRM trả 400
/// hoặc — tệ hơn — lặng lẽ bỏ lọc rồi trả số của cả công ty.</para>
///
/// <para><b>Bảng kiểu là danh sách đóng.</b> Thêm tham số mới vào <c>ChatTools</c> mà quên khai ở đây thì
/// test <c>Moi_tham_so_trong_ChatTools_deu_co_kieu</c> đỏ — cố ý, không để tham số rơi về "chuỗi" trong im
/// lặng. Lúc chạy vẫn phòng thủ: tên lạ (không thể xảy ra nếu test xanh) coi là chuỗi.</para>
///
/// <para>Kiểu lấy theo DTO bên TourKit.Api (<c>AiController</c>): <c>branch</c>, <c>groupId</c> là
/// <c>string</c> (mã chi nhánh/nhóm), <c>gender</c> là "M"/"F" — đừng "sửa" thành số.</para>
/// </summary>
public static class McpToolSchemas
{
    /// <summary>Trần số dòng một trang. CRM có vài mệnh đề IN theo id của trang — nâng pageSize là nhân số
    /// tham số SQL lên theo; và kết quả MCP chỉ hiện 20 dòng, kéo 500 dòng về chỉ tốn thời gian.</summary>
    public const int MaxPageSize = 50;

    private const string DateFormat = "yyyy-MM-dd";

    public static readonly IReadOnlyDictionary<string, McpParamSpec> ParamTypes =
        new Dictionary<string, McpParamSpec>(StringComparer.OrdinalIgnoreCase)
        {
            // ── Ngày ──
            ["startDate"] = new(McpParamType.Date, "Từ ngày, dạng yyyy-MM-dd"),
            ["endDate"] = new(McpParamType.Date, "Đến ngày (tính cả ngày này), dạng yyyy-MM-dd"),
            ["fromDate"] = new(McpParamType.Date, "Khởi hành từ ngày, dạng yyyy-MM-dd"),
            ["toDate"] = new(McpParamType.Date, "Khởi hành đến ngày, dạng yyyy-MM-dd"),
            // ── Số ──
            ["year"] = new(McpParamType.Integer, "Năm, vd 2026"),
            ["pageIndex"] = new(McpParamType.Integer, "Trang thứ mấy, bắt đầu từ 1"),
            ["pageSize"] = new(McpParamType.Integer, $"Số dòng mỗi trang, tối đa {MaxPageSize}"),
            ["id"] = new(McpParamType.Integer, "Mã tour — lấy từ trường 'id' của một dòng trong tour_catalog"),
            ["employeeId"] = new(McpParamType.Integer, "Mã nhân viên (nếu chỉ biết tên thì gửi employeeName)"),
            ["marketId"] = new(McpParamType.Integer, "Mã thị trường (nếu chỉ biết tên thì gửi marketName)"),
            ["tourType"] = new(McpParamType.Integer, "Loại tour — mã số như mô tả tool"),
            ["status"] = new(McpParamType.Integer, "Trạng thái — mã số như mô tả tool"),
            ["trangThai"] = new(McpParamType.Integer, "Trạng thái — mã số như mô tả tool"),
            ["nguon"] = new(McpParamType.Integer, "Nguồn — mã số như mô tả tool"),
            ["nhanVienPhuTrach"] = new(McpParamType.Integer, "Mã nhân viên phụ trách"),
            ["tabFilter"] = new(McpParamType.Integer, "Bộ lọc nhanh — mã số như mô tả tool"),
            ["mucDoUuTien"] = new(McpParamType.Integer, "Mức độ ưu tiên — mã số như mô tả tool"),
            ["workFlowId"] = new(McpParamType.Integer, "Mã quy trình công việc"),
            ["customerGroupId"] = new(McpParamType.Integer, "Nhóm khách — mã số như mô tả tool"),
            ["customerTypeId"] = new(McpParamType.Integer, "Mã loại khách"),
            ["careFilter"] = new(McpParamType.Integer, "Chưa chăm sóc bao lâu — mã số như mô tả tool"),
            ["dateFilter"] = new(McpParamType.Integer, "Bộ lọc thời gian — mã số như mô tả tool"),
            ["voucherType"] = new(McpParamType.Integer, "Loại phiếu: 4=Phiếu thu, 5=Phiếu chi"),
            ["approvalStatus"] = new(McpParamType.Integer, "Trạng thái duyệt — mã số như mô tả tool"),
            // ── Có/không ──
            ["birthdayThisMonth"] = new(McpParamType.Boolean, "true = chỉ khách có sinh nhật trong tháng này"),
            ["onlyWaiting"] = new(McpParamType.Boolean, "true = chỉ phiếu đang chờ duyệt"),
            // ── Chuỗi ──
            ["branch"] = new(McpParamType.Text, "Mã chi nhánh"),
            ["groupId"] = new(McpParamType.Text, "Mã nhóm chi nhánh"),
            ["groupBy"] = new(McpParamType.Text, "Gộp theo: month (tháng) hoặc day (ngày)"),
            ["employeeName"] = new(McpParamType.Text, "Tên nhân viên — máy chủ tự đổi sang mã"),
            ["marketName"] = new(McpParamType.Text, "Tên thị trường (vd 'Nhật Bản', 'Nội địa miền Nam') — máy chủ tự đổi sang mã"),
            ["keyword"] = new(McpParamType.Text, "Từ khoá tìm kiếm"),
            ["tourName"] = new(McpParamType.Text, "Tên tour (tìm gần đúng)"),
            ["filter"] = new(McpParamType.Text, "Từ khoá tìm kiếm"),
            ["gender"] = new(McpParamType.Text, "Giới tính: M hoặc F"),
        };

    /// <summary>
    /// Tham số MCP nhận được của một tool = <see cref="ChatTool.Params"/> + <c>marketName</c> nếu tool lọc
    /// được theo <c>marketId</c>. Lý do: AI biết TÊN thị trường chứ không biết mã; <c>CrmToolRunner</c> đổi
    /// tên → mã cho MỌI tool, nhưng <c>market_analysis</c> chỉ khai <c>marketId</c> nên nếu bám đúng
    /// <c>Params</c> thì schema chặn mất đường đó.
    /// </summary>
    public static IReadOnlyList<string> ParamsOf(ChatTool tool)
    {
        var list = tool.Params.ToList();
        if (list.Contains("marketId", StringComparer.OrdinalIgnoreCase)
            && !list.Contains("marketName", StringComparer.OrdinalIgnoreCase))
            list.Add("marketName");
        return list;
    }

    private static McpParamSpec SpecOf(string name)
        => ParamTypes.TryGetValue(name, out var s) ? s : new McpParamSpec(McpParamType.Text, name);

    /// <summary><c>{"type":"object","properties":{...},"additionalProperties":false}</c> — không có
    /// <c>required</c>: tham số nào bắt buộc theo nghiệp vụ đã nói trong mô tả tool, CRM có mặc định cho phần còn lại.</summary>
    public static JsonElement InputSchemaFor(ChatTool tool)
    {
        var props = new JsonObject();
        foreach (var name in ParamsOf(tool))
        {
            var spec = SpecOf(name);
            var p = new JsonObject { ["description"] = spec.Description };
            switch (spec.Type)
            {
                case McpParamType.Date:
                    p["type"] = "string";
                    p["format"] = "date";
                    break;
                case McpParamType.Integer:
                    p["type"] = "integer";
                    if (IsPaging(name)) p["minimum"] = 1;
                    if (name.Equals("pageSize", StringComparison.OrdinalIgnoreCase)) p["maximum"] = MaxPageSize;
                    break;
                case McpParamType.Boolean:
                    p["type"] = "boolean";
                    break;
                default:
                    p["type"] = "string";
                    break;
            }
            props[name] = p;
        }
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = props,
            ["additionalProperties"] = false,
        };
        return JsonSerializer.SerializeToElement(schema);
    }

    private static bool IsPaging(string name)
        => name.Equals("pageSize", StringComparison.OrdinalIgnoreCase) || name.Equals("pageIndex", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ép tham số máy khách gửi về đúng kiểu trước khi dựng query. Trả <c>(Params, null)</c> khi được,
    /// <c>(null, câu lỗi)</c> khi không — câu lỗi nêu TÊN tham số + dạng đúng để mô hình tự sửa và gọi lại.
    ///
    /// <list type="bullet">
    /// <item>Số/có-không gửi dạng chuỗi ("3", "true") được ép — mô hình hay làm vậy, từ chối thì phí một lượt.</item>
    /// <item>Ngày phải đúng <c>yyyy-MM-dd</c>; không đoán "15/10/2026" là 15/10 hay 10/15 — báo lỗi kèm gợi ý.</item>
    /// <item><c>pageSize</c> kẹp vào 1..50, <c>pageIndex</c> ≥ 1 (kẹp chứ không báo lỗi: ý người dùng vẫn rõ).</item>
    /// <item>null hoặc chuỗi rỗng = không gửi tham số đó.</item>
    /// </list>
    /// </summary>
    public static (JsonElement? Params, string? Error) Coerce(ChatTool tool, JsonElement? args)
    {
        var result = new JsonObject();
        if (args is not { } a || a.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return (JsonSerializer.SerializeToElement(result), null);
        if (a.ValueKind != JsonValueKind.Object)
            return (null, "Tham số phải là một đối tượng JSON dạng {\"tên\": giá trị}.");

        var allowed = ParamsOf(tool);
        foreach (var p in a.EnumerateObject())
        {
            var name = allowed.FirstOrDefault(x => x.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (name == null)
                return (null, allowed.Count == 0
                    ? $"Tool {tool.Name} không nhận tham số nào (nhận được '{p.Name}')."
                    : $"Tool {tool.Name} không có tham số '{p.Name}'. Tham số hợp lệ: {string.Join(", ", allowed)}.");

            var v = p.Value;
            if (v.ValueKind == JsonValueKind.Null) continue;
            if (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString())) continue;
            if (v.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                return (null, $"Tham số '{name}' phải là một giá trị đơn, không phải danh sách hay đối tượng.");

            var spec = SpecOf(name);
            switch (spec.Type)
            {
                case McpParamType.Date:
                {
                    var s = v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : v.GetRawText();
                    if (v.ValueKind != JsonValueKind.String
                        || !DateTime.TryParseExact(s, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                        return (null, DateError(name, s));
                    result[name] = s;
                    break;
                }
                case McpParamType.Integer:
                {
                    long n;
                    if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out n)) { }
                    else if (v.ValueKind == JsonValueKind.String
                             && long.TryParse(v.GetString()!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n)) { }
                    else return (null, $"Tham số '{name}' phải là số nguyên, nhận được {v.GetRawText()}.");

                    if (IsPaging(name) && n < 1) n = 1;
                    if (name.Equals("pageSize", StringComparison.OrdinalIgnoreCase) && n > MaxPageSize) n = MaxPageSize;
                    result[name] = n;
                    break;
                }
                case McpParamType.Boolean:
                {
                    bool b;
                    if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) b = v.GetBoolean();
                    else if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString()!.Trim(), out var sb)) b = sb;
                    else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var bi) && bi is 0 or 1) b = bi == 1;
                    else return (null, $"Tham số '{name}' phải là true hoặc false, nhận được {v.GetRawText()}.");
                    result[name] = b;
                    break;
                }
                default:
                {
                    // Chuỗi: số/có-không cũng nhận (mã chi nhánh "1" gửi thành số 1 vẫn đúng ý).
                    var s = v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString()!.Trim(),
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => v.GetRawText(),
                    };
                    result[name] = s;
                    break;
                }
            }
        }
        return (JsonSerializer.SerializeToElement(result), null);
    }

    private static string DateError(string name, string got)
    {
        var msg = $"Tham số '{name}' phải là ngày dạng yyyy-MM-dd (vd 2026-10-15), nhận được '{got}'.";
        // Người Việt viết ngày/tháng/năm — gợi ý đúng bản chuyển đổi để mô hình gọi lại ngay.
        if (DateTime.TryParseExact(got, new[] { "d/M/yyyy", "dd/MM/yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            msg += $" Có phải ý bạn là {d.ToString(DateFormat, CultureInfo.InvariantCulture)}?";
        return msg;
    }
}
