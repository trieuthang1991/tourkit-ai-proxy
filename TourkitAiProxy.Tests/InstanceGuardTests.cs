using Xunit;
using TourkitAiProxy.Tests.Chat;

namespace TourkitAiProxy.Tests;

/// <summary>
/// Canh hai thứ khiến "nhiều tiến trình dùng chung một CSDL" trở nên nhìn thấy được thay vì phải
/// đoán. Cả hai đều sinh ra từ đúng một ngày trả giá (28/08/2026).
/// </summary>
public class InstanceGuardTests
{
    [Fact]
    public void Worker_chat_phai_co_co_bat_tat()
    {
        // Chạy hai instance trên cùng một CSDL thì hàng đợi vẫn an toàn (FOR UPDATE SKIP LOCKED),
        // nhưng hai con chạy HAI PHIÊN BẢN MÃ khác nhau là con cũ hỏng mọi nhịp trong im lặng.
        // Phải có đường tắt worker ở những con không được chọn.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("Workflows:RunChatWorkers", src);
    }

    [Fact]
    public void Co_worker_chat_phai_mac_dinh_BAT()
    {
        // Ngược hướng an toàn với Workflows:RunScheduler, và cố ý: quên khai scheduler thì bản tin
        // sáng chậm một nhịp; quên khai cờ này mà mặc định TẮT thì hộp thư ngừng nhận VÀ ngừng gửi
        // trên MỌI máy — khách nhắn vào hư không, không lỗi nào hiện lên.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("GetValue(\"Workflows:RunChatWorkers\", true)", src);
    }

    [Fact]
    public void Worker_ANH_phai_tach_khoi_worker_TIN_NHAN()
    {
        // Hai viec khac nhip han: tin nhan phai di trong vai giay va rat nhe; anh thi khong ai cho
        // nhung moi tep la mot luot tai mang cong mot luot nen ton CPU. Gop chung mot co thi muon
        // doi viec nang sang may khac la doi luon ca viec gap.
        var src = ChatSchemaGuardTests.DocFile(
            "TourkitAiProxy.Services/Bootstrap/WebFeatureRegistration.cs");
        Assert.Contains("Workflows:RunChatMediaWorker", src);
        var i = src.IndexOf("Workflows:RunChatWorkers", System.StringComparison.Ordinal);
        var j = src.IndexOf("Workflows:RunChatMediaWorker", System.StringComparison.Ordinal);
        Assert.True(i > 0 && j > i, "Hai co phai la hai lenh if rieng");
    }

    [Fact]
    public void Tien_trinh_phai_tu_khai_luc_khoi_dong()
    {
        // Không có dòng này thì câu hỏi "con nào đang chạy worker" chỉ trả lời được bằng cách đoán.
        var src = ChatSchemaGuardTests.DocFile("Program.cs");
        Assert.Contains("InstanceInfo.MotDong", src);
    }

    [Fact]
    public void Deploy_KHONG_duoc_xoa_tep_nhan_vien_da_gui()
    {
        // robocopy chạy /MIR: thứ gì không có trong bản publish thì bị XOÁ ở máy đích. Thư mục
        // chat-uploads chứa tệp nhân viên gửi khi Storage:Provider=local, và đường dẫn của chúng
        // đã ghi vĩnh viễn vào chat_messages — xoá là mọi tệp thành liên kết gãy, không dựng lại
        // được. Đây là loại mất dữ liệu không ai phát hiện cho tới khi khách hỏi lại tệp cũ.
        var src = ChatSchemaGuardTests.DocFile("scripts/deploy-iis.ps1");
        var i = src.IndexOf("$ExcludeDirs", System.StringComparison.Ordinal);
        Assert.True(i > 0, "Không thấy $ExcludeDirs trong deploy-iis.ps1");
        var khoi = src.Substring(i, System.Math.Min(600, src.Length - i));
        Assert.Contains("chat-uploads", khoi);
    }
}
