using TourkitAiProxy.Services.Chat;
using Xunit;

/// <summary>
/// Test cho <see cref="ActionResolutionMemory"/> — bộ nhớ resolve theo phiên, fix bug
/// "đã chọn đối tượng ở clarify rồi, bổ sung thông tin qua chat lại BẮT CHỌN LẠI".
///
/// Mỗi test dưới đây tương ứng 1 TÌNH HUỐNG THỰC TẾ có thể gây lỗi (đúng/sai hành vi).
/// </summary>
public class ActionResolutionMemoryTests
{
    // ── CASE 1 (lõi bug): user đã chọn "Phong" → id, lượt sau vẫn hỏi "Phong" thì phải RA ĐÚNG id,
    //    KHÔNG hỏi lại. Đây là kịch bản "chọn rồi bổ sung thông tin".
    [Fact]
    public void Recall_returns_id_after_Remember_same_session_kind_name()
    {
        var m = new ActionResolutionMemory();
        m.Remember("sess-1", "staff", "Phong", 123);

        Assert.Equal(123, m.Recall("sess-1", "staff", "Phong"));
    }

    // ── CASE 2: chưa từng chọn → Recall null → hệ thống vẫn clarify bình thường (không "nhớ nhầm").
    [Fact]
    public void Recall_returns_null_when_never_remembered()
    {
        var m = new ActionResolutionMemory();
        Assert.Null(m.Recall("sess-1", "staff", "Phong"));
    }

    // ── CASE 3: cách ly theo PHIÊN — phiên A chọn Phong=1, phiên B hỏi Phong thì KHÔNG được dùng
    //    lựa chọn của phiên A (mỗi người dùng/phiên độc lập).
    [Fact]
    public void Recall_is_isolated_per_session()
    {
        var m = new ActionResolutionMemory();
        m.Remember("sess-A", "staff", "Phong", 1);

        Assert.Equal(1, m.Recall("sess-A", "staff", "Phong"));
        Assert.Null(m.Recall("sess-B", "staff", "Phong"));   // phiên khác → không thấy
    }

    // ── CASE 4: cách ly theo LOẠI — nhớ nhân viên tên "An" KHÔNG được lẫn sang khách hàng tên "An".
    [Fact]
    public void Recall_is_isolated_per_kind()
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", "An", 10);

        Assert.Equal(10, m.Recall("s", "staff", "An"));
        Assert.Null(m.Recall("s", "customer", "An"));   // khác loại → không thấy
        Assert.Null(m.Recall("s", "deal", "An"));
    }

    // ── CASE 5: KHÔNG phân biệt HOA/THƯỜNG — user gõ "phong" / "PHONG" vẫn khớp "Phong" đã chọn.
    [Theory]
    [InlineData("phong")]
    [InlineData("PHONG")]
    [InlineData("Phong")]
    [InlineData("  Phong  ")]   // khoảng trắng thừa
    public void Recall_ignores_case_and_surrounding_spaces(string queryName)
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", "Phong", 7);

        Assert.Equal(7, m.Recall("s", "staff", queryName));
    }

    // ── CASE 6: KHÔNG phân biệt DẤU tiếng Việt + đ→d — "Đà Nẵng"/"da nang"/"Da Nang" cùng khóa.
    //    (Quan trọng vì model đôi khi bỏ dấu, hoặc user gõ không dấu.)
    [Theory]
    [InlineData("Đà Nẵng", "da nang")]
    [InlineData("Nguyễn Văn An", "nguyen van an")]
    [InlineData("Trần Đức", "tran duc")]
    public void Recall_ignores_vietnamese_diacritics(string remembered, string queried)
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "customer", remembered, 42);

        Assert.Equal(42, m.Recall("s", "customer", queried));
    }

    // ── CASE 7: khoảng trắng GIỮA chữ được gộp — " Phong   Travel " ≡ "phong travel".
    [Fact]
    public void Recall_collapses_inner_whitespace()
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", " Phong   Travel ", 9);

        Assert.Equal(9, m.Recall("s", "staff", "phong travel"));
    }

    // ── CASE 8: user ĐỔI Ý chọn lại — nhớ lần sau ghi đè lần trước (dùng id mới nhất).
    [Fact]
    public void Remember_overwrites_previous_choice()
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", "Phong", 1);
        m.Remember("s", "staff", "Phong", 2);

        Assert.Equal(2, m.Recall("s", "staff", "Phong"));
    }

    // ── CASE 9: tên rỗng/null/toàn khoảng trắng → KHÔNG lưu, Recall null, KHÔNG crash.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_name_is_not_stored_and_recalls_null(string? name)
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", name, 5);          // không lưu
        Assert.Null(m.Recall("s", "staff", name));  // không crash, trả null
    }

    // ── CASE 10: thiếu sessionId hoặc kind → không lưu / trả null (không rơi vào key rác).
    [Theory]
    [InlineData(null, "staff")]
    [InlineData("", "staff")]
    [InlineData("s", null)]
    [InlineData("s", "")]
    public void Missing_session_or_kind_is_ignored(string? sid, string? kind)
    {
        var m = new ActionResolutionMemory();
        m.Remember(sid, kind, "Phong", 5);
        Assert.Null(m.Recall(sid, kind, "Phong"));
    }

    // ── CASE 11: nhiều tên khác nhau trong CÙNG phiên không đè lẫn nhau.
    [Fact]
    public void Different_names_do_not_collide()
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "staff", "Phong", 1);
        m.Remember("s", "staff", "Hoa", 2);
        m.Remember("s", "staff", "Trang", 3);

        Assert.Equal(1, m.Recall("s", "staff", "Phong"));
        Assert.Equal(2, m.Recall("s", "staff", "Hoa"));
        Assert.Equal(3, m.Recall("s", "staff", "Trang"));
        Assert.Null(m.Recall("s", "staff", "Dũng"));   // tên chưa chọn → null
    }

    // ── CASE 12: khách nhận diện bằng SĐT (user hay nói SĐT thay tên) — nhớ theo SĐT cũng khớp lại.
    [Fact]
    public void Recall_works_for_phone_as_identifier()
    {
        var m = new ActionResolutionMemory();
        m.Remember("s", "customer", "0982385108", 555);

        Assert.Equal(555, m.Recall("s", "customer", "0982385108"));
        Assert.Null(m.Recall("s", "customer", "0900000000"));   // SĐT khác → null
    }
}
