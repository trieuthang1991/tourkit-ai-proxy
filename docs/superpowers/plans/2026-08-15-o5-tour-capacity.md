# O5 — Canh chỗ tour: sắp đầy thì đẩy bán, quá vắng thì xoay sớm

> Đợt 3. Mở rộng tác vụ **`tour-readiness`** đã có (O1, Đợt 2) — **KHÔNG dựng tác vụ mới**.
> Ngày: 15/08/2026 · Nhánh: `feat/dot2-hanh-dong`

## Vì sao mở rộng chứ không làm tác vụ riêng

`tour-readiness` đã quét đúng tập tour đó, đúng nhịp đó, ghi vào đúng chỗ đó (Bảng tin). Dựng tác vụ
thứ hai nghĩa là quét lại y hệt và đẻ ra **hai thẻ nói về cùng một tour** trong cùng một buổi sáng —
người điều hành đọc hai thẻ rồi tự ghép lại trong đầu. Một tác vụ, một thẻ mỗi tour mỗi mốc.

## Dữ liệu — đã kiểm THẬT, không tin tài liệu

Tài liệu lộ trình (11/08) ghi "capacity có sẵn". Đo lại ngày 15/08 trên bốn tenant, chỉ đọc:

| Tenant | Tour lấy về | Có khai số chỗ | Có khách đặt |
|---|---|---|---|
| staging.tourkit.vn | 100 | **1** | **0** |
| demo2.tourkit.vn | 100 | **100** | **38** |
| erp.tourkit.vn | 0 | — | — |

**Kết luận quan trọng:** nếu chỉ nhìn staging thì phải kết luận O5 vô nghĩa (giống S2/S3 đã hoãn).
Nhưng staging là dữ liệu rác — tenant dùng thật khai chỗ cho **mọi** tour. O5 chạy được.
⚠️ Hệ quả cho khâu kiểm thử: **không thể e2e phần chỗ ngồi trên staging** (không có dữ liệu để kích
hoạt). Xem mục "Kiểm thử" bên dưới.

Ba dòng thật lấy từ demo2:

```
slots=20  booked=6  onHold=1  available=13
slots=100 booked=1  onHold=3  available=96
slots=25  booked=0  onHold=1  available=24
```

→ `available = slots − booked − onHold`. **Giữ chỗ cũng chiếm chỗ.**

## Lỗi phát hiện trong O1 (đã giao ở Đợt 2)

`TourReadinessRule` đang đếm khách bằng **`Booked` một mình**, bỏ qua `OnHold`. Tour thật ở trên đã
kín 7/20 chỗ nhưng O1 tính là 6 → công ty khai ngưỡng tối thiểu 7 sẽ nhận **cảnh báo sai** "chưa đủ
khách" cho một tour đã đủ. Sai theo hướng nguy hiểm: báo động giả làm người ta bỏ qua cả cảnh báo thật.

Sửa: `Taken = Booked + OnHold`.

Cố ý **KHÔNG** dùng trường `available` upstream trả về, mà tự tính `Slots − Taken`: giữ một nguồn duy
nhất (3 số gốc), khỏi cảnh hai con số vênh nhau mà không biết tin cái nào.

## Thay đổi

### 1. `TourReadinessRow` — thêm `OnHold`

Thêm field, `TourReadinessWorkflow` map thêm `GetInt(it, "onHold")`.

### 2. Sửa phép đếm khách (lỗi ở trên)

`checkSeats`: `Taken < minSeats` thay vì `Booked < minSeats`. Câu chữ đổi theo:
`"mới 7/20 chỗ (6 đã đặt + 1 giữ chỗ), dưới mức tối thiểu 10"` — nói tách ra vì người điều hành cần
biết phần "giữ chỗ" có thể rơi.

### 3. Thêm tín hiệu "sắp đầy"

`nearly_full`: `Slots > 0 && Available > 0 && tỉ lệ kín ≥ ngưỡng` (mặc định **80%**).
→ `"đã kín 17/20 chỗ — còn 3 chỗ, đẩy bán nốt"`

- `Available > 0` là bắt buộc: **tour đầy hẳn thì không còn gì để làm**, nhắc là nhiễu.
- Dùng **tỉ lệ** chứ không phải số chỗ tuyệt đối: "còn ≤3 chỗ" đúng với tour 20 chỗ nhưng vô nghĩa
  với tour 100 chỗ (còn 3/100 là gần như đã đầy từ lâu).

### 4. Mốc RIÊNG cho phần chỗ ngồi

`capacityMilestones` mặc định **{21, 14, 7}**, tách khỏi `milestones` {7, 3, 1}.

Lý do: hai loại việc có **đồng hồ khác nhau**. "Chưa thu đủ tiền" chỉ gấp khi sát ngày đi. Còn "bán
nốt 3 chỗ cuối" hay "vắng quá, cân nhắc dồn chuyến" mà tới D-7 mới nói thì đã hết đường xoay — đó
đúng là điều O5 sinh ra để sửa.

Cách chấm mốc: lấy **hợp** của hai tập mốc; ở mỗi mốc chỉ chạy nhóm kiểm nào có mốc đó trong tập của
mình. Nhờ vậy tour ở D-10 chỉ bị soi phần chỗ ngồi (mốc 14), còn ở D-5 thì soi cả hai nhưng vẫn ra
**một thẻ duy nhất** (cùng mốc 7).

### 5. Chữ trên thẻ

Thẻ hiện có mở đầu bằng *"Còn thiếu:"* — dán "sắp đầy" vào đó thì đọc thành "còn thiếu: sắp đầy".
Tách hai nhóm: **vấn đề** giữ nguyên như cũ; **cơ hội** (`nearly_full`) xuống mục riêng.
Thẻ chỉ có cơ hội → đổi hẳn tiêu đề, không dùng chữ "chưa xong".
`Severity` chỉ tính theo nhóm vấn đề — tour sắp đầy là tin vui, tô đỏ là sai.

### 6. Tuỳ chọn (khớp 3 nơi)

| Khoá | Mặc định | Nghĩa |
|---|---|---|
| `checkNearlyFull` | `true` | Bật cảnh báo tour sắp đầy |
| `nearlyFullPercent` | `80` | Kín từ bao nhiêu % thì nhắc |
| `capacityMilestones` | `[21, 14, 7]` | Mốc riêng cho phần chỗ ngồi |

Phải khai ở **cả ba** chỗ: `Options` trong workflow · `WORKFLOW_OPTIONS` trong
[workflow-options.jsx](../../../wwwroot/components/workflow-options.jsx) · nhóm hiển thị ở cùng file.

### 7. Sơ đồ luồng

Cập nhật [`wwwroot/flows/tour-readiness.js`](../../../wwwroot/flows/tour-readiness.js), rồi
`node scripts/e2e/features-flow-diagram.check.js`.

## Không làm

- **Không** gợi ý "hủy hay dồn chuyến" — đó là quyết định kinh doanh, hệ thống chỉ nêu con số.
- **Không** nhắc riêng cho từng nhân viên bán. Thẻ vẫn tenant-wide như O1.
- **Không** đụng phần tiền/visa.

## Thứ tự làm (test trước)

1. Test cho phép đếm khách gồm giữ chỗ (đỏ trước, vì đang là lỗi thật).
2. Test `nearly_full`: đúng ngưỡng, tour đầy hẳn thì im, tour không khai chỗ thì im.
3. Test mốc riêng: D-10 chỉ ra chỗ ngồi; D-5 gộp một thẻ; mốc trùng không đẻ hai thẻ.
4. Sửa `TourReadinessRule` cho xanh.
5. `TourReadinessRow` + map `onHold` + `Options` + chữ trên thẻ.
6. Frontend options + nhóm + sơ đồ luồng.
7. Chạy toàn bộ test.

## Kiểm thử

**Đơn vị** — `TourReadinessRuleTests` (luật thuần, không DB/mạng).

**Đối chiếu dữ liệu THẬT (chỉ đọc).** Staging không kích hoạt được phần chỗ ngồi. Lấy tour thật của
tenant có dữ liệu **qua đường đọc**, chạy luật, in ra thẻ sẽ sinh — **không ghi Bảng tin, không đụng
tenant nào**. Đây là chỗ duy nhất chứng minh luật đúng trên số thật.

**E2E đường ghi** — chạy tác vụ trên **staging** (`/run-now` rồi đọc `/runs`): chứng minh không vỡ,
chấp nhận không có thẻ chỗ ngồi nào vì staging không có dữ liệu. Nói rõ điều đó khi báo cáo, đừng để
"0 thẻ" bị đọc thành "đã kiểm xong".
