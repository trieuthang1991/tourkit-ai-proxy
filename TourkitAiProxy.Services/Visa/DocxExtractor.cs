using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using System.Text;

namespace TourkitAiProxy.Services.Visa;

/// Trích text thuần từ file .docx (Word 2007+). KHÔNG hỗ trợ .doc (binary cổ).
/// Bỏ ảnh, header/footer phức tạp, chỉ giữ paragraph text + bảng — đủ cho AI đọc nội dung
/// đơn xin nghỉ phép / hợp đồng lao động / sao kê dạng văn bản.
public static class DocxExtractor
{
    /// Trích text từ byte array DOCX. Trả chuỗi text hoặc ném IOException nếu không phải DOCX hợp lệ.
    public static string ExtractText(byte[] bytes)
    {
        using var ms = new MemoryStream(bytes);
        using var doc = WordprocessingDocument.Open(ms, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        if (body is null) return string.Empty;

        var sb = new StringBuilder();
        ExtractInto(body, sb);
        return sb.ToString().TrimEnd();
    }

    private static void ExtractInto(OpenXmlElement el, StringBuilder sb)
    {
        foreach (var child in el.ChildElements)
        {
            switch (child)
            {
                case Paragraph p:
                    var line = string.Join("", p.Descendants<Text>().Select(t => t.Text));
                    if (!string.IsNullOrWhiteSpace(line)) { sb.AppendLine(line); }
                    else if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.AppendLine();
                    break;
                case Table tbl:
                    foreach (var row in tbl.Elements<TableRow>())
                    {
                        var cells = row.Elements<TableCell>().Select(c =>
                            string.Join(" ", c.Descendants<Text>().Select(t => t.Text)).Trim());
                        sb.AppendLine(string.Join(" | ", cells));
                    }
                    break;
                default:
                    if (child.HasChildren) ExtractInto(child, sb);
                    break;
            }
        }
    }
}
