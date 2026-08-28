using Microsoft.Extensions.Configuration;
using TourkitAiProxy.Services.Bootstrap;
using TourkitAiProxy.Services.Storage;
using TourkitAiProxy.Services.Workflow;

namespace TourkitAiProxy.Endpoints;

public static class SystemEndpoints
{
    public static IEndpointRouteBuilder MapSystemEndpoints(this IEndpointRouteBuilder routes)
    {
        // Bản TỰ KHAI của chính tiến trình này. Có nó thì câu hỏi "con nào đang chạy worker"
        // trả lời được bằng hai lượt curl, thay vì đoán qua log hoặc dò trong CSDL.
        //
        // Đang chạy nhiều instance trên cùng một CSDL thì đây là chỗ đầu tiên phải nhìn: hai
        // con cùng bật worker mà KHÁC phiên bản mã là con cũ hỏng mọi nhịp trong im lặng.
        routes.MapGet("/healthz", (IConfiguration cfg, IChatFileStorage kho) => Results.Json(new
        {
            ok        = true,
            service   = "Tourkit AI Proxy",
            version   = "v1.0.1",
            deployedVia = "github-actions",
            instance  = InstanceInfo.Doc(cfg, kho),
            endpoints = new[]
            {
                "GET  /api/v1/providers",
                "GET  /api/v1/models",
                "GET  /api/v1/usage",
                "POST /api/v1/completions",
                "POST /api/v1/completions/stream",
                "GET  /api/v1/workflow-traces"
            }
        }));

        // Xem lại workflow traces đã log (data/workflow-traces.jsonl).
        // Query: ?days=7 ?workflow=CustomerReview ?limit=100
        var v1 = routes.MapGroup("/api/v1");

        // Tính năng nào đang mở — để giao diện biết mà ẩn menu/nút của phần chưa ra mắt.
        // KHÔNG cần đăng nhập: chỉ nói tính năng nào bật, không lộ cấu hình hay dữ liệu nào.
        // Đây là cờ RA MẮT, khác phân quyền (/api/v1/permissions): tắt là tắt cho tất cả.
        v1.MapGet("/features", (IConfiguration cfg,
            Services.Chat.Inbox.ChatEventBus chatBus) => Results.Json(new
        {
            digest        = Services.Bootstrap.FeatureFlags.Digest(cfg),
            tourReadiness = Services.Bootstrap.FeatureFlags.TourReadiness(cfg),
            meetingBrief  = Services.Bootstrap.FeatureFlags.MeetingBrief(cfg),
            anomalyWatchdog = Services.Bootstrap.FeatureFlags.AnomalyWatchdog(cfg),
            autoCare        = Services.Bootstrap.FeatureFlags.AutoCare(cfg),
            chat            = Services.Bootstrap.FeatureFlags.Chat(cfg),
            // Hộp thư chat có tin vào ĐẨY tới được không. false = bus chỉ thấy sự kiện của chính
            // instance mình (chưa cắm Redis), nên giao diện phải giữ đường lùi hỏi lại định kỳ.
            // Nói ra chứ không im lặng chạy chế độ kém hơn: triệu chứng "thỉnh thoảng tin mới
            // không hiện" cực khó lần nếu giao diện tưởng đẩy luôn đủ.
            chatRealtime    = chatBus.MultiInstance,
        }));
        v1.MapGet("/workflow-traces", (WorkflowTraceLog log, int? days, string? workflow, int? limit) =>
        {
            var entries = log.Read(days ?? 7, workflow, limit ?? 100);
            var summary = log.Summary(days ?? 7);
            return Results.Json(new
            {
                days      = days ?? 7,
                workflow,
                count     = entries.Count,
                summary   = summary.Select(kv => new {
                    workflow = kv.Key, count = kv.Value.Count,
                    maxMs = kv.Value.MaxMs, minMs = kv.Value.MinMs
                }).ToArray(),
                entries
            });
        });
        return routes;
    }
}
