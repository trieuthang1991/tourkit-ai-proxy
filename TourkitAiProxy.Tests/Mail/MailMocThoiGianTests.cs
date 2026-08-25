using TourkitAiProxy.Infrastructure.Mail;
using Xunit;

namespace TourkitAiProxy.Tests.Mail;

/// <summary>
/// Chốt chặn cho hai vế của luật ngày giờ trong <c>docs/datetime-convention.md</c> — cả hai đều đã
/// hỏng thật trong <see cref="MailRepository"/>, và <b>chúng che nhau</b> nên nằm im rất lâu:
/// lưu dư 7 tiếng (giờ máy chủ), rồi đọc ra thiếu <c>Z</c> nên trình duyệt lại hiểu là giờ địa
/// phương — cộng lại thành ra hiển thị ĐÚNG trên máy ở Việt Nam.
///
/// <para>E2E bắt được 25/08/2026 vì nó soi hậu tố <c>Z</c> thay vì nhìn màn hình. Bài học: hai lỗi
/// ngược dấu triệt tiêu nhau thì kiểm bằng mắt không bao giờ thấy — phải kiểm chính cái BẤT BIẾN
/// (mốc thời gian trả ra luôn là UTC và luôn có <c>Z</c>).</para>
/// </summary>
public class MailMocThoiGianTests
{
    // ── Ghi: chuỗi ISO → DateTime UTC ────────────────────────────────────────

    [Fact]
    public void Chuoi_co_Z_giu_nguyen_gio_UTC()
    {
        var dt = MailRepository.DocMocUtc("2026-08-25T14:08:09.0000000Z");

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(14, dt.Hour);   // KHÔNG được thành 21 (giờ máy chủ +7)
        Assert.Equal(new DateTime(2026, 8, 25, 14, 8, 9, DateTimeKind.Utc), dt);
    }

    [Fact]
    public void Chuoi_khong_co_Z_duoc_coi_la_UTC()
    {
        // AssumeUniversal: chuỗi trần thì hiểu là UTC, không phải giờ máy chủ.
        var dt = MailRepository.DocMocUtc("2026-08-25T14:08:09");

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(14, dt.Hour);
    }

    [Fact]
    public void Chuoi_co_lech_mui_gio_duoc_quy_ve_UTC()
    {
        var dt = MailRepository.DocMocUtc("2026-08-25T21:08:09+07:00");

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.Equal(14, dt.Hour);
    }

    [Fact]
    public void Chuoi_hong_thi_lay_gio_hien_tai_UTC()
    {
        // Thư không có ngày hợp lệ vẫn phải lưu được — nhưng lưu bằng mốc UTC, không phải giờ máy.
        var dt = MailRepository.DocMocUtc("khong-phai-ngay");

        Assert.Equal(DateTimeKind.Utc, dt.Kind);
        Assert.True((DateTime.UtcNow - dt).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void Chuoi_rong_hoac_null_khong_lam_no()
    {
        Assert.Equal(DateTimeKind.Utc, MailRepository.DocMocUtc(null).Kind);
        Assert.Equal(DateTimeKind.Utc, MailRepository.DocMocUtc("").Kind);
    }

    // ── Đọc: DateTime từ CSDL → chuỗi ISO ────────────────────────────────────

    [Fact]
    public void Moc_doc_tu_CSDL_luon_ra_chuoi_co_Z()
    {
        // Dapper trả DATETIME2 ra Kind=Unspecified — đúng cái đã làm mất chữ 'Z'.
        var tuDapper = new DateTime(2026, 8, 25, 14, 8, 9, DateTimeKind.Unspecified);

        var s = MailRepository.MocUtcRaChuoi(tuDapper);

        Assert.EndsWith("Z", s);
        Assert.StartsWith("2026-08-25T14:08:09", s);
    }

    [Fact]
    public void Moc_da_la_UTC_thi_khong_bi_doi_gio()
    {
        var s = MailRepository.MocUtcRaChuoi(new DateTime(2026, 8, 25, 14, 8, 9, DateTimeKind.Utc));

        Assert.EndsWith("Z", s);
        Assert.StartsWith("2026-08-25T14:08:09", s);
    }

    // ── Vòng tròn: ghi rồi đọc phải ra đúng mốc ban đầu ──────────────────────

    [Fact]
    public void Ghi_roi_doc_lai_khong_troi_gio()
    {
        // Đây là bất biến thật sự đáng giữ. Trước khi sửa, vòng này trôi +7 tiếng mỗi lần
        // đi qua — mà vẫn hiển thị đúng trên máy Việt Nam nên không ai thấy.
        const string goc = "2026-08-25T14:08:09.0000000Z";

        var s = MailRepository.MocUtcRaChuoi(MailRepository.DocMocUtc(goc));

        Assert.Equal(DateTime.Parse(goc).ToUniversalTime(), DateTime.Parse(s).ToUniversalTime());
        Assert.EndsWith("Z", s);
    }
}
