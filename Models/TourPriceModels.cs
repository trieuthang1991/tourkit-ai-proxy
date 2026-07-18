namespace TourkitAiProxy.Models;

// KHÔNG khai DTO cho item upstream: workflow đọc thẳng JsonElement (giống ChatAgentService /
// TourKitNccClient hiện tại) → thiếu field thì bỏ dòng, không ném. Thêm 1 record chỉ để
// deserialize rồi map lại là dư thừa.

/// 1 dòng trong `dbo.TourPriceCatalog`.
/// `Description` BẮT BUỘC giữ — chứa điều kiện áp giá viết tay ("Mùa thấp điểm (5,6,9)",
/// "T6-T7", "Lễ 2/9", "Trên 10 phòng"). Cột có cấu trúc `ngay_di` chỉ 9,3% được dùng,
/// nên đây là NGUỒN DUY NHẤT về mùa vụ. Xem spec §2.4.
public record CatalogRow(
    string TenantId,
    int PricingId,
    int ProviderServiceId,
    int ProviderId,
    string ProviderName,
    string? ProviderCode,
    string? City,
    string CityNorm,
    int CategoryId,
    string? CategoryName,
    string? PriceName,
    string? Description,
    decimal ContractPrice,
    decimal PublicPrice,
    int? Stars
);

/// Nguồn giá khi retriever lấy ứng viên. Both = ưu tiên thật, lấp mẫu vào cặp (điểm đến+loại) còn thiếu.
public enum PriceSource { Sample, Real, Both }

/// 1 dòng ứng viên + nhãn nguồn ("real" hoặc "sample") để UI/AI biết dòng nào là mẫu.
public record PriceCandidate(CatalogRow Row, string Source);

/// Điều kiện lọc ứng viên. Field null = không lọc theo trục đó.
public record PriceQuery(string? CityNorm, int? CategoryId, decimal? MinPrice, decimal? MaxPrice);
