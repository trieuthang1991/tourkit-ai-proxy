// Demo data + helpers for AI Tour Quy Nhơn
window.fmtVND = function(n) {
  if (n == null || isNaN(n)) return '0đ';
  return Math.round(n).toLocaleString('vi-VN').replace(/,/g, '.') + 'đ';
};
window.fmtNum = function(n) {
  if (n == null || isNaN(n)) return '0';
  return Math.round(n).toLocaleString('vi-VN').replace(/,/g, '.');
};

window.PREFERENCES = [
  'Nghỉ dưỡng', 'Team Building', 'Gala Dinner', 'Chụp ảnh',
  'Ẩm thực biển', 'Massage', 'Nhậu đêm', 'Khám phá văn hóa',
  'Trekking', 'Mua sắm'
];

window.DEMO_REQUEST = {
  code: 'GRPS-4839',
  route: 'Hà Nội - Quy Nhơn',
  adults: 200,
  children: 15,
  days: 3,
  nights: 2,
  startDate: '2026-06-15',
  budgetPerPax: 7000000,
  preferences: ['Team Building', 'Gala Dinner', 'Nhậu đêm', 'Ẩm thực biển'],
  notes: 'Khách muốn có 1 ngày vui chơi tự do. Đề xuất các điểm ăn chơi tự do trong lịch trình cho thành viên trong đoàn. Đoàn cần không gian event cho Gala Dinner.'
};

window.DEMO_ITINERARY = [
  {
    day: 1,
    title: 'Hành trình di sản & Khám phá tinh hoa Bình Định',
    activities: [
      { id: 'a1', time: '08:00', type: 'TRANSPORT', title: 'Chào đón đoàn tại Sân bay Phù Cát',
        description: 'Xe du lịch Universe 45 chỗ đời mới đón đoàn. Hướng dẫn viên chuyên nghiệp chào đón khách với khăn lạnh và nước suối.',
        cost: 1500000, supplier: 'Minh Hải Travel' },
      { id: 'a2', time: '10:00', type: 'SIGHTSEEING', title: 'Tham quan Quần thể Chùa Thiên Hưng',
        description: 'Khám phá "Phượng Hoàng Cổ Trấn" phiên bản Việt. Chiêm bái xá lợi Phật và vãn cảnh kiến trúc tâm linh độc đáo giữa lòng Bình Định.',
        cost: 1500000, supplier: 'Vé tham quan' },
      { id: 'a3', time: '12:00', type: 'MEAL', title: 'Thưởng thức Ẩm thực Cơm Niêu truyền thống',
        description: 'Thực đơn 8 món đặc sản Bình Định: bánh xèo tôm nhảy, gỏi cá mai, nem chua chợ Huyện, bún chả cá Quy Nhơn...',
        cost: 8000000, supplier: 'Cơm Niêu Bốn Mùa' },
      { id: 'a4', time: '14:00', type: 'HOTEL', title: 'Trải nghiệm nghỉ dưỡng tại FLC Quy Nhơn Resort',
        description: 'Check-in resort 5 sao bên bờ biển. 20 phòng deluxe view biển, hồ bơi vô cực, spa và khu vực giải trí riêng cho đoàn.',
        cost: 45000000, supplier: 'FLC Quy Nhơn Resort' },
      { id: 'a5', time: '18:30', type: 'ACTIVITY', title: 'Gala Dinner: "Vươn Xa Biển Lớn 2026"',
        description: 'Sân khấu bãi biển trọn gói: âm thanh, ánh sáng, MC, ban nhạc live, vinh danh nhân viên và tiệc buffet 30 món.',
        cost: 25000000, supplier: 'Event Coast' }
    ]
  },
  {
    day: 2,
    title: 'Thiên đường biển Kỳ Co & Sự kiện Team Building gắn kết',
    activities: [
      { id: 'b1', time: '07:00', type: 'MEAL', title: 'Buffet sáng tại resort', description: 'Buffet sáng 60 món Á - Âu tại nhà hàng chính của FLC Quy Nhơn.', cost: 0, supplier: 'FLC Quy Nhơn Resort' },
      { id: 'b2', time: '08:00', type: 'ACTIVITY', title: 'Hành trình biển đảo Kỳ Co - Eo Gió',
        description: 'Cano cao tốc đưa đoàn ra Kỳ Co - "Maldives của Việt Nam". Tắm biển, lặn ngắm san hô, chơi mô tô nước.',
        cost: 18000000, supplier: 'Kỳ Co Tours' },
      { id: 'b3', time: '12:30', type: 'MEAL', title: 'Hải sản tươi sống tại Nhà hàng Ngọc Duy', description: 'Hải sản đầm Thị Nại: tôm hùm baby, ghẹ xanh, mực ống nướng muối ớt, ốc hương rang muối.', cost: 9000000, supplier: 'Ngọc Duy SEAFOOD' },
      { id: 'b4', time: '15:00', type: 'ACTIVITY', title: 'Team Building: "Đột Phá Giới Hạn"',
        description: '8 thử thách trên cát: kéo co, vượt chướng ngại vật, xếp tháp người, đào kho báu. Đội thắng nhận giải thưởng.',
        cost: 22000000, supplier: 'TB Pro Vietnam' },
      { id: 'b5', time: '19:30', type: 'MEAL', title: 'Tiệc nhậu đêm bãi biển + BBQ', description: 'BBQ tự do bên bờ biển, bia tươi, hải sản nướng, âm nhạc acoustic.', cost: 12000000, supplier: 'Beach Bar QN' }
    ]
  },
  {
    day: 3,
    title: 'Mua sắm đặc sản địa phương & Chia tay đoàn',
    activities: [
      { id: 'c1', time: '07:00', type: 'MEAL', title: 'Buffet sáng & Check-out resort', description: 'Trả phòng. Tự do tắm biển, dạo bộ ven biển trước khi khởi hành.', cost: 0, supplier: 'FLC Quy Nhơn Resort' },
      { id: 'c2', time: '09:00', type: 'SIGHTSEEING', title: 'Trải nghiệm mua sắm Chợ đặc sản Bình Định', description: 'Tự do mua đặc sản: rượu Bàu Đá, bánh tráng nước dừa, nem chua chợ Huyện, mực một nắng.', cost: 0, supplier: 'Tự túc' },
      { id: 'c3', time: '11:00', type: 'TRANSPORT', title: 'Tiễn đoàn ra Sân bay Phù Cát', description: 'Xe đưa đoàn ra sân bay. Hướng dẫn viên tiễn đoàn lên máy bay về Hà Nội.', cost: 1500000, supplier: 'Minh Hải Travel' }
    ]
  }
];

window.DEMO_GUIDE_COST = 3000000;

window.SUPPLIER_LIBRARY = {
  HOTEL: [
    { name: 'Khách sạn Anyia Hotel 4*', desc: 'Vị trí trung tâm, tiêu chuẩn cao cấp', price: 32000000, ncc: 'Anyia Group' },
    { name: 'Khách sạn Fleur De Lys 4*', desc: 'Phong cách Pháp sang trọng', price: 38000000, ncc: 'Fleur De Lys' },
    { name: 'FLC Quy Nhơn Resort 5*', desc: 'Resort biển 5 sao, không gian event lớn', price: 45000000, ncc: 'FLC Hospitality' }
  ],
  MEAL: [
    { name: 'Nhà hàng Ngọc Duy', desc: 'Chuyên hải sản đầm Thị Nại', price: 7500000, ncc: 'Ngọc Duy SEAFOOD' },
    { name: 'Cơm Niêu Bốn Mùa', desc: 'Ẩm thực truyền thống Bình Định', price: 8000000, ncc: 'Bốn Mùa Group' },
    { name: 'Beach BBQ Bar', desc: 'Tiệc nhậu BBQ bãi biển', price: 12000000, ncc: 'Beach Bar QN' }
  ],
  TRANSPORT: [
    { name: 'Xe 45 chỗ Minh Hải', desc: 'Dòng xe Hyundai Universe 2024', price: 1200000, ncc: 'Minh Hải Travel' },
    { name: 'Xe 45 chỗ Phú Quý', desc: 'Đời mới, có WiFi & nước miễn phí', price: 1350000, ncc: 'Phú Quý Travel' }
  ],
  SIGHTSEEING: [
    { name: 'Vé Quần thể Chùa Thiên Hưng', desc: 'Bao gồm hướng dẫn tại điểm', price: 1500000, ncc: 'Tourkit Internal' },
    { name: 'Tour Kỳ Co - Eo Gió', desc: 'Cano cao tốc + lặn san hô', price: 18000000, ncc: 'Kỳ Co Tours' }
  ],
  ACTIVITY: [
    { name: 'Gala Dinner trọn gói', desc: 'Âm thanh, ánh sáng, MC, ban nhạc, tiệc buffet', price: 25000000, ncc: 'Event Coast' },
    { name: 'Team Building Pro 8 trạm', desc: 'MC chuyên nghiệp, huấn luyện viên, dụng cụ', price: 22000000, ncc: 'TB Pro Vietnam' }
  ],
  GUIDE: [
    { name: 'Hướng dẫn viên VIP', desc: 'Kinh nghiệm trên 10 năm, am hiểu lịch sử', price: 3000000, ncc: 'Tourkit Internal' },
    { name: 'HDV cấp Quốc gia', desc: 'Chứng chỉ HDV Quốc gia, đa ngôn ngữ', price: 4500000, ncc: 'VTGA' }
  ]
};

window.SERVICE_TYPES = {
  TRANSPORT: { label: 'Vận chuyển', icon: 'bus' },
  SIGHTSEEING: { label: 'Tham quan', icon: 'camera' },
  MEAL: { label: 'Ăn uống', icon: 'utensils' },
  HOTEL: { label: 'Lưu trú', icon: 'bed' },
  ACTIVITY: { label: 'Hoạt động', icon: 'star' },
  GUIDE: { label: 'Hướng dẫn viên', icon: 'user' }
};

window.COSTING_ROWS = [
  { service: 'Khách sạn 5*', supplier: 'FLC Quy Nhơn Resort', verified: true, qty: '2 Đêm (20 Phòng)', priceNet: 45000000, vat: 10, markup: 15, type: 'HOTEL' },
  { service: 'Xe du lịch', supplier: 'Nhà xe Minh Hải', verified: true, qty: 'Xe 45 Chỗ', priceNet: 8500000, vat: 8, markup: 20, type: 'TRANSPORT' },
  { service: 'Nhà hàng', supplier: 'Cơm Niêu Bốn Mùa', verified: true, qty: '40 Suất x 2', priceNet: 16000000, vat: 8, markup: 10, type: 'MEAL' },
  { service: 'Gala Dinner', supplier: 'Sân khấu bãi biển', verified: false, qty: 'Gói trọn gói', priceNet: 25000000, vat: 10, markup: 30, type: 'ACTIVITY' },
  { service: 'Tham quan', supplier: 'Vé Kỳ Co + Cano', verified: true, qty: '40 Vé lớn', priceNet: 18000000, vat: 0, markup: 20, type: 'SIGHTSEEING' },
  { service: 'Team Building', supplier: 'TB Pro Vietnam', verified: true, qty: 'Gói 8 trạm', priceNet: 22000000, vat: 10, markup: 25, type: 'ACTIVITY' },
  { service: 'Hướng dẫn viên', supplier: 'Tourkit Internal', verified: true, qty: '1 HDV VIP', priceNet: 3000000, vat: 0, markup: 15, type: 'GUIDE' }
];
