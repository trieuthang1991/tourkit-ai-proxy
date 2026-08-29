using Xunit;

namespace TourkitAiProxy.Tests.Chat;

/// <summary>
/// Canh việc worker của chat phải CHỜ schema dựng xong rồi mới chạy nhịp đầu.
///
/// <para><b>Vì sao.</b> <c>ChatDb.InitAsync</c> được gọi trong một <c>Task.Run</c> KHÔNG chờ ở
/// <c>Program.cs</c>, còn worker thì bắt đầu tick ngay khi máy chủ lên. Nên tồn tại một quãng mà
/// mã MỚI chạy trên schema CŨ — mọi truy vấn đụng cột mới đều hỏng, và chỉ để lại log.</para>
///
/// <para>Đã xảy ra thật 28/08/2026: worker gửi hỏi cột <c>send_after</c> 0,6 giây trước khi
/// <c>ALTER TABLE</c> thêm nó xong. Lần đó tự lành, nhưng độ dài quãng ấy là do CSDL quyết —
/// đường mạng chập thì <c>InitAsync</c> chờ hết hạn kết nối rồi NUỐT lỗi, và worker chạy suốt với
/// schema cũ. Đúng hình dạng sự cố ngừng nhận tin sáng cùng ngày.</para>
/// </summary>
public class ChatSchemaReadyTests
{
    [Theory]
    [InlineData("TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs")]
    [InlineData("TourkitAiProxy.Services/Chat/Inbox/ChatInboundWorker.cs")]
    [InlineData("TourkitAiProxy.Services/Chat/Inbox/ChatMediaBackfillWorker.cs")]
    public void Worker_chat_phai_cho_schema_truoc_nhip_dau(string tep)
        => Assert.Contains("DungSchema", ChatSchemaGuardTests.DocFile(tep));

    [Fact]
    public void Bao_dung_schema_phai_nam_trong_finally()
    {
        // Quên một nhánh — chưa khai chuỗi kết nối, hoặc InitAsync ném — là worker chờ mãi và cụm
        // chat đứng im KHÔNG dấu vết. Đặt trong finally là cách duy nhất phủ hết mọi đường ra.
        var src = ChatSchemaGuardTests.DocFile("TourkitAiProxy.Infrastructure/Chat/Inbox/ChatDb.cs");
        Assert.Contains("finally { _dungSchema.TrySetResult(); }", src);
    }

    [Fact]
    public void Cho_schema_phai_co_TRAN_thoi_gian()
    {
        // CSDL treo thì thà chạy tiếp rồi hỏng có log, còn hơn worker nằm im vô hạn mà không ai
        // biết vì sao hộp thư đứng — im lặng là kiểu hỏng khó lần ra nhất.
        foreach (var tep in new[]
        {
            "TourkitAiProxy.Services/Chat/Inbox/ChatOutboxWorker.cs",
            "TourkitAiProxy.Services/Chat/Inbox/ChatInboundWorker.cs",
            "TourkitAiProxy.Services/Chat/Inbox/ChatMediaBackfillWorker.cs",
        })
            Assert.Contains("WaitAsync(", ChatSchemaGuardTests.DocFile(tep));
    }
}
