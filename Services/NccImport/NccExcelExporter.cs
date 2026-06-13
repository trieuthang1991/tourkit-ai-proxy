using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using TourkitAiProxy.Models;

namespace TourkitAiProxy.Services.NccImport;

/// Build file_import_ncc.xlsx CHUẨN từ list NccRow để user tải về import lên hệ thống.
/// Header khớp 1-1 với template gốc (13 cột).
public static class NccExcelExporter
{
    private static readonly string[] Headers = new[]
    {
        "STT", "Mã NCC", "Tên NCC", "Số điện thoại", "Email", "Loại NCC",
        "Số lượng", "Tổng mua", "Đã trả", "Thu hộ", "Còn nợ", "Số dư", "Tình trạng"
    };

    public static byte[] BuildXlsx(IReadOnlyList<NccRow> rows)
    {
        using var ms = new MemoryStream();
        using (var doc = SpreadsheetDocument.Create(ms, SpreadsheetDocumentType.Workbook))
        {
            var wbPart = doc.AddWorkbookPart();
            wbPart.Workbook = new Workbook();

            var wsPart = wbPart.AddNewPart<WorksheetPart>();
            var sheetData = new SheetData();
            wsPart.Worksheet = new Worksheet(sheetData);

            var sheets = wbPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet
            {
                Id = wbPart.GetIdOfPart(wsPart),
                SheetId = 1,
                Name = "NCC"
            });

            // Row 1: headers
            sheetData.AppendChild(BuildRow(1, Headers.Select(h => (object)h).ToArray()));

            // Data rows
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                var cells = new object[]
                {
                    i + 1,
                    r.Code ?? "",
                    r.Name ?? "",
                    r.Phone ?? "",
                    r.Email ?? "",
                    r.Type ?? "",
                    r.Quantity,
                    r.TotalBuy, r.Paid, r.Collected, r.Owed, r.Balance,
                    r.Status ?? "Hoạt động"
                };
                sheetData.AppendChild(BuildRow((uint)(i + 2), cells));
            }

            wbPart.Workbook.Save();
        }
        return ms.ToArray();
    }

    private static Row BuildRow(uint index, object[] values)
    {
        var row = new Row { RowIndex = index };
        foreach (var v in values)
        {
            var cell = new Cell();
            if (v is int i) { cell.DataType = CellValues.Number; cell.CellValue = new CellValue(i); }
            else if (v is decimal d) { cell.DataType = CellValues.Number; cell.CellValue = new CellValue(d); }
            else
            {
                cell.DataType = CellValues.String;
                cell.CellValue = new CellValue(v?.ToString() ?? "");
            }
            row.Append(cell);
        }
        return row;
    }
}
