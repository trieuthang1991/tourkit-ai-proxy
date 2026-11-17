# Quy trình làm việc với Multica

> Chốt ngày 07/09/2026. Multica (`multica.ai`) là bảng theo dõi công việc của đội — **không phải**
> nơi chạy agent. Lý do ở mục 6.

---

## 1. Ba chỗ, ba vai — đừng để chồng nhau

Thêm một công cụ theo dõi mà không chia vai rõ thì hậu quả không phải "có thêm chỗ tra", mà là
**không chỗ nào đúng**: mỗi nơi đúng một nửa, và người đọc phải hỏi lại người viết.

| Chỗ | Vai | Ai viết vào |
|---|---|---|
| **Google Sheet** (`Bug App`, `Chat đa kênh`…) | Nơi **ĐẶT VẤN ĐỀ**. Nghiệp vụ mô tả cái đang sai và cái mong muốn | Đội nghiệp vụ |
| **Multica** | Nơi **VIỆC SỐNG**. Chỗ DUY NHẤT trả lời "đang làm gì, ai làm, tới đâu" | Chủ dự án + Claude |
| **`docs/superpowers/specs/` · `plans/` · sổ SDD** | Hồ sơ kỹ thuật của MỘT đợt: vì sao thiết kế vậy, bẫy ở đâu | Claude |

⚠️ **Sheet không bỏ.** Đội nghiệp vụ quen viết ở đó, và bắt họ đổi chỗ là hỏng nguồn vào — sẽ có
người ngừng báo lỗi vì ngại công cụ mới. Nhưng từ nay Sheet **không còn là nơi theo dõi tiến độ**;
một dòng Sheet được nhận vào thì đời sống của nó chuyển sang Multica.

⚠️ **Sổ SDD không phải chỗ theo dõi.** Nó là bản ghi để Claude sống sót qua việc mất ngữ cảnh giữa
chừng. Người khác không đọc nó, và không nên phải đọc.

---

## 2. Có vấn đề mới thì làm gì

**Bước 1 — Vào Multica ngay, thô cũng được.** Trạng thái `backlog`. Một câu mô tả là đủ; không cần
đợi hiểu rõ mới ghi, vì thứ hay mất nhất là cái chưa kịp ghi.

- Từ Sheet → Claude chép sang, **ghi rõ dòng nguồn** (tên tab + nội dung gốc) để sau còn đối chiếu.
- Chủ dự án nghĩ ra → gõ thẳng vào Multica, kể cả từ điện thoại.

**Bước 2 — Phân loại bằng ĐÚNG MỘT câu hỏi:** *chờ được tới khi đợt đang chạy xong không?*

| Trả lời | Làm gì |
|---|---|
| **Chờ được** | Để `backlog`, gắn vào dự án phù hợp. Hết đợt mới xét. Không bàn thêm lúc này |
| **Không chờ được** — khách đang hỏng, dữ liệu sai, rò rỉ | Chuyển `todo`, và đợt đang chạy **dừng ở ranh giới task** |

⚠️ **Dừng ở RANH GIỚI TASK, không dừng giữa task.** Mỗi task kết bằng một commit đã qua review; cắt
giữa chừng là để lại mã chưa ai soát trên nhánh, và tuần sau không ai nhớ nó dở tới đâu. Ngày
07/09/2026 một agent bị dừng giữa task — may là nó chưa commit gì nên mất trắng chứ không để lại
rác; lần sau chưa chắc may vậy.

**Bước 3 — Việc gấp làm trên nhánh riêng tách từ `dev`**, không nhét vào nhánh của đợt đang chạy.
Nhét chung thì lúc cần phát hành riêng phần gấp sẽ phải gỡ commit, mà gỡ commit giữa một đợt đã
review là cách nhanh nhất để mất một bản sửa.

---

## 3. Vòng đời một việc, và trạng thái nghĩa là gì

```
backlog  →  todo  →  in_progress  →  in_review  →  done
```

| Trạng thái | Nghĩa CHÍNH XÁC |
|---|---|
| `backlog` | Đã ghi nhận, chưa quyết định làm |
| `todo` | Đã quyết làm, chưa bắt đầu |
| `in_progress` | Đang có người/agent làm **ngay lúc này** |
| `in_review` | Mã đã xong, đang soát |
| `done` | Review sạch **và** đã commit |

⚠️ **Trạng thái phải phản ánh thật, không cập nhật cho đẹp.** Một bảng mà `in_progress` nghĩa là
"chắc đang làm gì đó" thì không dùng được để quyết định gì cả.

**Mỗi việc xong, Claude ghi MỘT bình luận vào issue:** commit nào · review bắt được lỗi gì · còn nợ
gì. Đó là thứ chủ dự án đọc thay vì phải hỏi lại.

### Cửa vào `in_progress`: chạy `codegraph impact` TRƯỚC

Chủ dự án chốt 07/09/2026. `CLAUDE.md` đã bắt buộc điều này *trước khi sửa một hàm*; luật ở đây
sớm hơn một nhịp — **trước khi VIẾT BRIEF**, lúc còn đang mô tả việc.

```bash
codegraph impact <Symbol>     # cho MỌI symbol mà brief bảo agent sửa
```

Bán kính trả về **dán thẳng vào brief**. Agent không phải tự dò, và không sót chỗ gọi.

⚠️ **Lọc dương tính giả bằng grep trước khi tin.** Đo thật ngày 07/09: `codegraph impact
ChatAuditRow` trả 5 file, **3 file là giả** (`CeoBriefWorkflow`, `SaleBriefWorkflow`,
`TikTokChatAdapter` — grep xác nhận không file nào nhắc tên đó). Công thức: **codegraph để LẤY
danh sách, grep để LỌC**. Đưa danh sách chưa lọc cho agent là giao cho nó mấy đường cụt.

⚠️ **Coi chừng hàm TRÙNG TÊN giữa hai cụm.** `MarkReadAsync` tồn tại ở cả cụm chat lẫn cụm Bảng
tin (`InsightEndpoints`) với chữ ký khác nhau. Đổi theo tên mà không nhìn kiểu là sửa nhầm sang
tính năng khác — đúng thứ chủ dự án đã dặn phải soi ("có ảnh hưởng gì đến bản tin, thông báo deal
không").

**Giá của việc bỏ bước này, đo được:** brief bảo đổi `ChatAuditRow` mà không kèm danh sách chỗ
dùng; agent sửa xong tầng Domain thì chết giữa chừng, để lại cây **gãy biên dịch** ở
`ChatInboxEndpoints.cs` — đúng lúc một luồng review khác đang chạy `dotnet test`. Chạy bù thì
codegraph nêu đúng chỗ đó ngay từ đầu.

---

## 4. Việc còn nợ KHÔNG được nuốt vào "done"

Một task xong thường đẻ ra vài món chưa làm: phải chạy tay trên môi trường thật, một chỗ tối ưu để
sau, một lỗ hẹp cố ý hoãn. **Mỗi món đó là một issue riêng trên Multica**, không phải một dòng
trong báo cáo.

⚠️ Vì sao thành luật cứng: ngày 07/09/2026, bốn task đầu của đợt phân quyền chat đẻ ra **sáu món
còn nợ** — ba thứ phải chạy tay trên staging, một chỗ thiếu bộ đệm, một lỗ ở đường tải tệp, một
guard phủ hụt. Cả sáu nằm trong sổ SDD, tức **không ai ngoài Claude đọc được**. Một món nợ chỉ tồn
tại trong đầu một người thì bằng không tồn tại.

Issue nợ đặt tên bắt đầu bằng `Nợ:` và **liên kết về issue cha**.

---

## 5. Đợt lớn ánh xạ lên Multica thế nào

Việc lớn vẫn đi qua quy trình sẵn có — bàn phương án → đặc tả → kế hoạch → thi công từng task có
review. Ánh xạ:

| Trong repo | Trên Multica |
|---|---|
| Một đặc tả + kế hoạch | Một **project**, mô tả ghi rõ luật đã chốt và cảnh báo lớn |
| Một task trong kế hoạch | Một **issue**, mô tả ghi việc làm gì + bẫy phải tránh |
| Một vòng review | Bình luận trên issue đó |
| Món nợ phát sinh | Issue `Nợ:` riêng |

Đường dẫn đặc tả và kế hoạch **ghi trong mô tả project**, để ai muốn đọc sâu thì lần được.

---

## 6. Multica KHÔNG làm gì — và vì sao

Multica cho phép gán issue cho agent để nó tự chạy Claude Code trên máy đã nối. **Chưa dùng.**

**Lý do 1 — mất lớp review.** Quy trình hiện tại chạy hai vòng soát độc lập cho mỗi task. Ngày
07/09/2026 chính hai vòng đó bắt được ba lỗi mà agent đã tự báo là "xong":

- quyền admin không sống sót qua lần khởi động lại — admin âm thầm tụt xuống nhân viên thường;
- bộ đếm không lọc trong khi danh sách đã lọc — rò đúng khối lượng dữ liệu đang cố giấu;
- một luật bảo mật đúng nhưng **không test nào khoá** — nới cho sai đi thì cả 1.194 test vẫn xanh.

Cả ba đều không có triệu chứng khi chạy. Agent tự chạy tự báo xong thì cả ba đã lọt.

**Lý do 2 — ranh giới dữ liệu.** Tài liệu Multica nói rõ: mã nguồn và thư mục làm việc ở lại máy
mình, nhưng máy chủ **có lưu** ngữ cảnh và kết quả mỗi lượt chạy, *"có thể gồm đoạn mã hoặc ngữ
cảnh dự án mà agent đọc và đưa vào câu trả lời"*. Kho này cầm hội thoại khách thật của nhiều công
ty du lịch. Một lượt chạy mà agent trích nội dung hộp thư vào báo cáo là nội dung đó nằm trên máy
chủ bên thứ ba — đúng loại việc mà [yeu-cau-du-lieu-tu-co-quan.md](yeu-cau-du-lieu-tu-co-quan.md)
nói tới.

**Mở lại khi nào:** khi có việc lặp đi lặp lại, không đụng dữ liệu khách, và không cần review —
ví dụ bản tin tiến độ hằng ngày. Không phải khi thấy nó tiện.

---

## 7. Mẹo vận hành — bốn thứ tốn thời gian nếu không biết trước

**CLI nằm trong bản Desktop**, không cài riêng:
```
%LOCALAPPDATA%\Programs\@multicadesktop\resources\app.asar.unpacked\resources\bin\multica.exe
```

⚠️ **Phải truyền `--profile desktop-api.multica.ai`.** Bản Desktop giữ cấu hình ở hồ sơ riêng; gọi
CLI trần thì nó báo *"No server configured"* dù bạn đã đăng nhập trên Desktop.

⚠️ **Đổi trạng thái thì thêm `--no-start`.** Không có cờ đó, Multica có thể **tự khởi động một lượt
chạy agent**. Đánh dấu xong bốn việc cũ mà kích hoạt bốn lượt chạy thật là chuyện đã suýt xảy ra.

⚠️ **Mô tả nhiều dòng:** issue dùng `--description-file` (an toàn với tiếng Việt trên Windows).
Project **chỉ có** `--description` và **không giải mã** `\n` — phải truyền xuống dòng thật, không
thì mô tả hiện ra đầy ký tự `\n`.

⚠️ **DNS hay chập** với `api.multica.ai` và `raw.githubusercontent.com` trên máy dev. Lệnh đầu
thường hỏng, thử lại là được — đừng đi chẩn đoán nhầm sang phía Multica.
