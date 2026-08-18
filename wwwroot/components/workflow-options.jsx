// components/workflow-options.jsx — NGUỒN DUY NHẤT cho schema + ô nhập cấu hình workflow.
//
// Trước đây toàn bộ nằm trong pages/workflows.jsx. Tách ra để trang sơ đồ
// (pages/flow-preview.jsx) sửa được ĐÚNG bộ cấu hình đó, thay vì dựng bộ ô nhập thứ hai —
// hai bộ form cho cùng một cấu hình là kiểu lỗi âm thầm khó tìm (sửa bên này quên bên kia).
//
// Nội dung chuyển sang NGUYÊN VĂN, không đổi hành vi. Thêm workflow/option mới vẫn chỉ cần
// thêm 1 entry vào WORKFLOW_OPTIONS — cả 2 trang tự render.
//
// ⚠️ File này PHẢI được nạp TRƯỚC pages/workflows.jsx và pages/flow-preview.jsx
//    (thứ tự trong index.html và bundle-entry.js).

(function () {
  const { useState: uS, useEffect: uE } = React;

  // Tần suất chạy (giá trị = SỐ PHÚT lưu xuống backend).
  const INTERVAL_OPTIONS = [
    { value: 5,    label: 'Mỗi 5 phút' },
    { value: 10,   label: 'Mỗi 10 phút' },
    { value: 15,   label: 'Mỗi 15 phút' },
    { value: 30,   label: 'Mỗi 30 phút' },
    { value: 60,   label: 'Mỗi 1 giờ' },
    { value: 180,  label: 'Mỗi 3 giờ' },
    { value: 360,  label: 'Mỗi 6 giờ' },
    { value: 720,  label: 'Mỗi 12 giờ' },
    { value: 1440, label: 'Mỗi ngày' },
  ];

  const MAIL_CATEGORIES = [
    { value: 'hoi_dat_tour', label: 'Hỏi/đặt tour' },
    { value: 'xin_bao_gia',  label: 'Xin báo giá' },
    { value: 'xac_nhan',     label: 'Xác nhận' },
    { value: 'khieu_nai',    label: 'Khiếu nại' },
  ];
  const MAIL_TONES = [
    { value: 'lich_su',    label: 'Lịch sự' },
    { value: 'than_thien', label: 'Thân thiện' },
    { value: 'dam_phan',   label: 'Đàm phán' },
    { value: 'xin_loi',    label: 'Xin lỗi' },
  ];
  // Loại tour — danh sách CỐ ĐỊNH của hệ thống, giống nhau ở mọi công ty (không phải mã tự đặt).
  // Trước đây ô này bắt người dùng gõ số ("102"), mà con số đó chỉ người viết code mới biết nghĩa.
  // Giữ nguyên GIÁ TRỊ SỐ khi lưu để cấu hình cũ vẫn đúng, chỉ đổi phần người dùng nhìn thấy.
  const TOUR_TYPES = [
    { value: 2,   label: 'FIT (khách lẻ)' },
    { value: 3,   label: 'GIT (đoàn ghép)' },
    { value: 1,   label: 'LandTour' },
    { value: 102, label: 'Visa' },
    { value: 100, label: 'Booking' },
    { value: 101, label: 'Dịch vụ lẻ' },
    { value: 104, label: 'Vé máy bay' },
  ];

  // Schema option ĐỘNG per-workflow (client). Thêm workflow/option mới = thêm 1 entry — UI tự render.
  // type: 'bool'→toggle | 'select'→dropdown | 'multi'→chip nhiều lựa chọn. showIf: chỉ hiện khi option kia bật.
  // Khớp shape OptionsJson backend (mail-auto-sync: {autoReply, replyMode, replyCategories[], replyTone}).
  const WORKFLOW_OPTIONS = {
    'mail-auto-sync': [
      { key: 'autoReply', type: 'bool', label: 'Tự động trả lời',
        hint: 'AI tự soạn/gửi trả lời cho email mới theo cấu hình dưới đây.' },
      { key: 'replyMode', type: 'select', label: 'Chế độ', showIf: 'autoReply', default: 'draft',
        options: [
          { value: 'draft', label: 'Soạn sẵn (người duyệt rồi gửi)' },
          { value: 'send',  label: 'Gửi thẳng tự động' },
        ],
        hint: 'Soạn sẵn = an toàn (nháp chờ NV duyệt). Gửi thẳng = AI gửi luôn cho khách.' },
      // Bắt buộc: rỗng thì KHÔNG email nào được trả lời (backend so `ReplyCategories.Contains`),
      // tức bật "tự động trả lời" xong chẳng có gì xảy ra mà không báo lỗi gì.
      { key: 'replyCategories', type: 'multi', label: 'Áp dụng nhóm', showIf: 'autoReply', required: true,
        default: ['hoi_dat_tour', 'xin_bao_gia', 'xac_nhan'],
        options: MAIL_CATEGORIES,
        hint: 'Chỉ auto-reply email thuộc nhóm đã chọn. Khiếu nại nên để người xử lý.' },
      { key: 'replyTone', type: 'select', label: 'Giọng văn', showIf: 'autoReply', default: 'lich_su',
        options: MAIL_TONES },
    ],
    'deal-auto-review': [
      { key: 'statuses', type: 'multi', dynamic: 'dealStatuses', label: 'Trạng thái áp dụng', required: true,
        dynamicDefault: 'openDealStatuses',
        hint: 'Những trạng thái cơ hội mà workflow được phép xử lý. Chọn sẵn các trạng thái còn đang mở — xem lại cho khớp cách công ty bạn đặt tên.' },
      { key: 'createdWithinDays', type: 'number', label: 'Chỉ deal tạo trong (ngày)', default: 30, min: 1, max: 365,
        hint: 'Chỉ xử lý deal được tạo trong khoảng ngày gần đây này. Deal cũ hơn được bỏ qua.' },
      // ── ② Chấm điểm cơ hội (AI) — công tắc chính autoReview ──
      { key: 'autoReview', type: 'bool', label: 'AI tự chấm điểm cơ hội mới', default: true,
        hint: 'Bật để AI tự cho điểm khả năng chốt từng cơ hội mới. Tắt hẳn phần chấm điểm (vẫn có thể bật riêng cảnh báo nguội bên dưới).' },
      { key: 'reviewMax', type: 'number', label: 'Tối đa cơ hội chấm mỗi lượt', showIf: 'autoReview', default: 20, min: 1, max: 100,
        hint: 'Mỗi lượt chạy chấm tối đa bao nhiêu cơ hội (gồm cả chấm mới lẫn chấm lại).' },
      { key: 'reReview', type: 'bool', label: 'Chấm lại cơ hội cũ định kỳ', showIf: 'autoReview', default: true,
        hint: 'Bật để định kỳ chấm lại cơ hội đã chấm (theo chu kỳ bên dưới) khi nội dung có thay đổi. Tắt = chỉ chấm cơ hội mới, không chấm lại.' },
      { key: 'reReviewDays', type: 'number', label: 'Chấm lại sau mỗi (ngày)', showIf: ['autoReview', 'reReview'], default: 10, min: 1, max: 365,
        hint: 'Cơ hội đã chấm được xét chấm lại sau tối thiểu bao nhiêu ngày (và chỉ khi nội dung đổi). 7 ≈ mỗi tuần, 10 = mặc định, 30 ≈ mỗi tháng.' },
      { key: 'maxAutoReviews', type: 'number', label: 'Tối đa số lần chấm lại / cơ hội', showIf: ['autoReview', 'reReview'], default: 5, min: 1, max: 50,
        hint: 'Mỗi cơ hội được chấm lại tối đa bao nhiêu lần, tránh chấm đi chấm lại mãi một cơ hội.' },
      // ── ③ Cảnh báo cơ hội nguội — công tắc chính alertCooling ──
      { key: 'alertCooling', type: 'bool', label: 'Gửi cảnh báo cơ hội nguội', default: true,
        hint: 'Bật để tự phát hiện cơ hội đang mở nhưng lâu không chăm sóc ("nguội") và gửi email nhắc nhân viên phụ trách. Tắt = bỏ hẳn phần cảnh báo (không quét, không gửi).' },
      { key: 'coolingStatuses', type: 'multi', dynamic: 'dealStatuses', label: 'Trạng thái tính "nguội"',
        showIf: 'alertCooling', required: true, dynamicDefault: 'openDealStatuses',
        hint: 'Chỉ cảnh báo nguội cho cơ hội ở các trạng thái này. Chọn sẵn các trạng thái còn đang mở — xem lại cho khớp cách công ty bạn đặt tên. Cũng áp cho badge "nguội" trên trang Cơ hội.' },
      // Mặc định 3 ngày (trước là 7) — PHẢI khớp DealAutoReviewOptions.Parse bên C#,
      // lệch nhau thì giao diện hiện một đằng, hệ thống chạy một nẻo.
      { key: 'coolingDays', type: 'number', label: 'Coi là "nguội" sau (ngày)', showIf: 'alertCooling', default: 3, min: 1, max: 90,
        hint: 'Cơ hội đang mở mà quá số ngày này không ai chăm sóc thì coi là "nguội" và được đưa vào cảnh báo.' },
      { key: 'minWinRateToNotify', type: 'number', label: 'Chỉ cảnh báo khi % chốt từ', showIf: 'alertCooling', default: 0, min: 0, max: 100,
        hint: 'Chỉ cảnh báo những cơ hội có khả năng chốt từ mức % này trở lên. Để 0 = cảnh báo mọi cơ hội nguội.' },
      { key: 'maxNotifications', type: 'number', label: 'Tối đa số lần cảnh báo / cơ hội', showIf: 'alertCooling', default: 3, min: 1, max: 20,
        hint: 'Mỗi cơ hội chỉ gửi cảnh báo tối đa bao nhiêu lần, tránh làm phiền nhân viên.' },
      { key: 'notifyMinGapHours', type: 'number', label: 'Nhắc lại cùng 1 cơ hội sau ít nhất (giờ)', showIf: 'alertCooling', default: 24, min: 1, max: 720,
        hint: 'Sau khi đã cảnh báo một cơ hội, phải chờ đủ số giờ này mới được nhắc lại. Ví dụ 24 = mỗi cơ hội tối đa 1 lần/ngày.' },
    ],
    // Bản tin sáng. Mấy giá trị này TRƯỚC ĐÂY LÀ HẰNG SỐ TRONG CODE — hằng số nghĩa là mình đoán hộ
    // công ty. Ngưỡng "im bao lâu thì cần gọi" ở bên bán tour đoàn khác hẳn bên bán vé lẻ.
    // ⚠️ default PHẢI khớp SaleBriefWorkflow.ParseOptions bên C#: lệch nhau thì giao diện hiện một
    // đằng, hệ thống chạy một nẻo — mà người dùng không có cách nào biết.
    // Bản tin sáng. NHÓM THEO MỤC, không theo loại cấu hình: người dùng nghĩ "tôi muốn được nhắc
    // cơ hội cần gọi" rồi mới tới "nhắc khi nào". Bản đầu tách tick / trạng thái / ngưỡng ra 3 nhóm
    // riêng nên chỉnh một mục phải đụng 3 chỗ — không ai mò ra.
    //
    // Trạng thái hỏi theo hướng KHẲNG ĐỊNH ("chỉ nhắc khi ở trạng thái…") chứ không phải phủ định
    // ("coi là đã đóng"): bắt người dùng suy ngược "cái nào là đóng để loại ra" là bắt họ làm việc
    // của mình. deal-auto-review ngay cạnh cũng hỏi "Trạng thái áp dụng" — giữ cho giống nhau.
    //
    // ⚠️ default PHẢI khớp SaleBriefWorkflow.ParseOptions bên C#.
    'sale-brief': [
      // ── Cơ hội cần gọi lại ──
      { key: 'secCooling', type: 'bool', label: 'Đưa vào bản tin', default: true,
        hint: 'Cơ hội đang theo đuổi mà lâu không ai liên hệ.' },
      // BẮT BUỘC chọn: bỏ trống thì hệ thống phải tự đoán trạng thái nào là "đã đóng" theo TÊN, mà
      // CRM không có cờ nào nói điều đó — đoán trượt là bản tin bảo gọi lại đơn đã hủy (đã xảy ra).
      { key: 'callStatuses', type: 'multi', dynamic: 'dealStatuses', showIf: 'secCooling', required: true,
        dynamicDefault: 'openDealStatuses',
        label: 'Chỉ nhắc khi cơ hội đang ở trạng thái',
        hint: 'Chọn sẵn mọi trạng thái CÒN phải chăm (bỏ Hủy / Đã chốt / Thất bại) — xem lại cho khớp cách công ty bạn đặt tên. Danh sách này cũng áp cho mục "cần dọn hồ sơ" bên dưới.' },
      { key: 'silentDaysMin', type: 'number', showIf: 'secCooling', default: 3, min: 1, max: 90,
        label: 'Nhắc khi im lặng quá (ngày)',
        hint: 'Đặt thấp thì bản tin đầy; đặt cao thì phát hiện muộn. 3 ngày là mặc định.' },

      // ── Cơ hội cần dọn hồ sơ ──
      { key: 'secHygiene', type: 'bool', label: 'Đưa vào bản tin', default: true,
        hint: 'Cơ hội nằm lì một trạng thái quá lâu, chưa có bước tiếp theo — cần vào cập nhật cho đúng thực tế.' },
      { key: 'hygieneStuckDays', type: 'number', showIf: 'secHygiene', default: 14, min: 3, max: 365,
        label: 'Nhắc khi kẹt quá (ngày)',
        hint: 'Phải lớn hơn ngưỡng im lặng ở trên. Cơ hội vượt mốc này chuyển sang mục "cần dọn" thay vì "cần gọi" — mỗi cơ hội chỉ nằm ở MỘT mục.' },

      // ── Báo giá bỏ dở ──
      { key: 'secQuotes', type: 'bool', label: 'Đưa vào bản tin', default: true,
        hint: 'Báo giá tour đã dựng nhưng lâu không ai cập nhật. LƯU Ý: hiện chưa phân biệt được báo giá đã gửi khách hay đã chốt, nên có thể nhắc cả cái đã xong — tắt nếu thấy phiền.' },
      { key: 'staleQuoteDays', type: 'number', showIf: 'secQuotes', default: 5, min: 1, max: 365,
        label: 'Nhắc khi quá (ngày) không cập nhật' },

      // ── Việc cần làm ──
      { key: 'secTasks', type: 'bool', label: 'Việc cần làm hôm nay', default: true,
        hint: 'Nhắc việc đến hạn trong hôm nay VÀ việc đã quá hạn mà chưa xong (việc quá hạn xếp lên đầu, có dấu riêng). Việc quá hạn vẫn làm được nên vẫn nhắc — khác lịch hẹn, đã trôi qua thì thôi.' },
      { key: 'taskStatuses', type: 'multi', dynamic: 'taskStatuses', showIf: 'secTasks', required: true,
        dynamicDefault: 'openTaskStatuses',
        label: 'Việc coi là CHƯA xong khi ở trạng thái',
        hint: 'Quyết định thế nào là "còn phải làm". Tên trạng thái do công ty bạn đặt nên chỉ bạn biết chắc — ví dụ có nơi "Đang kiểm tra" nghĩa là đã làm xong, chờ duyệt. Chọn sẵn theo cách hiểu thông thường, xem lại cho khớp.' },

      // ── Các mục còn lại: chỉ bật/tắt, không có gì để chỉnh ──
      { key: 'secAppointments', type: 'bool', label: 'Lịch hẹn hôm nay', default: true,
        hint: 'Chỉ nhắc cuộc hẹn có giờ TRONG HÔM NAY; hẹn đã trôi qua thì bỏ qua vì không làm bù được. Lịch đã đánh dấu thành công/không thành công cũng không nhắc nữa.' },
      { key: 'secPayments', type: 'bool', label: 'Tour sắp đi còn thiếu tiền', default: true,
        hint: 'Tour sắp khởi hành mà khách chưa thanh toán đủ — trễ một ngày là mất tiền thật.' },
      { key: 'secVips', type: 'bool', label: 'Khách quen lâu không chăm', default: true },
      { key: 'secMailbox', type: 'bool', label: 'Hộp thư công ty', default: true,
        hint: 'Một dòng tổng số thư chờ xử lý của cả công ty (không phải của riêng bạn).' },

      // ── Cách trình bày ──
      { key: 'useAi', type: 'bool', label: 'AI sắp xếp lại bản tin', default: true,
        hint: 'Bật: AI đọc dữ liệu rồi chọn ra việc đáng làm nhất, bản tin gọn và có thứ tự ưu tiên (tốn 1 lượt AI mỗi người mỗi ngày). Tắt: in đủ mọi mục theo ngưỡng — dài hơn nhưng không tốn lượt. AI lỗi thì tự rơi về bản đủ, không mất bản tin.' },
      { key: 'maxItems', type: 'number', label: 'Tối đa số việc trong bản tin', showIf: 'useAi', default: 7, min: 3, max: 20,
        hint: 'Bản tin dài thì không ai đọc hết, mà đọc không hết thì mục quan trọng cũng bị bỏ qua. 5–7 việc là vừa một buổi sáng.' },
    ],
    // Bản tin điều hành. Trước 14/08 tác vụ này KHÔNG đọc tuỳ chọn nào — luôn so cùng kỳ tháng
    // trước, luôn in đủ 6 dòng, luôn gọi AI. Hằng số nghĩa là đoán hộ mọi công ty.
    // ⚠️ default PHẢI khớp CeoBriefWorkflow.ParseOptions bên C#.
    'ceo-brief': [
      // ── Kỳ so sánh ──
      { key: 'comparePeriod', type: 'select', label: 'So sánh với', default: 'prev-month',
        options: [
          { value: 'prev-month', label: 'Cùng kỳ tháng trước' },
          { value: 'prev-year',  label: 'Cùng kỳ năm trước' },
          { value: 'none',       label: 'Không so sánh' },
        ],
        hint: 'Du lịch theo mùa nên "so tháng trước" dễ đánh lừa: hè thì tháng nào cũng tăng mạnh, tháng 9 thì năm nào cũng giảm sâu. So cùng kỳ NĂM trước mới thấy thật sự hơn hay kém. Chọn "không so sánh" nếu chỉ cần con số tuyệt đối.' },

      // ── Đưa thêm mục nào vào bản tin ──
      { key: 'secSellers', type: 'bool', label: 'Top nhân viên bán hàng', default: true,
        hint: 'Xếp hạng doanh số từ đầu tháng tới hôm nay.' },
      { key: 'sellerCount', type: 'number', label: 'Lấy mấy người đầu bảng', showIf: 'secSellers',
        default: 3, min: 1, max: 10,
        hint: 'Tối đa 10 — CRM chỉ trả về 10 người đầu bảng, đặt cao hơn cũng không có thêm.' },
      { key: 'secNewDeals', type: 'bool', label: 'Cơ hội mới hôm qua', default: true,
        hint: 'Số cơ hội bán hàng được tạo trong ngày hôm qua — nhịp vào của đầu phễu.' },
      { key: 'secAppointments', type: 'bool', label: 'Lịch hẹn hôm nay', default: true,
        hint: 'Số cuộc hẹn trong ngày, kèm số tồn đọng quá hạn. LƯU Ý: số tồn đọng là tích luỹ từ trước tới nay nên ở CRM dùng lâu có thể lên hàng nghìn — tắt nếu thấy gây nhiễu.' },
      { key: 'secAlerts', type: 'bool', label: 'Cảnh báo thanh toán đang mở', default: true,
        hint: 'Số cảnh báo tour sắp khởi hành mà khách chưa trả đủ. Cần bật tác vụ "Canh thanh toán trước khởi hành" thì mới có số.' },
      { key: 'secTasks', type: 'bool', label: 'Công việc chưa hoàn thành', default: true,
        hint: 'Tổng số việc của cả công ty còn đang treo, kèm số việc đã quá hạn — để biết khối lượng còn nợ và phần nào cần can thiệp.' },
      { key: 'taskStatuses', type: 'multi', dynamic: 'taskStatuses', showIf: 'secTasks', required: true,
        dynamicDefault: 'openTaskStatuses',
        label: 'Việc coi là CHƯA xong khi ở trạng thái',
        hint: 'Quyết định con số "còn treo" đếm những việc nào. Tên trạng thái do công ty bạn đặt nên chỉ bạn biết chắc — ví dụ có nơi "Đang kiểm tra" nghĩa là đã làm xong, chờ duyệt. Chọn càng nhiều trạng thái thì mỗi lần gửi càng phải hỏi CRM nhiều lượt, nên chỉ tick những trạng thái thật sự còn phải làm.' },

      // ── Dự phóng cuối tháng ──
      { key: 'secForecast', type: 'bool', label: 'Dự phóng cuối tháng', default: true,
        hint: 'Theo tốc độ bán từ đầu tháng tới hôm nay, ước cả tháng sẽ đạt bao nhiêu và bằng bao nhiêu phần trăm kế hoạch. Phép tính thẳng, không phải dự báo bằng máy học — và số gốc luôn in kèm để bạn tự kiểm.' },
      { key: 'revenueTarget', type: 'number', label: 'Chỉ tiêu doanh thu tháng (đồng)', showIf: 'secForecast',
        default: 0, min: 0, max: 1000000000000,
        hint: 'Để 0 = KHÔNG hiện phần dự phóng. Cố ý không đoán hộ: một chỉ tiêu bịa ra sẽ khiến bản tin nào cũng kèm một dự phóng vô nghĩa. Nhập số tiền, ví dụ 3000000000 cho 3 tỷ. Bốn ngày đầu tháng hệ thống chỉ báo số thực đạt chứ không ước — quá sớm thì một hợp đồng lớn cũng đủ làm con số sai lệch nhiều lần.' },

      // ── Cách trình bày ──
      { key: 'useAi', type: 'bool', label: 'AI viết lời', default: true,
        hint: 'Bật: AI đọc số rồi viết 5–8 câu tổng kết (tốn khoảng 1 lượt AI mỗi lần gửi). Tắt: in thẳng bảng số, không tốn lượt nào. Số luôn do máy chủ tính — AI không bao giờ tự tính, nên bật hay tắt cũng không đổi con số.' },
      { key: 'showNumbers', type: 'bool', label: 'Đính bảng số dưới bài viết', default: true, showIf: 'useAi',
        hint: 'Giữ bật thì đọc xong lời văn còn đối chiếu được ngay với số gốc. Tắt thì bản tin gọn hơn nhưng phải tin AI suông.' },
    ],
    // Canh thanh toán trước khởi hành (O2). Luật thuần, không AI.
    // ⚠️ default PHẢI khớp PaymentWatchdogWorkflow.ParseOptions bên C#.
    'payment-watchdog': [
      { key: 'scanTourTypes', type: 'multi', label: 'Loại tour cần quét', default: [2, 3], required: true,
        options: TOUR_TYPES,
        hint: 'Quyết định tác vụ NHÌN THẤY những đơn nào — không phải bộ lọc phụ. Không chọn gì thì hệ thống chỉ đọc được tour FIT, nợ của tour GIT hay đơn lẻ sẽ không ai canh mà cũng không báo lỗi. Mặc định FIT + GIT.' },
      { key: 'windowDays', type: 'number', label: 'Canh trong bao nhiêu ngày trước khi đi', default: 7, min: 1, max: 60,
        hint: 'Nới rộng thì biết sớm hơn nhưng nhắc dai hơn, vì mỗi tour còn nợ được nhắc lại mỗi ngày cho tới lúc khởi hành. Bên đòi cọc sớm hay để 14–30; bên chỉ chốt sát ngày thì 7 là đủ.' },
      { key: 'minOutstanding', type: 'number', label: 'Nợ từ bao nhiêu đồng thì mới nhắc', default: 0, min: 0, max: 1000000000,
        hint: 'Để 0 = nhắc mọi khoản còn thiếu, kể cả chênh vài nghìn do làm tròn — mà mỗi khoản như thế vẫn chiếm một thẻ riêng trong Bảng tin. Thấy nhiễu thì nâng lên, ví dụ 1.000.000.' },
      { key: 'paymentStatus', type: 'select', label: 'Lấy "còn nợ" theo đâu', default: 1,
        options: [
          { value: 1, label: 'Bộ lọc "Chưa thu hết" của phần mềm' },
          { value: 0, label: 'Tự tính: doanh thu trừ đã thu' },
        ],
        hint: 'Nên để theo phần mềm — đó đúng là bộ lọc bạn bấm ở màn hình tìm kiếm tour, nên cảnh báo và màn hình luôn nói cùng một con số. Cách này chặt hơn: chỉ tính đơn đã ghi nhận dòng tiền, phía khách hàng, và bỏ khách đã huỷ. Chọn "tự tính" nếu công ty bạn không dùng bước ghi nhận dòng tiền, vì khi đó bộ lọc kia sẽ không thấy khoản nào.' },
      { key: 'maxReminders', type: 'number', label: 'Mỗi tour nhắc tối đa bao nhiêu lần', default: 3, min: 0, max: 30,
        hint: 'Mỗi ngày nhiều nhất một lần cho mỗi tour, và dừng hẳn khi đủ số lần này. Để 0 = nhắc mãi tới ngày khởi hành — cửa sổ 30 ngày nghĩa là 30 lần cho cùng một tour, tới lần thứ tư là không ai đọc nữa.' },
      { key: 'emailEnabled', type: 'bool', label: 'Gửi thêm qua email', default: false,
        hint: 'Bảng tin trong ứng dụng thì luôn có. Bật thêm cái này nếu muốn nhận được thư kể cả hôm không mở phần mềm. Thư gửi cho MỌI người đã bật email ở khối "Nơi nhận của tôi" — họ khai một lần, dùng cho cả bản tin lẫn cảnh báo. Mỗi lượt quét một thư gộp, không phải mỗi tour một thư.' },
      { key: 'alertEmails', type: 'text', label: 'Gửi thêm tới (không bắt buộc)', showIf: 'emailEnabled', default: '',
        placeholder: 'ketoan@congty.vn, giamdoc@congty.vn',
        hint: 'Chỉ dùng cho địa chỉ KHÔNG phải tài khoản trong phần mềm — ví dụ hộp thư chung của kế toán. Người có tài khoản thì tự khai ở "Nơi nhận của tôi", đừng gõ lại vào đây kẻo sau này họ đổi email mà chỗ này vẫn giữ địa chỉ cũ. Nhiều địa chỉ thì cách nhau dấu phẩy.' },
    ],
    // Kiểm tra sẵn sàng khởi hành (O1). Ba nhóm kiểm tách riêng vì ĐỘ TIN của dữ liệu khác nhau:
    // tiền có số thật, chỗ ngồi chỉ đúng khi công ty khai ngưỡng, visa thì CRM không lưu trạng thái
    // hồ sơ nên chỉ nhắc được là "tour có visa, tự kiểm".
    // ⚠️ default PHẢI khớp TourReadinessWorkflow.ParseOptions bên C#.
    'tour-readiness': [
      { key: 'scanTourTypes', type: 'multi', label: 'Loại tour cần quét', default: [2, 3], required: true,
        options: TOUR_TYPES,
        hint: 'Quyết định tác vụ NHÌN THẤY những đơn nào — không phải bộ lọc phụ. Không chọn gì thì hệ thống chỉ đọc được tour FIT, mọi phần kiểm khác vẫn chạy nhưng chỉ trên FIT mà không báo lỗi. Mặc định FIT + GIT. Mỗi loại tick thêm là thêm một lượt đọc dữ liệu.' },
      { key: 'milestones', type: 'numbers', label: 'Nhắc ở các mốc (ngày trước khi đi)', default: [7, 3, 1],
        hint: 'Nhập các mốc cách nhau dấu phẩy, ví dụ 7, 3, 1. Mỗi mốc nhắc đúng một lần: một lần còn kịp xoay, một lần cảnh báo, một lần chốt cuối. Nhắc mỗi ngày suốt tuần thì tới ngày thứ ba là không ai đọc nữa.' },

      { key: 'checkPayment', type: 'bool', label: 'Kiểm tiền đã thu', default: true,
        hint: 'Khách đã trả đủ chưa. Tour chưa chốt giá thì bỏ qua — "còn thiếu" lúc đó là khái niệm vô nghĩa.' },

      { key: 'checkSeats', type: 'bool', label: 'Kiểm số khách tối thiểu', default: true,
        hint: 'Tour ghép chưa đủ khách thì càng gần ngày đi càng khó xoay: dồn chuyến, đổi lịch hay huỷ đều cần thời gian.' },
      { key: 'minSeats', type: 'number', label: 'Số khách tối thiểu để tour chạy', showIf: 'checkSeats',
        default: 0, min: 0, max: 200,
        hint: 'Để 0 = KHÔNG kiểm phần này. Cố ý không đoán hộ: công ty chạy tour lẻ 2 khách mà bị áp ngưỡng 10 thì tour nào cũng bị báo thiếu khách. Chỉ tour có khai số chỗ mới được xét.' },

      { key: 'checkVisa', type: 'bool', label: 'Nhắc hồ sơ visa', default: true,
        hint: 'CRM không lưu trạng thái từng bộ hồ sơ, nên đây chỉ là lời nhắc "tour này có visa, kiểm lại hồ sơ khách" — không phải kết luận là đang thiếu.' },
      { key: 'visaTourTypes', type: 'multi', label: 'Loại nào được tính là hồ sơ visa', showIf: 'checkVisa', default: [102],
        options: TOUR_TYPES,
        hint: '⚠️ Đây KHÔNG phải "tour đi nước ngoài" — hệ thống không có chỗ nào ghi tour nào cần visa. Ô này chỉ chọn loại đơn nào được coi là hồ sơ visa, mặc định là đơn Visa. Tick FIT hay GIT vào đây thì MỌI tour loại đó đều bị nhắc, kể cả tour trong nước. Loại chọn ở đây cũng phải nằm trong "Loại tour cần quét" ở trên, không thì phần này không chạy.' },

      { key: 'checkNearlyFull', type: 'bool', label: 'Nhắc tour sắp đầy chỗ', default: true,
        hint: 'Tour ghép gần kín chỗ thì báo để đẩy bán nốt vài chỗ cuối. Đây là tin vui, không phải cảnh báo — thẻ hiện ở mức thông tin. Tour đã đầy hẳn thì không nhắc, vì chẳng còn gì để bán.' },
      { key: 'nearlyFullPercent', type: 'number', label: 'Kín từ bao nhiêu % thì nhắc', showIf: 'checkNearlyFull',
        default: 80, min: 50, max: 100,
        hint: 'Tính theo TỈ LỆ chứ không theo số chỗ còn lại: "còn 3 chỗ" đáng nhắc với tour 20 chỗ nhưng vô nghĩa với tour 100 chỗ. Chỗ đang GIỮ cũng tính là đã chiếm.' },
      { key: 'capacityMilestones', type: 'numbers', label: 'Mốc nhắc riêng cho phần chỗ ngồi', default: [21, 14, 7],
        hint: 'Xa hơn mốc ở trên là cố ý: "chưa thu đủ tiền" chỉ gấp khi sát ngày đi, còn "bán nốt chỗ cuối" hay "vắng quá, cân nhắc dồn chuyến" mà tới sát ngày mới nói thì đã hết đường xoay.' },
    ],
    // Canh doanh thu bất thường (C2). Ngành tour lên xuống theo mùa nên NGƯỠNG là thứ mỗi công ty
    // phải tự đặt — để mặc định thấp thì tuần nào cũng báo, ba lần là người ta tắt tính năng.
    'anomaly-watchdog': [
      { key: 'baselineWeeks', type: 'number', label: 'Lấy mấy tuần trước làm mức thường', default: 4, min: 2, max: 12,
        hint: 'Càng nhiều tuần thì mức "bình thường" càng ổn định nhưng phản ứng càng chậm với thay đổi thật. 4 tuần ≈ một tháng. Tối thiểu 2 — một tuần làm nền thì đó chỉ là ngẫu nhiên của đúng tuần đó.' },
      { key: 'thresholdPercent', type: 'number', label: 'Lệch từ bao nhiêu % thì báo', default: 30, min: 10, max: 200,
        hint: 'Lệch ít hơn mức này coi như dao động bình thường và KHÔNG báo. Đặt thấp quá thì tuần nào cũng có cảnh báo, mà cảnh báo tuần nào cũng có thì không còn là cảnh báo.' },
      { key: 'alertOnIncrease', type: 'bool', label: 'Báo cả khi tăng vọt', default: true,
        hint: 'Tăng mạnh cũng đáng biết — để kịp giữ nhịp hoặc phát hiện số liệu nhập sai. Thẻ tăng hiện ở mức thông tin, không tô đỏ như cảnh báo giảm. Tắt nếu bạn chỉ muốn nghe tin xấu.' },

      // Cảnh báo này KHÔNG có "người phụ trách" — không ai phụ trách doanh thu công ty — nên nơi
      // nhận phải khai tay, khác hẳn canh thanh toán / nhắc chăm khách (tự tìm người phụ trách).
      // Và vì nó mang số liệu tài chính toàn công ty, để trống là an toàn: chỉ vào Bảng tin.
      { key: 'alertEmails', type: 'text', label: 'Gửi email tới',
        placeholder: 'giamdoc@congty.vn, ketoan@congty.vn',
        hint: 'Nhiều địa chỉ thì cách nhau bằng dấu phẩy hoặc xuống dòng. Để trống = không gửi email, cảnh báo vẫn vào Bảng tin. Đây là SỐ LIỆU TÀI CHÍNH cả công ty — chỉ điền người thật sự cần xem.' },
      { key: 'alertTelegramChatIds', type: 'text', label: 'Gửi Telegram tới',
        placeholder: '6234567890, -1001234567890',
        hint: 'Là dãy SỐ (chat id), không phải @tên. Người nhận phải bấm Bắt đầu với bot trước, nếu không Telegram từ chối gửi. Chat id của nhóm bắt đầu bằng dấu trừ.' },
      { key: 'alertZaloPhones', type: 'text', label: 'Gửi Zalo tới',
        placeholder: '0912345678, 0987654321',
        hint: 'Số điện thoại đang dùng Zalo, cách nhau bằng dấu phẩy. Cần khai OA Zalo của công ty ở mục "Theo tổ chức" thì mới gửi được.' },
      { key: 'zaloTemplateId', type: 'text', label: 'Mã mẫu ZNS cho cảnh báo này',
        placeholder: 'mã mẫu ZNS',
        hint: 'Zalo duyệt mẫu theo nội dung nên cảnh báo doanh thu cần mẫu riêng, không dùng chung mẫu bản tin sáng. Bỏ trống thì phần Zalo không gửi được.' },
    ],
    // Nhắc chăm lại khách ngủ quên (S6). KHÔNG có tuỳ chọn nào về gửi thư — tác vụ này cố ý không
    // gửi gì cho khách, xem ghi chú trong CustomerAutoCareWorkflow.cs.
    'customer-auto-care': [
      { key: 'quietDays', type: 'number', label: 'Bao lâu không chăm thì coi là ngủ quên (ngày)', default: 90, min: 7, max: 3650,
        hint: 'Tính từ ngày chăm sóc gần nhất ghi trong CRM. Bên bán tour đoàn quen chu kỳ dài có thể để 180; bên bán vé lẻ thì 30–60 hợp hơn. Mẹo: để đúng 7, 15, 30 hoặc 90 thì bạn đối chiếu được — mở màn Khách hàng trong phần mềm, lọc "chưa chăm sóc" đúng mốc đó sẽ ra cùng danh sách. Số lẻ vẫn chạy chính xác, chỉ là phần mềm không có bộ lọc tương ứng để so tay.' },
      { key: 'ranks', type: 'multi', label: 'Chỉ nhắc khách hạng (không chọn = mọi hạng)', default: [],
        options: [
          { value: 'A', label: 'Hạng A' }, { value: 'B', label: 'Hạng B' },
          { value: 'C', label: 'Hạng C' }, { value: 'D', label: 'Hạng D' },
        ],
        hint: 'Hạng do phần chấm hạng khách hàng sinh ra. Không chọn gì = nhắc mọi hạng. Chọn A và B để chỉ tập trung vào khách sộp — cố ý KHÔNG bắt buộc chọn, vì "mọi hạng" là một lựa chọn hợp lệ chứ không phải chưa khai.' },
      { key: 'requireBought', type: 'bool', label: 'Chỉ khách đã từng mua', default: true,
        hint: 'Nên bật. Tắt đi thì danh sách sẽ kéo theo cả những hồ sơ chưa mua bao giờ và cũng lâu không ai đụng — phần lớn là dữ liệu rác trong CRM, và nó chôn vùi mấy khách thật sự đáng gọi.' },
      { key: 'maxLeads', type: 'number', label: 'Mỗi lần nhắc tối đa mấy khách', default: 20, min: 1, max: 50,
        hint: 'Danh sách phải ngắn thì mới có người gọi. Hai trăm dòng mỗi sáng thì không ai gọi dòng nào. Khách đã chi nhiều được xếp lên trước.' },

      // Không có hai ô này thì khách đã nhắc hôm nay sẽ nằm lại danh sách MỖI SÁNG cho tới khi có
      // người gọi — đo thật trên staging: 9/10 khách của lượt trước lặp lại nguyên vẹn ở lượt sau.
      { key: 'remindGapDays', type: 'number', label: 'Nhắc lại sau bao nhiêu ngày', default: 7, min: 0, max: 365,
        hint: 'Đã đưa vào danh sách rồi thì im bấy nhiêu ngày, không nhắc lại mỗi sáng. Để 0 = không giới hạn (nhắc lại ngay lượt sau) — chỉ nên dùng khi bạn chạy tác vụ rất thưa.' },
      { key: 'maxReminders', type: 'number', label: 'Mỗi khách nhắc tối đa mấy lần', default: 3, min: 0, max: 20,
        hint: 'Nhắc đủ số lần này mà vẫn chưa ai gọi thì thôi — nhắc mãi chỉ làm người ta bỏ qua cả danh sách. Bộ đếm TỰ VỀ 0 khi khách được chăm sóc thật (ngày chăm sóc trong CRM đổi), nên khách đã gọi rồi mà sau này ngủ quên lại vẫn được nhắc tiếp. Để 0 = không giới hạn số lần.' },
    ],
    'customer-auto-review': [
      { key: 'createdWithinDays', type: 'number', label: 'Chỉ khách tạo trong (ngày)', default: 30, min: 1, max: 365,
        hint: 'Chỉ review khách được tạo trong khoảng ngày gần đây này. Khách cũ hơn được bỏ qua ở lần review đầu.' },
      { key: 'reReview', type: 'bool', label: 'Tự động review lại định kỳ', default: true,
        hint: 'Bật để định kỳ chấm lại những khách đã review (theo chu kỳ bên dưới). Tắt = chỉ review khách mới.' },
      { key: 'reReviewDays', type: 'number', label: 'Chấm lại sau mỗi (ngày)', showIf: 'reReview', default: 30, min: 1, max: 365,
        hint: 'Bao lâu thì chấm lại một khách kể từ lần review trước. 30 ngày ≈ mỗi tháng, 90 ≈ mỗi quý, 7 = mỗi tuần.' },
    ],
  };

  // Nhóm option theo chức năng (cho tiêu đề mục trong card) — gọn, dễ quét. key option → tên nhóm.
  const OPTION_GROUPS = {
    'mail-auto-sync': {
      autoReply: 'Tự động trả lời', replyMode: 'Tự động trả lời',
      replyCategories: 'Tự động trả lời', replyTone: 'Tự động trả lời',
    },
    'deal-auto-review': {
      statuses: '① Phạm vi xử lý', createdWithinDays: '① Phạm vi xử lý',
      autoReview: '② Chấm điểm cơ hội (AI)', reviewMax: '② Chấm điểm cơ hội (AI)',
      reReview: '② Chấm điểm cơ hội (AI)', reReviewDays: '② Chấm điểm cơ hội (AI)', maxAutoReviews: '② Chấm điểm cơ hội (AI)',
      alertCooling: '③ Cảnh báo cơ hội nguội', coolingStatuses: '③ Cảnh báo cơ hội nguội', coolingDays: '③ Cảnh báo cơ hội nguội',
      minWinRateToNotify: '③ Cảnh báo cơ hội nguội', maxNotifications: '③ Cảnh báo cơ hội nguội', notifyMinGapHours: '③ Cảnh báo cơ hội nguội',
    },
    'sale-brief': {
      secCooling: '① Cơ hội cần gọi lại', callStatuses: '① Cơ hội cần gọi lại', silentDaysMin: '① Cơ hội cần gọi lại',
      secHygiene: '② Cơ hội cần dọn hồ sơ', hygieneStuckDays: '② Cơ hội cần dọn hồ sơ',
      secQuotes: '③ Báo giá bỏ dở', staleQuoteDays: '③ Báo giá bỏ dở',
      secTasks: '④ Việc cần làm', taskStatuses: '④ Việc cần làm',
      secAppointments: '⑤ Các mục chỉ bật/tắt',
      secPayments: '⑤ Các mục chỉ bật/tắt', secVips: '⑤ Các mục chỉ bật/tắt', secMailbox: '⑤ Các mục chỉ bật/tắt',
      useAi: '⑥ Cách trình bày', maxItems: '⑥ Cách trình bày',
    },
    'ceo-brief': {
      comparePeriod: '① Kỳ so sánh',
      secSellers: '② Đưa thêm vào bản tin', sellerCount: '② Đưa thêm vào bản tin',
      secNewDeals: '② Đưa thêm vào bản tin', secAppointments: '② Đưa thêm vào bản tin',
      secAlerts: '② Đưa thêm vào bản tin', secTasks: '② Đưa thêm vào bản tin',
      taskStatuses: '② Đưa thêm vào bản tin',
      secForecast: '③ Dự phóng cuối tháng', revenueTarget: '③ Dự phóng cuối tháng',
      useAi: '③ Cách trình bày', showNumbers: '③ Cách trình bày',
    },
    'payment-watchdog': {
      scanTourTypes: '① Quét những đơn nào',
      windowDays: '② Khi nào thì nhắc', minOutstanding: '② Khi nào thì nhắc',
      paymentStatus: '② Khi nào thì nhắc', maxReminders: '② Khi nào thì nhắc',
      emailEnabled: '③ Báo cho ai', alertEmails: '③ Báo cho ai',
    },
    'tour-readiness': {
      scanTourTypes: '① Quét những đơn nào',
      milestones: '② Nhắc khi nào',
      checkPayment: '③ Kiểm những gì', checkSeats: '③ Kiểm những gì', minSeats: '③ Kiểm những gì',
      checkVisa: '③ Kiểm những gì', visaTourTypes: '③ Kiểm những gì',
      checkNearlyFull: '④ Canh chỗ ngồi', nearlyFullPercent: '④ Canh chỗ ngồi',
      capacityMilestones: '④ Canh chỗ ngồi',
    },
    'customer-auto-care': {
      quietDays: '① Thế nào là ngủ quên', ranks: '② Nhắc về ai',
      requireBought: '② Nhắc về ai', maxLeads: '② Nhắc về ai',
      remindGapDays: '③ Nhắc bao nhiêu lần', maxReminders: '③ Nhắc bao nhiêu lần',
    },
    'anomaly-watchdog': {
      baselineWeeks: '① Mức thường tính thế nào', thresholdPercent: '② Khi nào thì báo',
      alertOnIncrease: '② Khi nào thì báo',
    },
    'customer-auto-review': {
      createdWithinDays: 'Phạm vi',
      reReview: 'Chu kỳ review lại', reReviewDays: 'Chu kỳ review lại',
    },
  };

  // showIf: string (1 key) HOẶC mảng key (AND — chỉ hiện khi TẤT CẢ key bật). Hỗ trợ toggle lồng
  // (vd option con của "chấm lại" chỉ hiện khi vừa bật "chấm điểm" vừa bật "chấm lại").
  function optVisible(opt, options) {
    if (!opt.showIf) return true;
    const keys = Array.isArray(opt.showIf) ? opt.showIf : [opt.showIf];
    return keys.every(k => !!options[k]);
  }

  // Gom default từ schema → {key: default} để pre-fill options khi user mới bật (tránh gửi mảng rỗng).
  function optionDefaults(type) {
    const out = {};
    (WORKFLOW_OPTIONS[type] || []).forEach(o => { if (o.default !== undefined) out[o.key] = o.default; });
    return out;
  }

  // ─── Default ĐỘNG: tính từ danh sách lấy về từ CRM ────────────────────────────────
  //
  // Có những option không thể khai default tĩnh vì lựa chọn là dữ liệu của từng công ty (id trạng
  // thái cơ hội mỗi nơi một khác). Để trống thì người dùng mở ra thấy "Tất cả trạng thái" và phải tự
  // đoán nên tick gì — mà tick sai thì bản tin nhắc gọi lại đơn đã hủy (đúng lỗi đã gặp thật).
  // Nên: khi danh sách về mà option CHƯA từng được khai, chọn sẵn các trạng thái còn phải chăm.

  // Bỏ dấu + gộp khoảng trắng để so tên trạng thái do công ty tự đặt.
  function viNorm(s) {
    return String(s || '').toLowerCase()
      // Viết dải dấu bằng \u… thay vì ký tự thật: dấu tổ hợp dán thẳng vào regex là thứ vô hình,
      // một lần lưu sai bảng mã là im lặng hỏng mà nhìn code không thấy gì bất thường.
      .normalize('NFD').replace(/[\u0300-\u036f]/g, '').replace(/\u0111/g, 'd')
      .replace(/[^a-z0-9]+/g, ' ').trim();
  }

  // Trạng thái coi là ĐÃ ĐÓNG (không nhắc gọi nữa). Bám theo DealCooling.IsClosedWon bên C#, thêm
  // "hủy" và nhánh thua ("thất bại", "từ chối", "không thành công") — server bỏ đơn hủy theo MÃ
  // trạng thái nên danh sách theo TÊN của nó không có mấy từ này; ở đây chọn theo tên thì phải có,
  // nếu không mặc định lại tick nhầm đúng những đơn mà bản tin không nên nhắc.
  const CLOSED_STATUS_WORDS = [
    'huy', 'chot don', 'da chot', 'thanh cong', 'hoan thanh', 'hoan tat', 'da ban',
    'that bai', 'tu choi', 'khong thanh cong', 'da dong',
  ];

  function isClosedStatusName(label) {
    const s = viNorm(label);
    if (!s) return false;
    // "không thành công" chứa "thành công" → xét nhánh thua trước.
    if (s.includes('khong thanh cong') || s.includes('that bai') || s.includes('tu choi')) return true;
    return CLOSED_STATUS_WORDS.some(w => s.includes(w));
  }

  const DYNAMIC_DEFAULTS = {
    openDealStatuses: list => (list || []).filter(o => !isClosedStatusName(o.label)).map(o => o.value),
    // Công việc có THÊM một tín hiệu chắc hơn tên: mã 4/5 là "hoàn thành"/"hủy" trong chính CRM
    // (cả tab "trễ hạn" lẫn cờ IsLate của CRM đều loại đúng 2 mã này). Dùng cả hai — mã bắt được
    // trường hợp đổi tên lạ, tên bắt được trường hợp công ty dùng mã 3 làm trạng thái kết thúc.
    openTaskStatuses: list => (list || [])
      .filter(o => o.value !== 4 && o.value !== 5 && !isClosedStatusName(o.label))
      .map(o => o.value),
  };

  /// Trả về patch {key: value} cho những option có dynamicDefault mà người dùng CHƯA từng khai.
  /// CHƯA khai = `undefined`. Mảng rỗng KHÔNG tính là chưa khai — bỏ chọn hết rồi lưu là một lựa
  /// chọn có chủ đích, điền lại giúp là ghi đè ý người dùng.
  ///
  /// `suggested` là gợi ý do MÁY CHỦ tính (AI đọc tên trạng thái của chính công ty đó). Ưu tiên nó
  /// vì AI hiểu được "Kết thúc", "Win", "Đã bàn giao" — thứ mà bảng từ khoá dưới đây chịu. Bảng từ
  /// khoá chỉ là lưới đỡ khi máy chủ không trả gợi ý (AI lỗi, chưa khai khoá model…).
  function dynamicDefaults(type, options, dynOptions, suggested) {
    const patch = {};
    (WORKFLOW_OPTIONS[type] || []).forEach(o => {
      if (!o.dynamicDefault || options[o.key] !== undefined) return;
      const list = (dynOptions || {})[o.dynamic] || [];
      if (!list.length) return;                       // chưa tải xong → để lần sau
      const fromServer = (suggested || {})[o.dynamic];
      if (Array.isArray(fromServer) && fromServer.length) { patch[o.key] = fromServer; return; }
      const fn = DYNAMIC_DEFAULTS[o.dynamicDefault];
      if (fn) patch[o.key] = fn(list);
    });
    return patch;
  }

  // Field bắt buộc đang trống? (chỉ tính field đang hiện + có dữ liệu để chọn)
  function optEmpty(opt, options, dynOptions) {
    const v = options[opt.key];
    if (opt.type === 'multi') {
      const opts = opt.dynamic ? ((dynOptions || {})[opt.dynamic] || []) : (opt.options || []);
      return opts.length > 0 && (!Array.isArray(v) || v.length === 0);   // chỉ bắt buộc khi đã có list để chọn
    }
    if (opt.type === 'numbers') return !Array.isArray(v) || v.length === 0;
    return v == null || v === '';
  }

  // ─── OptHelp — dấu ? cạnh nhãn, rê chuột mới hiện lời giải thích ──────────────────
  //
  // Trước đây mỗi ô có 1–3 dòng chữ xám nằm dưới, cộng lại chiếm quá nửa chiều cao form: mở thẻ ra
  // là một bức tường chữ, mà phần lớn chỉ cần đọc một lần. Đưa vào tooltip thì form nhìn hết được
  // trong một màn, chữ vẫn còn nguyên cho ai cần.
  //
  // Dùng <button> chứ không phải <span>: bàn phím tab tới được và Enter/Space mở ra — rê chuột là
  // thao tác mà điện thoại và người dùng bàn phím không có, tooltip chỉ-hover là mất chữ với họ.
  function OptHelp({ text, tone }) {
    const [open, setOpen] = uS(false);
    if (!text) return null;
    return (
      <button type="button"
        className={'wf-help' + (tone === 'warn' ? ' is-warn' : '') + (open ? ' is-open' : '')}
        aria-label={tone === 'warn' ? 'Cảnh báo' : 'Giải thích'}
        title=""
        onClick={e => { e.preventDefault(); setOpen(v => !v); }}
        onBlur={() => setOpen(false)}>
        {tone === 'warn' ? '!' : '?'}
        <span className="wf-help-bubble" role="tooltip">{text}</span>
      </button>
    );
  }

  // ─── MultiSelect (select2-style) — chip + dropdown checklist cho options động ──────

  function MultiSelectDropdown({ options, value, onChange, placeholder, loading }) {
    const [open, setOpen] = uS(false);
    const ref = React.useRef(null);
    uE(() => {
      if (!open) return;
      const h = e => { if (ref.current && !ref.current.contains(e.target)) setOpen(false); };
      document.addEventListener('mousedown', h);
      return () => document.removeEventListener('mousedown', h);
    }, [open]);
    const sel = Array.isArray(value) ? value : [];
    const labelOf = v => { const o = options.find(x => x.value === v); return o ? o.label : v; };
    const toggle = v => onChange(sel.includes(v) ? sel.filter(x => x !== v) : [...sel, v]);
    return (
      <div className="wf-ms" ref={ref}>
        <div className={'wf-ms-control' + (open ? ' open' : '')} onClick={() => setOpen(o => !o)}>
          <div className="wf-ms-tags">
            {sel.length === 0
              ? <span className="wf-ms-ph">{loading ? 'Đang tải…' : (placeholder || 'Tất cả')}</span>
              : sel.map(v => (
                <span className="wf-ms-tag" key={v}>
                  {labelOf(v)}
                  <span className="wf-ms-x" onClick={e => { e.stopPropagation(); toggle(v); }}>×</span>
                </span>
              ))}
          </div>
          <span className="wf-ms-chev"><Icon name={open ? 'chevronUp' : 'chevronDown'} size={14} /></span>
        </div>
        {open && (
          <div className="wf-ms-menu">
            {options.length === 0
              ? <div className="wf-ms-empty">{loading ? 'Đang tải…' : 'Chưa lấy được trạng thái — kiểm tra kết nối CRM / tài khoản.'}</div>
              : options.map(o => (
                <label key={o.value} className={'wf-ms-item' + (sel.includes(o.value) ? ' on' : '')}>
                  <input type="checkbox" checked={sel.includes(o.value)} onChange={() => toggle(o.value)} />
                  <span>{o.label}</span>
                </label>
              ))}
          </div>
        )}
      </div>
    );
  }

  // ─── Ô nhập của 1 option, theo type (bool/select/multi/number/numbers) ─────────
  // Trước là hàm renderControl() nằm trong WorkflowCard (đóng gói options/setOptions/dynOptions).
  // Nay nhận qua props để trang nào cũng dùng được. Markup + class GIỮ NGUYÊN.

  function OptionControl({ opt, options, setOptions, dynOptions, dynLoading }) {
    dynOptions = dynOptions || {};
    dynLoading = dynLoading || {};

    if (opt.type === 'bool') return (
      <div className="workflows-toggle-wrap">
        <label className="workflows-toggle">
          <input type="checkbox" checked={!!options[opt.key]}
            onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.checked }))} />
          <span className="workflows-toggle-track" />
        </label>
        <span className="workflows-toggle-label">{options[opt.key] ? 'Bật' : 'Tắt'}</span>
      </div>
    );
    if (opt.type === 'select') return (
      <select className="workflows-select workflows-opt-input"
        value={options[opt.key] || opt.options[0].value}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value }))}>
        {opt.options.map(o => <option key={o.value} value={o.value}>{o.label}</option>)}
      </select>
    );
    if (opt.type === 'multi') {
      // Options ĐỘNG (vd trạng thái deal từ CRM) → multi-select dropdown (select2-style).
      if (opt.dynamic) return (
        <MultiSelectDropdown
          options={dynOptions[opt.dynamic] || []}
          value={options[opt.key]}
          loading={!!dynLoading[opt.dynamic]}
          placeholder="Tất cả trạng thái"
          onChange={vals => setOptions(o => ({ ...o, [opt.key]: vals }))} />
      );
      // Options TĨNH (vd nhóm mail) → chips.
      return (
        <div className="workflows-chips">
          {opt.options.map(o => {
            const arr = Array.isArray(options[opt.key]) ? options[opt.key] : [];
            const on = arr.includes(o.value);
            return (
              <label key={o.value} className={'workflows-chip' + (on ? ' on' : '')}>
                <input type="checkbox" checked={on} style={{ display: 'none' }}
                  onChange={() => setOptions(prev => {
                    const cur = Array.isArray(prev[opt.key]) ? prev[opt.key] : [];
                    return { ...prev, [opt.key]: on ? cur.filter(x => x !== o.value) : [...cur, o.value] };
                  })} />
                {o.label}
              </label>
            );
          })}
        </div>
      );
    }
    if (opt.type === 'number') return (
      <input type="number" className="workflows-select workflows-opt-num"
        min={opt.min} max={opt.max}
        value={options[opt.key] ?? opt.default ?? 0}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value === '' ? 0 : Number(e.target.value) }))} />
    );
    if (opt.type === 'numbers') return (
      <input type="text" className="workflows-select workflows-opt-input"
        placeholder="để trống = tất cả"
        value={(Array.isArray(options[opt.key]) ? options[opt.key] : []).join(', ')}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value.split(',').map(x => parseInt(x.trim(), 10)).filter(n => !isNaN(n) && n > 0) }))} />
    );
    // Chữ tự do (vd danh sách email nhận cảnh báo). Lưu NGUYÊN chuỗi người dùng gõ, cắt/lọc ở
    // máy chủ — cắt ngay trong ô nhập thì đang gõ dở dấu phẩy là chữ nhảy lung tung dưới tay.
    if (opt.type === 'text') return (
      <input type="text" className="workflows-select workflows-opt-input"
        placeholder={opt.placeholder || ''}
        value={options[opt.key] ?? opt.default ?? ''}
        onChange={e => setOptions(o => ({ ...o, [opt.key]: e.target.value }))} />
    );
    // Kiểu lạ thì KÊU LÊN, đừng trả null im lặng: ô biến mất khỏi form mà không lỗi gì là bẫy đã
    // dính một lần — cấu hình trông như đã lưu nhưng thực ra chưa bao giờ nhập được.
    console.error('[workflow-options] type không hỗ trợ:', opt.type, '— option', opt.key);
    return <div className="workflows-opt-hint">⚠️ Không hiển thị được ô này (kiểu "{opt.type}").</div>;
  }

  window.tourkitWorkflowOptions = {
    INTERVAL_OPTIONS, MAIL_CATEGORIES, MAIL_TONES,
    WORKFLOW_OPTIONS, OPTION_GROUPS,
    optVisible, optionDefaults, optEmpty, dynamicDefaults, isClosedStatusName,
    MultiSelectDropdown, OptionControl, OptHelp,
  };
})();
