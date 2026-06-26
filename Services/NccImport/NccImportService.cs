using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using MimeKit;
using TourkitAiProxy.Models;
using TourkitAiProxy.Services.Json;
using TourkitAiProxy.Services.Providers;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;
using WP = DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;

namespace TourkitAiProxy.Services.NccImport;

/// <summary>
/// Bóc tách + chuẩn hoá file NCC (Excel/CSV/text dán tay) → list NccRow khớp schema
/// file_import_ncc.xlsx. Excel/CSV parse trực tiếp (không cần AI), text dán tay đi
/// qua AI để tách hàng + đoán Loại NCC. Trong cả 2 luồng, "Loại NCC" lệch chuẩn
/// sẽ được AI snap về enum gần nhất.
/// </summary>
public class NccImportService
{
    private readonly ProviderRegistry _registry;
    private readonly AiModelRegistry _modelRegistry;
    private readonly ILogger<NccImportService> _log;

    // 10 enum Loại NCC từ file mẫu — AI bắt buộc snap free-text về 1 trong các giá trị này.
    public static readonly string[] AllowedTypes = new[]
    {
        "Mũ", "Visa", "Tour Hàng Ngày", "Chi Phí Khác", "Nhà xe",
        "Vận chuyển", "Nước suối", "LandTour", "Hướng dẫn viên", "Quỹ phòng"
    };
    public static readonly string[] AllowedStatus = new[] { "Hoạt động", "Ngừng" };

    public NccImportService(ProviderRegistry registry, AiModelRegistry modelRegistry, ILogger<NccImportService> log)
    {
        _registry = registry; _modelRegistry = modelRegistry; _log = log;
    }

    // ──────────────────────────────────────────────────────────────────────────
    /// Đọc file binary → text dạng "rows" để feed cho AI (hoặc parse trực tiếp).
    public NccExtractResult ParseExcel(Stream stream, CancellationToken ct)
    {
        var rawRows = ReadExcelRows(stream);
        return NormalizeRows(rawRows, source: "excel");
    }

    public NccExtractResult ParseCsv(string text, CancellationToken ct)
    {
        var rawRows = ReadCsvRows(text);
        return NormalizeRows(rawRows, source: "csv");
    }

    /// Đọc text từ PDF (PdfPig — pure C#, không native deps) rồi pipe qua AI.
    public Task<NccExtractResult> ExtractFromPdfAsync(Stream stream, string? providerId, string? model, CancellationToken ct)
    {
        var sb = new StringBuilder();
        // PdfPig đọc stream cần seek. ASP.NET form stream không seekable → copy sang MemoryStream.
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        using (var pdf = PdfDocument.Open(ms))
        {
            foreach (UglyToad.PdfPig.Content.Page p in pdf.GetPages())
            {
                sb.AppendLine(p.Text);
                sb.AppendLine();
            }
        }
        return ExtractFromTextAsync(sb.ToString(), providerId, model, ct);
    }

    /// Đọc text từ Word .docx bằng DocumentFormat.OpenXml (đã có sẵn) rồi pipe qua AI.
    /// Lấy tất cả paragraph + table cell (vì NCC list trong Word thường ở table).
    public Task<NccExtractResult> ExtractFromDocxAsync(Stream stream, string? providerId, string? model, CancellationToken ct)
    {
        var sb = new StringBuilder();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;
        using (var doc = WordprocessingDocument.Open(ms, false))
        {
            var body = doc.MainDocumentPart?.Document.Body;
            if (body == null) throw new InvalidOperationException("Word rỗng / không đọc được");

            // Walk theo thứ tự document → giữ table rows làm 1 line (cells join bằng " | ")
            foreach (var node in body.ChildElements)
            {
                if (node is WP.Paragraph para)
                {
                    sb.AppendLine(para.InnerText);
                }
                else if (node is WP.Table tbl)
                {
                    foreach (var row in tbl.Elements<WP.TableRow>())
                    {
                        var cells = row.Elements<WP.TableCell>()
                            .Select(c => c.InnerText.Trim())
                            .Where(t => !string.IsNullOrEmpty(t));
                        var line = string.Join(" | ", cells);
                        if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
                    }
                    sb.AppendLine();
                }
            }
        }
        return ExtractFromTextAsync(sb.ToString(), providerId, model, ct);
    }

    /// User dán text tự do (paste từ Word, PDF copy…). AI mới biết tách hàng.
    public async Task<NccExtractResult> ExtractFromTextAsync(string text, string? providerId, string? model, CancellationToken ct)
    {
        // Resolve qua AiModelRegistry → caller có thể omit providerId/model, đọc từ Models:NccImport.
        var resolved = _modelRegistry.Resolve(AiFeature.NccImport, providerId, model);
        var provider = _registry.Resolve(resolved.Provider);
        var prompt = BuildAiExtractPrompt(text);
        var req = new CompleteRequest(
            Prompt: prompt,
            Provider: provider.Id,
            Model: resolved.Model,
            MaxTokens: 6000,
            Temperature: 0.1,
            System: AI_SYSTEM,
            ApiKey: resolved.ApiKey);
        var ai = await provider.CompleteAsync(req, ct);
        var rows = ParseAiJson(ai.Text);
        return new NccExtractResult(
            Rows: NormalizeRowList(rows),
            Source: "ai-text",
            RawRowCount: rows.Count,
            CleanedRowCount: rows.Count,
            LatencyMs: ai.LatencyMs,
            TokensIn: ai.InputTokens,
            TokensOut: ai.OutputTokens,
            Warning: ai.Warning);
    }

    // ===== GRID QUOTE: trích báo giá NCC GIỮ cấu trúc bảng gốc → JSON {supplier, tables[], conditions[]} =====

    private const string QUOTE_SYSTEM =
        "Bạn là bộ trích xuất báo giá nhà cung cấp (NCC) du lịch. CHỈ trả JSON hợp lệ đúng schema, KHÔNG markdown/giải thích.";

    private const string QUOTE_PROMPT =
"""
Bạn nhận NỘI DUNG file BÁO GIÁ DỊCH VỤ của 1 NCC du lịch. Trích thành JSON ĐÚNG schema, GIỮ NGUYÊN
cấu trúc BẢNG gốc (dạng GRID: mỗi dòng = 1 mục, cột = tiêu đề bảng gốc):
{
  "supplier": { "name", "serviceType":"hotel|restaurant|transport|ticket|combo|other", "address", "city", "phones":[], "email", "website", "contactName", "contactPhone", "validYear" },
  "tables": [ { "title": "<tên bảng>", "columns": ["<tiêu đề cột>"], "rows": [ ["<ô>"] ] } ],
  "conditions": [ "<điều kiện/ghi chú chung>" ]
}
QUY TẮC:
- GIỮ DẠNG BẢNG: mỗi loại phòng / dịch vụ = 1 row. KHÔNG flatten mỗi mức giá thành 1 row riêng.
- Header NHIỀU TẦNG phải GỘP thành 1 nhãn cột rõ ràng (vd "Giá đoàn <7 / không ăn", "Cao điểm - Đầu tuần", "Cao điểm - Cuối tuần", "Trung điểm", "Thấp điểm").
- Số tiền là number (1450000), KHÔNG phải "1.450.000". Ô trống = null. Mỗi row đủ số phần tử = len(columns). Mỗi bảng riêng = 1 phần tử "tables".
- Trả VỀ DUY NHẤT JSON, không markdown / giải thích.
""";

    /// PDF → text (PdfPig) → AI (grid) → JSON báo giá.
    public Task<NccQuoteResult> ExtractQuoteFromPdfAsync(Stream stream, string? providerId, string? model, CancellationToken ct)
        => ExtractQuoteFromTextAsync(ReadPdfText(stream), providerId, model, ct);

    /// Bóc tách báo giá NCC (grid) từ FILE — tự chọn reader theo đuôi file.
    /// Mọi định dạng .NET đọc được (pdf/docx/pptx/xlsx/eml/html/text) → text → ExtractQuoteFromTextAsync.
    public Task<NccQuoteResult> ExtractQuoteFromFileAsync(Stream stream, string ext, string? providerId, string? model, CancellationToken ct)
    {
        var text = ext switch
        {
            ".pdf" => ReadPdfText(stream),
            ".docx" => ReadDocxText(stream),
            ".pptx" => ReadPptxText(stream),
            ".xlsx" => ReadExcelText(stream),
            ".eml" => ReadEmailText(stream, ct),
            ".html" or ".htm" => StripHtml(ReadAllText(stream)),
            ".txt" or ".csv" or ".tsv" or ".json" or ".xml" or ".md" => ReadAllText(stream),
            _ => throw new InvalidOperationException($"Định dạng {ext} chưa hỗ trợ trích báo giá.")
        };
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("Không đọc được nội dung văn bản từ file (file rỗng / lỗi định dạng).");
        return ExtractQuoteFromTextAsync(text, providerId, model, ct);
    }

    // ── Text readers dùng chung (pure, KHÔNG gọi AI) ──────────────────────────
    private static string ReadAllText(Stream stream)
    {
        using var sr = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    private static string ReadPdfText(Stream stream)
    {
        var sb = new StringBuilder();
        using var ms = new MemoryStream();
        stream.CopyTo(ms); ms.Position = 0;
        using var pdf = PdfDocument.Open(ms);
        foreach (var p in pdf.GetPages()) { sb.AppendLine(p.Text); sb.AppendLine(); }
        return sb.ToString();
    }

    private static string ReadDocxText(Stream stream)
    {
        var sb = new StringBuilder();
        using var ms = new MemoryStream();
        stream.CopyTo(ms); ms.Position = 0;
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document.Body
            ?? throw new InvalidOperationException("Word rỗng / không đọc được");
        // Walk theo thứ tự document → giữ table rows làm 1 line (cells join bằng " | ")
        foreach (var node in body.ChildElements)
        {
            if (node is WP.Paragraph para) sb.AppendLine(para.InnerText);
            else if (node is WP.Table tbl)
            {
                foreach (var row in tbl.Elements<WP.TableRow>())
                {
                    var cells = row.Elements<WP.TableCell>().Select(c => c.InnerText.Trim())
                        .Where(t => !string.IsNullOrEmpty(t));
                    var line = string.Join(" | ", cells);
                    if (!string.IsNullOrWhiteSpace(line)) sb.AppendLine(line);
                }
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// PowerPoint: lấy mọi text trong slide (gồm cả ô bảng — cell cũng chứa a:t).
    private static string ReadPptxText(Stream stream)
    {
        var sb = new StringBuilder();
        using var ms = new MemoryStream();
        stream.CopyTo(ms); ms.Position = 0;
        using var doc = PresentationDocument.Open(ms, false);
        var presPart = doc.PresentationPart
            ?? throw new InvalidOperationException("PowerPoint rỗng / không đọc được");
        int n = 0;
        foreach (var slidePart in presPart.SlideParts)
        {
            n++;
            sb.AppendLine($"--- Slide {n} ---");
            foreach (var t in slidePart.Slide.Descendants<D.Text>())
                if (!string.IsNullOrWhiteSpace(t.Text)) sb.AppendLine(t.Text);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// Excel báo giá: dump mọi sheet, mỗi row = cells join " | " (giữ dạng grid cho AI).
    private static string ReadExcelText(Stream stream)
    {
        var sb = new StringBuilder();
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("Excel rỗng");
        var sst = wbPart.SharedStringTablePart?.SharedStringTable;
        foreach (var sheet in wbPart.Workbook.Descendants<Sheet>())
        {
            var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
            sb.AppendLine($"--- Sheet: {sheet.Name} ---");
            foreach (var row in wsPart.Worksheet.Descendants<Row>())
            {
                var line = string.Join(" | ", row.Elements<Cell>().Select(c => GetCellText(c, sst).Trim()));
                if (line.Replace("|", "").Trim().Length > 0) sb.AppendLine(line);
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// Email .eml (MIME) → tiêu đề + người gửi + thân (text, fallback HTML strip).
    private static string ReadEmailText(Stream stream, CancellationToken ct)
    {
        var msg = MimeMessage.Load(stream, ct);
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(msg.Subject)) sb.AppendLine("Tiêu đề: " + msg.Subject);
        var from = msg.From?.ToString();
        if (!string.IsNullOrWhiteSpace(from)) sb.AppendLine("Từ: " + from);
        sb.AppendLine();
        var body = !string.IsNullOrWhiteSpace(msg.TextBody) ? msg.TextBody
                 : !string.IsNullOrWhiteSpace(msg.HtmlBody) ? StripHtml(msg.HtmlBody) : "";
        sb.AppendLine(body);
        return sb.ToString();
    }

    /// Strip HTML → text thuần (bỏ script/style, thẻ block → xuống dòng, decode entity).
    private static string StripHtml(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        s = Regex.Replace(s, @"<\s*(br|/p|/div|/tr|/li|/h[1-6])[^>]*>", "\n", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(s);
    }

    public async Task<NccQuoteResult> ExtractQuoteFromTextAsync(string text, string? providerId, string? model, CancellationToken ct)
    {
        // Resolve qua AiModelRegistry (giống ExtractFromTextAsync) → tôn trọng config Models:NccImport
        // thay vì rơi về provider default (anthropic → claude-sonnet-4-5).
        var resolved = _modelRegistry.Resolve(AiFeature.NccImport, providerId, model);
        var provider = _registry.Resolve(resolved.Provider);
        var req = new CompleteRequest(
            Prompt: QUOTE_PROMPT + "\n\n=== NỘI DUNG FILE ===\n" + text,
            Provider: provider.Id,
            Model: resolved.Model,
            MaxTokens: 8000,
            Temperature: 0.2,
            System: QUOTE_SYSTEM,
            ApiKey: resolved.ApiKey);
        var ai = await provider.CompleteAsync(req, ct);
        var raw = ai.Text ?? "";
        int a = raw.IndexOf('{'), b = raw.LastIndexOf('}');
        if (a < 0 || b < 0 || b < a) throw new InvalidOperationException("AI không trả JSON hợp lệ");
        using var jd = System.Text.Json.JsonDocument.Parse(raw.Substring(a, b - a + 1));
        var quote = jd.RootElement.Clone();
        return new NccQuoteResult(quote, ai.LatencyMs, ai.InputTokens, ai.OutputTokens, ai.Warning);
    }

    // ──────────────────────────────────────────────────────────────────────────
    private NccExtractResult NormalizeRows(List<Dictionary<string, string>> rawRows, string source)
    {
        var rows = new List<NccRow>();
        foreach (var r in rawRows)
        {
            var name = Get(r, "Tên NCC", "Tên", "Name", "Nha cung cap", "NCC");
            if (string.IsNullOrWhiteSpace(name)) continue;   // skip dòng rác

            rows.Add(new NccRow(
                Code: Get(r, "Mã NCC", "Mã", "Code") ?? "",
                Name: name.Trim(),
                Phone: NormalizePhone(Get(r, "Số điện thoại", "SĐT", "Phone", "DT")),
                Email: NullIfEmpty(Get(r, "Email", "Mail")),
                Type: SnapType(Get(r, "Loại NCC", "Loại", "Type")),
                Quantity: ToInt(Get(r, "Số lượng", "SL", "Quantity")),
                TotalBuy: ToMoney(Get(r, "Tổng mua", "Tổng", "Total", "TotalBuy")),
                Paid: ToMoney(Get(r, "Đã trả", "Đa tra", "Paid")),
                Collected: ToMoney(Get(r, "Thu hộ", "Thu ho", "Collected")),
                Owed: ToMoney(Get(r, "Còn nợ", "Con no", "Owed")),
                Balance: ToMoney(Get(r, "Số dư", "So du", "Balance")),
                Status: SnapStatus(Get(r, "Tình trạng", "Trạng thái", "Status"))
            ));
        }
        return new NccExtractResult(
            Rows: rows,
            Source: source,
            RawRowCount: rawRows.Count,
            CleanedRowCount: rows.Count,
            LatencyMs: 0, TokensIn: 0, TokensOut: 0, Warning: null);
    }

    private List<NccRow> NormalizeRowList(List<Dictionary<string, string>> rawRows)
        => NormalizeRows(rawRows, "ai-text").Rows;

    // ──────────────────────────────────────────────────────────────────────────
    // Excel reader bằng DocumentFormat.OpenXml — chỉ đọc sheet đầu, row 1 là header.
    private List<Dictionary<string, string>> ReadExcelRows(Stream stream)
    {
        var result = new List<Dictionary<string, string>>();
        using var doc = SpreadsheetDocument.Open(stream, false);
        var wbPart = doc.WorkbookPart ?? throw new InvalidOperationException("Excel rỗng");
        var sheet = wbPart.Workbook.Descendants<Sheet>().FirstOrDefault()
            ?? throw new InvalidOperationException("Không tìm thấy sheet nào");
        var wsPart = (WorksheetPart)wbPart.GetPartById(sheet.Id!);
        var sst = wbPart.SharedStringTablePart?.SharedStringTable;

        var rows = wsPart.Worksheet.Descendants<Row>().ToList();
        if (rows.Count == 0) return result;

        var headers = rows[0].Elements<Cell>().Select(c => GetCellText(c, sst)).ToList();
        for (int i = 1; i < rows.Count; i++)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cells = rows[i].Elements<Cell>().ToList();
            for (int j = 0; j < cells.Count && j < headers.Count; j++)
            {
                dict[headers[j]] = GetCellText(cells[j], sst);
            }
            // Có ít nhất 1 cell không trống → row hợp lệ
            if (dict.Values.Any(v => !string.IsNullOrWhiteSpace(v))) result.Add(dict);
        }
        return result;
    }

    private static string GetCellText(Cell c, SharedStringTable? sst)
    {
        var raw = c.CellValue?.InnerText ?? "";
        if (c.DataType?.Value == CellValues.SharedString && int.TryParse(raw, out var idx) && sst != null)
        {
            var item = sst.ElementAtOrDefault(idx);
            return item?.InnerText ?? "";
        }
        if (c.DataType?.Value == CellValues.InlineString)
        {
            return c.InlineString?.Text?.Text ?? c.InnerText;
        }
        return raw;
    }

    // ──────────────────────────────────────────────────────────────────────────
    // CSV reader đơn giản — hỗ trợ "..." quoted field. Delimiter: tự detect , hoặc ;.
    private List<Dictionary<string, string>> ReadCsvRows(string text)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return new();

        var delim = lines[0].Count(c => c == ';') > lines[0].Count(c => c == ',') ? ';' : ',';
        var headers = SplitCsvLine(lines[0], delim);
        var result = new List<Dictionary<string, string>>();
        for (int i = 1; i < lines.Count; i++)
        {
            var fields = SplitCsvLine(lines[i], delim);
            if (fields.All(f => string.IsNullOrWhiteSpace(f))) continue;
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int j = 0; j < fields.Count && j < headers.Count; j++)
                dict[headers[j]] = fields[j];
            result.Add(dict);
        }
        return result;
    }

    private static List<string> SplitCsvLine(string line, char delim)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false;
        foreach (var ch in line)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (ch == delim && !inQuotes) { result.Add(sb.ToString().Trim()); sb.Clear(); continue; }
            sb.Append(ch);
        }
        result.Add(sb.ToString().Trim());
        return result;
    }

    // ──────────────────────────────────────────────────────────────────────────
    private static readonly string AI_SYSTEM =
        "Bạn là trợ lý chuẩn hoá danh sách NHÀ CUNG CẤP (NCC) cho công ty du lịch. " +
        "Đầu ra LUÔN là JSON array thuần (không markdown, không giải thích). " +
        "Mỗi item là 1 object có key tiếng Việt: " +
        "\"Mã NCC\", \"Tên NCC\", \"Số điện thoại\", \"Email\", \"Loại NCC\", " +
        "\"Số lượng\", \"Tổng mua\", \"Đã trả\", \"Thu hộ\", \"Còn nợ\", \"Số dư\", \"Tình trạng\". " +
        "Tài chính: ghi số nguyên không có ký tự đ. Mặc định 0 nếu không có. " +
        "\"Loại NCC\" PHẢI là 1 trong: " + string.Join(" / ", AllowedTypes) + ". " +
        "Đoán theo ngữ cảnh tên NCC (vd \"khách sạn Mường Thanh\" → Quỹ phòng, \"xe Ford Transit\" → Nhà xe). " +
        "\"Tình trạng\": Hoạt động | Ngừng (mặc định Hoạt động).";

    private static string BuildAiExtractPrompt(string text)
    {
        var truncated = text.Length > 12_000 ? text[..12_000] + "\n…[truncated]" : text;
        return "Bóc tách danh sách NCC từ NỘI DUNG dưới đây. Trả JSON array khớp schema đã nêu.\n\n" +
               "NỘI DUNG:\n" + truncated;
    }

    private static List<Dictionary<string, string>> ParseAiJson(string text)
    {
        var json = LooseJson.ExtractFirstArrayOrObject(text);
        if (string.IsNullOrEmpty(json)) return new();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var arr = root.ValueKind == JsonValueKind.Array ? root
                : (root.TryGetProperty("rows", out var r) ? r
                : (root.TryGetProperty("data", out var d) ? d : root));
            if (arr.ValueKind != JsonValueKind.Array) return new();
            var result = new List<Dictionary<string, string>>();
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in item.EnumerateObject())
                    dict[p.Name] = p.Value.ValueKind == JsonValueKind.String
                        ? p.Value.GetString() ?? ""
                        : p.Value.ToString();
                result.Add(dict);
            }
            return result;
        }
        catch (Exception ex)
        {
            return new();
        }
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Normalizers
    private static string? Get(Dictionary<string, string> d, params string[] keys)
    {
        foreach (var k in keys)
            if (d.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
        return null;
    }
    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? NormalizePhone(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        var digits = new string(p.Where(c => char.IsDigit(c) || c == '+').ToArray());
        return digits.Length > 0 ? digits : null;
    }

    private static int ToInt(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var clean = new string(s.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return int.TryParse(clean, out var n) ? n : 0;
    }

    private static decimal ToMoney(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var clean = s.Replace(".", "").Replace(",", "").Replace("đ", "").Replace("đ", "").Replace(" ", "").Replace("VND", "");
        return decimal.TryParse(clean, out var n) ? n : 0;
    }

    /// Snap "Loại NCC" free-text về 1 trong 10 enum. So sánh không phân biệt hoa thường/dấu.
    private static string? SnapType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var norm = StripDiacritics(raw).Trim().ToLowerInvariant();
        foreach (var t in AllowedTypes)
        {
            if (StripDiacritics(t).Equals(norm, StringComparison.OrdinalIgnoreCase)) return t;
        }
        // Substring fallback
        foreach (var t in AllowedTypes)
        {
            var tn = StripDiacritics(t).ToLowerInvariant();
            if (norm.Contains(tn) || tn.Contains(norm)) return t;
        }
        return raw.Trim();   // giữ nguyên nếu không snap được — user có thể sửa tay
    }

    private static string SnapStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Hoạt động";
        var norm = StripDiacritics(raw).Trim().ToLowerInvariant();
        if (norm.Contains("ngung") || norm.Contains("dung") || norm.Contains("stop") || norm.Contains("inactive"))
            return "Ngừng";
        return "Hoạt động";
    }

    private static string StripDiacritics(string s)
    {
        var formD = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in formD)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).Replace('đ', 'd').Replace('Đ', 'D');
    }
}
