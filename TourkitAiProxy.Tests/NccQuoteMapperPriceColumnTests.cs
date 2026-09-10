using System.Linq;
using System.Text.Json;
using TourkitAiProxy.Domain.NccImport;
using Xunit;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Phân loại cột của bảng báo giá NCC (sheet bug dòng 113 — "Import NCC không có giá tiền mà hệ
/// thống tự bắt số lượng, giá bán").
///
/// <para>Bản cũ coi MỌI cột nhiều số là cột giá, nên STT / Số lượng / Số đêm đều bị dựng thành
/// dòng giá, và báo giá không có cột tiền nào vẫn đẻ ra giá từ hư không.</para>
/// </summary>
public class NccQuoteMapperPriceColumnTests
{
    private static System.Collections.Generic.List<ProviderPricePayload> Prices(string quoteJson)
        => NccQuoteMapper.ToCreateProvider(JsonDocument.Parse(quoteJson).RootElement, 10, null).Prices;

    [Fact]
    public void Cot_so_luong_khong_bi_hieu_thanh_gia()
    {
        var prices = Prices("""
            { "tables": [ { "title": "Bảng giá phòng",
                "columns": ["Loại phòng", "Số lượng", "Giá NET"],
                "rows": [ ["Deluxe", 2, 1200000] ] } ] }
            """);

        var row = Assert.Single(prices);
        Assert.Equal(1200000m, row.ContractPrice);   // chỉ cột "Giá NET" thành tiền
        Assert.Equal(2m, row.Quantity);              // cột "Số lượng" điền đúng chỗ của nó
    }

    [Fact]
    public void Cot_stt_khong_bi_hieu_thanh_gia()
    {
        var prices = Prices("""
            { "tables": [ { "columns": ["STT", "Dịch vụ", "Đơn giá"],
                "rows": [ [1, "Xe 16 chỗ", 2500000], [2, "Xe 29 chỗ", 3500000] ] } ] }
            """);

        Assert.Equal(2, prices.Count);
        Assert.Equal(new[] { 2500000m, 3500000m }, prices.Select(p => p.ContractPrice!.Value));
    }

    [Fact]
    public void Bang_khong_co_cot_tien_thi_giu_ten_dich_vu_va_de_trong_gia()
    {
        // Đúng ca người kiểm thử gặp: bảng danh mục, chưa có giá.
        var prices = Prices("""
            { "tables": [ { "title": "Danh mục dịch vụ",
                "columns": ["STT", "Tên dịch vụ", "Số lượng"],
                "rows": [ [1, "Thuê xe đưa đón", 3], [2, "Hướng dẫn viên", 1] ] } ] }
            """);

        Assert.Equal(2, prices.Count);
        Assert.All(prices, p => Assert.Null(p.ContractPrice));   // KHÔNG bịa ra tiền
        Assert.Contains(prices, p => p.PriceName.Contains("Thuê xe đưa đón"));
        Assert.Equal(3m, prices.First(p => p.PriceName.Contains("Thuê xe")).Quantity);
    }

    [Fact]
    public void Cot_khong_co_tieu_de_van_nhan_ra_tien_theo_do_lon()
    {
        // Bảng thiếu tiêu đề → xét giá trị: số nhỏ là số lượng, số lớn là tiền.
        var prices = Prices("""
            { "tables": [ { "columns": ["", "", ""],
                "rows": [ ["Phòng đôi", 2, 850000] ] } ] }
            """);

        var row = Assert.Single(prices);
        Assert.Equal(850000m, row.ContractPrice);
    }

    [Fact]
    public void Nhieu_cot_tien_thi_moi_cot_mot_dong_gia_kem_nhan_cot()
    {
        var prices = Prices("""
            { "tables": [ { "columns": ["Loại phòng", "Giá NET", "Giá bán"],
                "rows": [ ["Superior", 900000, 1100000] ] } ] }
            """);

        Assert.Equal(2, prices.Count);
        Assert.Contains(prices, p => p.PriceName.Contains("Giá NET"));
        Assert.Contains(prices, p => p.PriceName.Contains("Giá bán"));
    }
}
