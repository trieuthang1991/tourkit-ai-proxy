# Kết nối AI (MCP server)

> Giai đoạn 1: **chỉ đọc**. Nhân viên cắm Claude Code / Cursor / Claude Desktop vào TourKit để hỏi số liệu
> CRM ngay trong công cụ AI của mình. Cờ `Features:Mcp` (mặc định TẮT).
> Kiến trúc chung và luật đặt file: [ARCHITECTURE.md](../ARCHITECTURE.md).

## Là gì

Một máy chủ MCP (Model Context Protocol) ở `POST /mcp` trong chính tiến trình proxy. Công cụ AI ngoài
(Claude Code, Cursor, Claude Desktop) gọi vào đây, thấy **20 tool đọc số liệu CRM** và gọi chúng để lấy số thật.
Không có hành động ghi nào ở giai đoạn này.

## Nhân viên kết nối thế nào

1. Vào menu **Tích hợp → Kết nối AI** (`/mcp-connect`). Mục này không đòi quyền quản trị, chỉ cần cờ bật.
2. Đặt tên thiết bị, bấm **Tạo khoá**. Khoá dạng `tkmcp_…` hiện **một lần** — chép ngay.
3. Dán một trong ba mẫu (trang tự điền khoá và địa chỉ của chính nơi đang mở):

```
# Claude Code
claude mcp add --transport http tourkit https://tourkit-ai.tourkit.vn/mcp --header "Authorization: Bearer <khoá>"

# Cursor — .cursor/mcp.json
{"mcpServers":{"tourkit":{"url":"https://tourkit-ai.tourkit.vn/mcp","headers":{"Authorization":"Bearer <khoá>"}}}}

# Claude Desktop (qua mcp-remote)
npx mcp-remote https://tourkit-ai.tourkit.vn/mcp --header "Authorization: Bearer <khoá>"
```

Thu hồi: nút **Thu hồi** trên cùng trang, bấm hai lần (lần một đổi chữ thành "Bấm lần nữa để thu hồi").

Trang có dòng cảnh báo: dữ liệu người dùng hỏi sẽ được gửi tới dịch vụ AI họ đang dùng.

## Tool từ đâu ra

20 tool **sinh từ `ChatTools`** — cùng nguồn với Trợ lý số liệu ([assistant.md](assistant.md)), không khai
song song. Thêm/sửa tool ở `ChatTools` thì MCP tự theo. Mỗi tool gọi API CRM `/api/ai/*` bằng
**phiên TourKit của chính người dùng**, nên quyền xem dữ liệu đúng như khi họ dùng trợ lý.

## Xác thực

- Khoá `tkmcp_…` gắn với một người dùng. Máy chủ chỉ lưu bản **băm** (`dbo.McpTokens`), không lưu khoá thô.
- Mỗi lời gọi: khoá → tra người dùng → phiên TourKit (`TkSessionStore`).
- Thu hồi qua `DELETE /api/v1/mcp/tokens/{id}`. Kết quả tra khoá được **đệm 60 giây**; khi chạy nhiều
  instance, thu hồi có hiệu lực trễ tối đa 60 giây trên máy khác.
- Tối đa 20 khoá còn hiệu lực mỗi người; nhãn bắt buộc, ≤100 ký tự.

## Giới hạn

- **Tần suất:** 60 lần gọi tool mỗi phút mỗi khoá.
- **Kết quả:** tối đa 20 dòng mỗi lần, khoảng 30KB, `pageSize` ≤ 50 — để AI ngoài không nuốt cả bảng.
- **Đệm dữ liệu CRM theo từng người dùng** (không chung cả công ty), nên hai nhân viên quyền khác nhau không lẫn số liệu.

## Cờ

| Cờ | Mặc định | Ý nghĩa |
|---|---|---|
| `Features:Mcp` | tắt | Bật `/mcp`, `/api/v1/mcp/tokens*` và mục menu. Tắt: route trả trang "chưa mở", menu ẩn |
| `Features:McpActions` | tắt | Giai đoạn 1b (hành động ghi). Chưa dùng ở giai đoạn 1 |

## Bật trên prod

1. Kiểm `TourKit:BaseUrl` của site prod trỏ tới máy chủ **có** `/api/ai/*`. Đã kiểm 10/11/2026: API prod
   `mobile-api.tourkit.vn` **đã có** route `/api/ai/*` (401 khi không xác thực, route lạ trả 404). Còn phải xác nhận
   site prod đang dùng đúng BaseUrl đó.
2. Rồi mới đặt `Features:Mcp=true` và Recycle app pool.

## Giới hạn đã biết

- Một số báo cáo trả chậm ở lần gọi đầu (vd danh sách khách theo `careFilter` khoảng 12 giây).
- Chỉ xác thực bằng khoá Bearer. **OAuth** (để dùng với Claude.ai web / ChatGPT) là giai đoạn 2.
- Chưa có hành động ghi (giai đoạn 1b, sau cờ `Features:McpActions`).
