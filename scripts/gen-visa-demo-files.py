# scripts/gen-visa-demo-files.py
# Sinh 6 file PDF mau ho so xin visa de demo trang /visa.
# Vietnamese support via DejaVuSans (built-in reportlab).
# Watermark "MAU DEMO - KHONG GIA TRI PHAP LY" cheo toan trang.
#
# Run: python scripts/gen-visa-demo-files.py
# Out: wwwroot/demo/visa/*.pdf (6 file)

import os
from pathlib import Path
from reportlab.lib.pagesizes import A4
from reportlab.lib.units import cm
from reportlab.lib.colors import HexColor, black, gray
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas
from reportlab.platypus import SimpleDocTemplate, Paragraph, Spacer, Table, TableStyle, Image
from reportlab.lib.styles import getSampleStyleSheet, ParagraphStyle
from reportlab.lib.enums import TA_LEFT, TA_CENTER, TA_RIGHT

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / "wwwroot" / "demo" / "visa"
OUT.mkdir(parents=True, exist_ok=True)

# DejaVu Sans support full Vietnamese diacritics, ships with reportlab
DEJAVU = ROOT / "tmp" / "DejaVuSans.ttf"
DEJAVU_BOLD = ROOT / "tmp" / "DejaVuSans-Bold.ttf"

# Try locate DejaVu in common system paths
import sys
candidates = [
    Path(sys.prefix) / "Lib" / "site-packages" / "reportlab" / "fonts" / "DejaVuSans.ttf",
    Path("C:/Windows/Fonts/DejaVuSans.ttf"),
    Path("C:/Windows/Fonts/seguiemj.ttf"),
    Path("C:/Windows/Fonts/arial.ttf"),
    Path("C:/Windows/Fonts/calibri.ttf"),
]
candidates_bold = [
    Path(sys.prefix) / "Lib" / "site-packages" / "reportlab" / "fonts" / "DejaVuSans-Bold.ttf",
    Path("C:/Windows/Fonts/arialbd.ttf"),
    Path("C:/Windows/Fonts/calibrib.ttf"),
]
def find_font(cands):
    for c in cands:
        if c.exists(): return c
    return None
font_regular = find_font(candidates)
font_bold = find_font(candidates_bold) or font_regular
if not font_regular:
    print("ERROR: Khong tim thay font ho tro Vietnamese. Vui long cai DejaVu Sans hoac chinh duong dan.")
    sys.exit(1)

pdfmetrics.registerFont(TTFont("VNS", str(font_regular)))
pdfmetrics.registerFont(TTFont("VNB", str(font_bold)))

styles = getSampleStyleSheet()
ST_TITLE = ParagraphStyle("title", parent=styles["Heading1"], fontName="VNB", fontSize=16, textColor=HexColor("#0f172a"), alignment=TA_CENTER, spaceAfter=14)
ST_H2 = ParagraphStyle("h2", parent=styles["Heading2"], fontName="VNB", fontSize=12, textColor=HexColor("#0f172a"), spaceAfter=8)
ST_BODY = ParagraphStyle("body", parent=styles["BodyText"], fontName="VNS", fontSize=10.5, textColor=HexColor("#1f2937"), leading=15)
ST_SMALL = ParagraphStyle("sm", parent=styles["BodyText"], fontName="VNS", fontSize=9, textColor=HexColor("#64748b"), leading=12)
ST_LABEL = ParagraphStyle("lbl", parent=styles["BodyText"], fontName="VNB", fontSize=9.5, textColor=HexColor("#475569"))

def watermark(canv, doc):
    canv.saveState()
    canv.setFont("VNB", 60)
    canv.setFillColor(HexColor("#fee2e2"))
    canv.translate(A4[0]/2, A4[1]/2)
    canv.rotate(35)
    canv.drawCentredString(0, 0, "MẪU DEMO")
    canv.setFont("VNB", 14)
    canv.setFillColor(HexColor("#dc2626"))
    canv.drawCentredString(0, -40, "KHÔNG CÓ GIÁ TRỊ PHÁP LÝ")
    canv.restoreState()
    # Footer
    canv.setFont("VNS", 7)
    canv.setFillColor(HexColor("#94a3b8"))
    canv.drawString(2*cm, 1*cm, "Tạo bởi TRAV-AI · File mẫu để demo wizard chấm điểm visa · Không dùng cho mục đích nộp lãnh sự")

def make_pdf(filename, builder):
    path = OUT / filename
    doc = SimpleDocTemplate(str(path), pagesize=A4,
        leftMargin=2.2*cm, rightMargin=2.2*cm, topMargin=2*cm, bottomMargin=2*cm)
    story = builder()
    doc.build(story, onFirstPage=watermark, onLaterPages=watermark)
    print(f"  -> {filename} ({path.stat().st_size//1024} KB)")

# ============================================================================
# 1. HỘ CHIẾU (passport) — trang thông tin
# ============================================================================
def passport():
    s = []
    s.append(Paragraph("HỘ CHIẾU PHỔ THÔNG · ORDINARY PASSPORT", ST_TITLE))
    s.append(Spacer(1, 6))
    s.append(Paragraph("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM<br/>SOCIALIST REPUBLIC OF VIETNAM", ST_H2))
    s.append(Spacer(1, 14))
    info = [
        ["Loại / Type", "P (Phổ thông)"],
        ["Mã số / Code", "VNM"],
        ["Số hộ chiếu / Passport No.", "B12345678"],
        ["Họ và tên / Full name", "NGUYỄN VĂN A"],
        ["Quốc tịch / Nationality", "VIETNAMESE"],
        ["Ngày sinh / Date of birth", "15 / 03 / 1990"],
        ["Giới tính / Sex", "M"],
        ["Nơi sinh / Place of birth", "HÀ NỘI"],
        ["Ngày cấp / Date of issue", "10 / 01 / 2023"],
        ["Có giá trị đến / Date of expiry", "10 / 01 / 2033"],
        ["Cơ quan cấp / Authority", "CỤC QUẢN LÝ XUẤT NHẬP CẢNH"],
    ]
    t = Table(info, colWidths=[6*cm, 8*cm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (0,-1), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 10.5),
        ("TEXTCOLOR", (0,0), (0,-1), HexColor("#475569")),
        ("TEXTCOLOR", (1,0), (1,-1), HexColor("#0f172a")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("VALIGN", (0,0), (-1,-1), "MIDDLE"),
        ("LEFTPADDING", (0,0), (-1,-1), 4),
        ("RIGHTPADDING", (0,0), (-1,-1), 4),
        ("TOPPADDING", (0,0), (-1,-1), 7),
        ("BOTTOMPADDING", (0,0), (-1,-1), 7),
    ]))
    s.append(t)
    s.append(Spacer(1, 18))
    # MRZ-like line
    s.append(Paragraph("P&lt;VNMNGUYEN&lt;&lt;VAN&lt;A&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;<br/>B12345678&lt;9VNM9003150M3301106&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;&lt;06", ST_SMALL))
    return s

# ============================================================================
# 2. CCCD/CMND
# ============================================================================
def idcard():
    s = []
    s.append(Paragraph("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM", ST_TITLE))
    s.append(Paragraph("Độc lập · Tự do · Hạnh phúc", ParagraphStyle("center", parent=ST_BODY, alignment=TA_CENTER, fontName="VNS", fontSize=10)))
    s.append(Spacer(1, 8))
    s.append(Paragraph("CĂN CƯỚC CÔNG DÂN", ParagraphStyle("ct", parent=ST_TITLE, fontSize=20, textColor=HexColor("#9a3412"))))
    s.append(Paragraph("CITIZEN IDENTITY CARD", ParagraphStyle("ct2", parent=ST_BODY, alignment=TA_CENTER, fontName="VNS", fontSize=9, textColor=HexColor("#94a3b8"))))
    s.append(Spacer(1, 16))
    info = [
        ["Số / No.", "001090012345"],
        ["Họ và tên / Full name", "NGUYỄN VĂN A"],
        ["Ngày sinh / Date of birth", "15/03/1990"],
        ["Giới tính / Sex", "Nam"],
        ["Quốc tịch / Nationality", "Việt Nam"],
        ["Quê quán / Place of origin", "Xã Liên Mạc, Huyện Mê Linh, TP. Hà Nội"],
        ["Nơi thường trú / Place of residence", "Số 12, Ngõ 34, P. Đống Đa, Q. Đống Đa, TP. Hà Nội"],
        ["Đặc điểm nhận dạng", "Nốt ruồi 1cm cạnh mắt phải"],
        ["Ngày cấp / Date of issue", "12/05/2022"],
        ["Có giá trị đến / Valid until", "15/03/2065"],
    ]
    t = Table(info, colWidths=[6*cm, 9*cm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (0,-1), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 10.5),
        ("TEXTCOLOR", (0,0), (0,-1), HexColor("#475569")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("VALIGN", (0,0), (-1,-1), "TOP"),
        ("LEFTPADDING", (0,0), (-1,-1), 4),
        ("TOPPADDING", (0,0), (-1,-1), 6),
        ("BOTTOMPADDING", (0,0), (-1,-1), 6),
    ]))
    s.append(t)
    return s

# ============================================================================
# 3. SỔ BHXH
# ============================================================================
def bhxh():
    s = []
    s.append(Paragraph("BẢO HIỂM XÃ HỘI VIỆT NAM", ST_H2))
    s.append(Paragraph("Bảo hiểm xã hội Thành phố Hà Nội", ST_BODY))
    s.append(Spacer(1, 12))
    s.append(Paragraph("TỜ RỜI SỔ BẢO HIỂM XÃ HỘI", ST_TITLE))
    s.append(Paragraph("Mã số: 0123456789", ParagraphStyle("c", parent=ST_BODY, alignment=TA_CENTER)))
    s.append(Spacer(1, 14))
    info = [
        ["Họ và tên", "NGUYỄN VĂN A"],
        ["Số sổ BHXH", "0123456789"],
        ["Ngày sinh", "15/03/1990"],
        ["Số CCCD", "001090012345"],
        ["Đơn vị hiện đang đóng", "Công ty TNHH Du Lịch TRAV-AI"],
        ["Mã đơn vị", "0101234567"],
        ["Bắt đầu đóng", "01/06/2018"],
        ["Tháng đóng đến nay", "84 tháng (7 năm)"],
        ["Mức lương đóng BHXH gần nhất", "25.000.000 VNĐ"],
        ["Tỷ lệ đóng", "10,5% (NLĐ) + 21,5% (NSDLĐ)"],
        ["Tình trạng", "Đang đóng"],
    ]
    t = Table(info, colWidths=[6.5*cm, 8.5*cm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (0,-1), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 10),
        ("TEXTCOLOR", (0,0), (0,-1), HexColor("#475569")),
        ("BACKGROUND", (0,0), (0,-1), HexColor("#f1f5f9")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("LEFTPADDING", (0,0), (-1,-1), 8),
        ("TOPPADDING", (0,0), (-1,-1), 7),
        ("BOTTOMPADDING", (0,0), (-1,-1), 7),
    ]))
    s.append(t)
    s.append(Spacer(1, 16))
    s.append(Paragraph("<b>Lịch sử đóng BHXH</b>", ST_H2))
    history = [
        ["Kỳ", "Đơn vị", "Lương đóng", "Trạng thái"],
        ["06/2018 → 12/2020", "Công ty CP ABC", "15.000.000", "Đã đóng đủ"],
        ["01/2021 → 12/2022", "Công ty TNHH XYZ", "18.000.000", "Đã đóng đủ"],
        ["01/2023 → hiện tại", "TNHH Du Lịch TRAV-AI", "25.000.000", "Đang đóng"],
    ]
    t2 = Table(history, colWidths=[4.5*cm, 5.5*cm, 3*cm, 3*cm])
    t2.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (-1,0), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 9.5),
        ("BACKGROUND", (0,0), (-1,0), HexColor("#0f172a")),
        ("TEXTCOLOR", (0,0), (-1,0), HexColor("#ffffff")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("LEFTPADDING", (0,0), (-1,-1), 6),
        ("TOPPADDING", (0,0), (-1,-1), 6),
        ("BOTTOMPADDING", (0,0), (-1,-1), 6),
    ]))
    s.append(t2)
    return s

# ============================================================================
# 4. SAO KÊ NGÂN HÀNG (6 tháng)
# ============================================================================
def bank_statement():
    s = []
    s.append(Paragraph("NGÂN HÀNG TMCP NGOẠI THƯƠNG VIỆT NAM (VIETCOMBANK)", ST_H2))
    s.append(Paragraph("Chi nhánh Hà Nội · 198 Trần Quang Khải, Hoàn Kiếm, Hà Nội", ST_SMALL))
    s.append(Spacer(1, 12))
    s.append(Paragraph("SAO KÊ TÀI KHOẢN THANH TOÁN", ST_TITLE))
    s.append(Spacer(1, 6))
    info = [
        ["Chủ tài khoản", "NGUYỄN VĂN A"],
        ["Số tài khoản", "0011001234567"],
        ["Loại tài khoản", "Tài khoản thanh toán VNĐ"],
        ["Kỳ sao kê", "01/12/2025 → 31/05/2026 (6 tháng)"],
        ["Số dư đầu kỳ", "152.430.000 VNĐ"],
        ["Số dư cuối kỳ", "287.910.000 VNĐ"],
    ]
    t = Table(info, colWidths=[5.5*cm, 9.5*cm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (0,-1), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 10),
        ("BACKGROUND", (0,0), (0,-1), HexColor("#f8fafc")),
        ("LEFTPADDING", (0,0), (-1,-1), 6),
        ("TOPPADDING", (0,0), (-1,-1), 6),
        ("BOTTOMPADDING", (0,0), (-1,-1), 6),
    ]))
    s.append(t)
    s.append(Spacer(1, 16))
    s.append(Paragraph("<b>Tóm tắt 6 tháng</b>", ST_H2))
    tx = [
        ["Tháng", "Lương vào", "Tổng chi", "Số dư cuối"],
        ["12/2025", "25.000.000", "18.450.000", "159.980.000"],
        ["01/2026", "25.000.000", "19.200.000", "165.780.000"],
        ["02/2026", "25.000.000 + 30.000.000 (thưởng Tết)", "32.500.000", "188.280.000"],
        ["03/2026", "25.000.000", "17.800.000", "195.480.000"],
        ["04/2026", "25.000.000", "21.200.000", "199.280.000"],
        ["05/2026", "25.000.000 + 90.000.000 (thưởng dự án)", "26.370.000", "287.910.000"],
    ]
    t2 = Table(tx, colWidths=[2.8*cm, 5.5*cm, 3.5*cm, 3.5*cm])
    t2.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (-1,0), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 9.5),
        ("BACKGROUND", (0,0), (-1,0), HexColor("#0f172a")),
        ("TEXTCOLOR", (0,0), (-1,0), HexColor("#ffffff")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("LEFTPADDING", (0,0), (-1,-1), 5),
        ("TOPPADDING", (0,0), (-1,-1), 6),
        ("BOTTOMPADDING", (0,0), (-1,-1), 6),
    ]))
    s.append(t2)
    s.append(Spacer(1, 10))
    s.append(Paragraph("<b>Thu nhập trung bình 6 tháng:</b> 28.333.333 VNĐ/tháng (gồm lương + thưởng)", ST_BODY))
    s.append(Paragraph("<b>Dòng tiền ổn định, có tăng trưởng số dư rõ rệt.</b>", ST_BODY))
    return s

# ============================================================================
# 5. SỔ TIẾT KIỆM
# ============================================================================
def savings():
    s = []
    s.append(Paragraph("NGÂN HÀNG TMCP CÔNG THƯƠNG VIỆT NAM (VIETINBANK)", ST_H2))
    s.append(Spacer(1, 12))
    s.append(Paragraph("CHỨNG NHẬN TIỀN GỬI TIẾT KIỆM", ST_TITLE))
    s.append(Spacer(1, 12))
    info = [
        ["Chủ tài khoản tiết kiệm", "NGUYỄN VĂN A"],
        ["Số CCCD", "001090012345"],
        ["Số sổ tiết kiệm", "TK-2026-0987654"],
        ["Loại tiết kiệm", "Có kỳ hạn"],
        ["Kỳ hạn", "12 tháng"],
        ["Ngày gửi", "15/01/2026"],
        ["Ngày đáo hạn", "15/01/2027"],
        ["Lãi suất", "5,4%/năm"],
        ["Số tiền gửi", "350.000.000 VNĐ (Ba trăm năm mươi triệu đồng)"],
        ["Lãi đến hạn", "18.900.000 VNĐ"],
        ["Tổng tiền nhận đáo hạn", "368.900.000 VNĐ"],
        ["Hình thức trả lãi", "Cuối kỳ"],
        ["Trạng thái", "Đang phong toả · không rút trước hạn"],
    ]
    t = Table(info, colWidths=[6*cm, 9*cm])
    t.setStyle(TableStyle([
        ("FONTNAME", (0,0), (-1,-1), "VNS"),
        ("FONTNAME", (0,0), (0,-1), "VNB"),
        ("FONTSIZE", (0,0), (-1,-1), 10),
        ("BACKGROUND", (0,0), (0,-1), HexColor("#f1f5f9")),
        ("LINEBELOW", (0,0), (-1,-1), 0.4, HexColor("#cbd5e1")),
        ("LEFTPADDING", (0,0), (-1,-1), 8),
        ("TOPPADDING", (0,0), (-1,-1), 7),
        ("BOTTOMPADDING", (0,0), (-1,-1), 7),
    ]))
    s.append(t)
    s.append(Spacer(1, 16))
    s.append(Paragraph("Sổ tiết kiệm được phong toả từ ngày phát hành đến ngày đáo hạn. Tiền lãi và gốc chỉ rút được sau ngày 15/01/2027.", ST_SMALL))
    return s

# ============================================================================
# 6. GIẤY XÁC NHẬN CÔNG TÁC
# ============================================================================
def work_cert():
    s = []
    s.append(Paragraph("CÔNG TY TNHH DU LỊCH TRAV-AI", ST_H2))
    s.append(Paragraph("Số 12, Ngõ 34, P. Đống Đa, Q. Đống Đa, TP. Hà Nội", ST_SMALL))
    s.append(Paragraph("MST: 0101234567 · ĐT: (024) 3456-7890", ST_SMALL))
    s.append(Spacer(1, 6))
    s.append(Paragraph("<b>Số: 124/2026/XN-TRAVAI</b>", ParagraphStyle("r", parent=ST_BODY, alignment=TA_RIGHT)))
    s.append(Paragraph("<b>Hà Nội, ngày 02 tháng 06 năm 2026</b>", ParagraphStyle("r", parent=ST_BODY, alignment=TA_RIGHT)))
    s.append(Spacer(1, 18))
    s.append(Paragraph("GIẤY XÁC NHẬN CÔNG TÁC", ST_TITLE))
    s.append(Spacer(1, 8))
    body = """Công ty TNHH Du lịch TRAV-AI xác nhận:<br/><br/>
    Ông/Bà: <b>NGUYỄN VĂN A</b><br/>
    Ngày sinh: 15/03/1990<br/>
    Số CCCD: 001090012345<br/><br/>
    Hiện đang làm việc tại Công ty với các thông tin sau:<br/><br/>
    - <b>Chức vụ:</b> Chuyên viên Kinh doanh cao cấp<br/>
    - <b>Phòng ban:</b> Phòng Kinh doanh Outbound<br/>
    - <b>Thời gian công tác:</b> Từ ngày 01/06/2018 đến nay (84 tháng)<br/>
    - <b>Hợp đồng:</b> Hợp đồng lao động không xác định thời hạn<br/>
    - <b>Mức lương:</b> 25.000.000 VNĐ/tháng (chưa bao gồm thưởng)<br/>
    - <b>Bảo hiểm xã hội:</b> Đang đóng đầy đủ theo quy định<br/><br/>
    Công ty cam kết Ông/Bà NGUYỄN VĂN A được nghỉ phép từ ngày <b>15/07/2026 đến 25/07/2026</b> để thực hiện chuyến đi cá nhân, và sẽ tiếp tục làm việc tại Công ty sau khi trở về.<br/><br/>
    Giấy xác nhận này được cấp để hoàn thiện hồ sơ xin Visa của Ông/Bà NGUYỄN VĂN A.
    """
    s.append(Paragraph(body, ST_BODY))
    s.append(Spacer(1, 30))
    s.append(Paragraph("<b>GIÁM ĐỐC</b>", ParagraphStyle("c", parent=ST_BODY, alignment=TA_CENTER, fontName="VNB", fontSize=11)))
    s.append(Paragraph("(Đã ký và đóng dấu)", ParagraphStyle("c", parent=ST_BODY, alignment=TA_CENTER, fontName="VNS", fontSize=9, textColor=HexColor("#94a3b8"))))
    s.append(Spacer(1, 30))
    s.append(Paragraph("<b>Trần Minh Quân</b>", ParagraphStyle("c", parent=ST_BODY, alignment=TA_CENTER, fontName="VNB", fontSize=11)))
    return s

# ============================================================================
# Build all
# ============================================================================
print("Generating Visa demo files...")
make_pdf("01-ho-chieu-mau.pdf", passport)
make_pdf("02-cccd-mau.pdf", idcard)
make_pdf("03-so-bhxh-mau.pdf", bhxh)
make_pdf("04-sao-ke-ngan-hang-6-thang-mau.pdf", bank_statement)
make_pdf("05-so-tiet-kiem-mau.pdf", savings)
make_pdf("06-giay-xac-nhan-cong-tac-mau.pdf", work_cert)
print(f"\nDone. Files saved to: {OUT}")
