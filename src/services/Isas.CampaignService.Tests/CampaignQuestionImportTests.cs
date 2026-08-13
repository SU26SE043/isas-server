using System.Text;
using Isas.CampaignService.Services;

namespace Isas.CampaignService.Tests;

/// <summary>
/// Nhập câu hỏi hàng loạt từ CSV — <see cref="QuestionCsvImporter"/>.
///
/// <para>Hợp đồng cốt lõi: <b>CHỈ ĐỌC</b>. Endpoint trả danh sách cho HR xem trước; muốn lưu thì HR bấm
/// Lưu và đi qua <c>PUT /questions</c> sẵn có. Nhờ thế guard Draft, audit và merge F10 vẫn nằm đúng một
/// chỗ, và file hỏng mã hoá trở thành vô hại — HR thấy chữ lỗi rồi bấm huỷ, thay vì DB ăn text hỏng.</para>
///
/// Khoá các hành vi: đọc đúng 4 cột · giữ nguyên thứ tự file · dấu phẩy và xuống dòng trong đáp án
/// không làm vỡ · BOM UTF-8 không làm hỏng tên cột đầu · dấu chấm phẩy (Excel locale VN) vẫn đọc được ·
/// byte không phải UTF-8 thì TỪ CHỐI chứ không đoán · lỗi từng dòng không huỷ cả file · file mẫu nhập
/// lại chính nó thì sạch lỗi.
/// </summary>
public class CampaignQuestionImportTests
{
    private static byte[] Utf8(string content, bool bom = false)
    {
        var bytes = new UTF8Encoding(false).GetBytes(content);
        return bom ? new byte[] { 0xEF, 0xBB, 0xBF }.Concat(bytes).ToArray() : bytes;
    }

    private const string Header = "question_text,sample_answer,is_required,nhom\n";

    // ───────────────── đọc đúng ─────────────────

    [Fact]
    public void Doc_duoc_cau_hoi_va_dap_an()
    {
        var csv = Header + "Index là gì?,Cấu trúc giúp tìm nhanh,có,CSDL\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        Assert.Equal(1, result.TotalRows);
        Assert.Empty(result.Errors);
        var q = Assert.Single(result.Questions);
        Assert.Equal("Index là gì?", q.QuestionText);
        Assert.Equal("Cấu trúc giúp tìm nhanh", q.SampleAnswer);
        Assert.Equal("CSDL", q.QuestionGroup);
        Assert.True(q.IsRequired);
        // Mọi dòng trong file là câu MỚI — không Id thì kết quả cắm thẳng vào PUT /questions được.
        Assert.Null(q.Id);
    }

    [Fact]
    public void Giu_nguyen_thu_tu_dong_trong_file()
    {
        var csv = Header + string.Concat(Enumerable.Range(1, 10).Select(i => $"Câu {i},,,\n"));

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        Assert.Equal(
            Enumerable.Range(1, 10).Select(i => $"Câu {i}"),
            result.Questions.Select(q => q.QuestionText));
    }

    [Fact]
    public void Dap_an_co_dau_phay_va_xuong_dong_thi_giu_nguyen_van()
    {
        // Ô có dấu phẩy / xuống dòng phải được bọc nháy trong file — đây là thứ HR lo nhất khi dùng CSV.
        var csv = Header
            + "\"Xử lý sự cố?\",\"Phát hiện, khoanh vùng, rồi khôi phục.\nSau đó viết lại quy trình.\",,\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        var q = Assert.Single(result.Questions);
        Assert.Equal("Phát hiện, khoanh vùng, rồi khôi phục.\nSau đó viết lại quy trình.", q.SampleAnswer);
    }

    [Fact]
    public void Cot_dao_thu_tu_van_map_dung_theo_ten()
    {
        var csv = "nhom,is_required,question_text,sample_answer\n"
                + "Mạng,không,TCP khác UDP thế nào?,TCP tin cậy hơn\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        var q = Assert.Single(result.Questions);
        Assert.Equal("TCP khác UDP thế nào?", q.QuestionText);
        Assert.Equal("TCP tin cậy hơn", q.SampleAnswer);
        Assert.Equal("Mạng", q.QuestionGroup);
        Assert.False(q.IsRequired);
    }

    [Fact]
    public void Cot_thua_thi_bo_qua_khong_bao_loi()
    {
        var csv = "question_text,ghi_chu,sample_answer\nCâu A,nội bộ,Đáp án A\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        Assert.Empty(result.Errors);
        Assert.Equal("Câu A", Assert.Single(result.Questions).QuestionText);
    }

    [Fact]
    public void Chi_co_cot_question_text_van_doc_duoc()
    {
        var result = QuestionCsvImporter.Parse(Utf8("question_text\nCâu A\nCâu B\n"));

        Assert.Equal(2, result.Questions.Count);
        Assert.All(result.Questions, q => Assert.Null(q.SampleAnswer));
        Assert.All(result.Questions, q => Assert.True(q.IsRequired));
    }

    // ───────────────── mã hoá & định dạng ─────────────────

    [Fact]
    public void File_co_BOM_utf8_thi_ten_cot_dau_tien_van_khop()
    {
        // BOM không tự biến mất khi decode bằng GetString → tên cột đầu thành "﻿question_text"
        // và ta báo "thiếu cột" cho một file hoàn toàn đúng. Chính file mẫu ta phát ra CÓ BOM.
        var result = QuestionCsvImporter.Parse(Utf8(Header + "Câu A,,,\n", bom: true));

        Assert.Empty(result.Errors);
        Assert.Equal("Câu A", Assert.Single(result.Questions).QuestionText);
    }

    [Fact]
    public void Delimiter_cham_phay_van_doc_duoc()
    {
        // Excel ở locale VN xuất CSV bằng dấu chấm phẩy. Không nhận ra thì cả file thành MỘT cột và
        // triệu chứng lộ ra là "thiếu cột question_text" — chẩn đoán sai hẳn nguyên nhân.
        var csv = "question_text;sample_answer;is_required;nhom\nCâu A;Đáp án A;có;Nhóm 1\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        var q = Assert.Single(result.Questions);
        Assert.Equal("Câu A", q.QuestionText);
        Assert.Equal("Đáp án A", q.SampleAnswer);
    }

    [Fact]
    public void Byte_khong_phai_utf8_thi_400_kem_huong_dan_luu_lai()
    {
        // Windows-1258 (bảng mã tiếng Việt cũ của Windows) — Excel "CSV (Comma delimited)" hay xuất ra.
        var bytes = new List<byte>(Encoding.ASCII.GetBytes(Header + "C"));
        bytes.AddRange(new byte[] { 0xE2, 0xF5, 0x20 });   // chuỗi byte không hợp lệ trong UTF-8
        bytes.AddRange(Encoding.ASCII.GetBytes(",,,\n"));

        var ex = Assert.Throws<ArgumentException>(() => QuestionCsvImporter.Parse(bytes.ToArray()));

        // Từ chối kèm hướng dẫn, KHÔNG đoán sang bảng mã khác: đoán sai với tiếng Việt gần như là tung
        // đồng xu, và text sai sinh ra sẽ được lưu vĩnh viễn.
        Assert.Contains("UTF-8", ex.Message);
    }

    [Fact]
    public void Ky_tu_dieu_khien_va_zero_width_bi_loai_bo()
    {
        // Ký tự vô hình hay lọt vào khi HR copy từ web/Word: không nhìn thấy nhưng làm lệch phép so
        // chuỗi (điều kiện "đáp án có đổi không" của R10) và tính vào độ dài.
        var csv = Header + "Câu​ A,Đáp án,,\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        var q = Assert.Single(result.Questions);
        Assert.Equal("Câu A", q.QuestionText);
        Assert.Equal("Đáp án", q.SampleAnswer);
    }

    // ───────────────── is_required ─────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("1")]
    [InlineData("x")]
    [InlineData("có")]
    [InlineData("YES")]
    public void Is_required_nhan_nhieu_cach_viet_true(string raw)
        => Assert.True(Assert.Single(
            QuestionCsvImporter.Parse(Utf8(Header + $"Câu A,,{raw},\n")).Questions).IsRequired);

    [Theory]
    [InlineData("false")]
    [InlineData("0")]
    [InlineData("không")]
    [InlineData("NO")]
    public void Is_required_nhan_nhieu_cach_viet_false(string raw)
        => Assert.False(Assert.Single(
            QuestionCsvImporter.Parse(Utf8(Header + $"Câu A,,{raw},\n")).Questions).IsRequired);

    [Fact]
    public void Is_required_de_trong_thi_mac_dinh_true()
        => Assert.True(Assert.Single(
            QuestionCsvImporter.Parse(Utf8(Header + "Câu A,,,\n")).Questions).IsRequired);

    [Fact]
    public void Is_required_gia_tri_la_thi_bao_loi_dong_do()
    {
        var result = QuestionCsvImporter.Parse(Utf8(Header + "Câu A,,có lẽ,\nCâu B,,,\n"));

        Assert.Single(result.Errors);
        Assert.Equal(2, result.Errors[0].Line);
        // Đoán bừa "có lẽ" thành true/false là âm thầm đổi đề thi của HR.
        Assert.Equal("Câu B", Assert.Single(result.Questions).QuestionText);
    }

    // ───────────────── lỗi từng dòng ─────────────────

    [Fact]
    public void Dong_thieu_question_text_thi_vao_Errors_kem_so_dong_va_bo_qua_dong_do()
    {
        var csv = Header + "Câu A,,,\n,Đáp án mồ côi,,\nCâu C,,,\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        Assert.Equal(3, result.TotalRows);
        Assert.Equal(2, result.Questions.Count);   // dòng hỏng bị bỏ, hai dòng kia vẫn về
        var err = Assert.Single(result.Errors);
        Assert.Equal(3, err.Line);                 // header là dòng 1 → dòng hỏng là dòng 3 của FILE
        Assert.Equal("question_text", err.Column);
    }

    [Fact]
    public void Dap_an_qua_dai_thi_bao_loi_MOT_dong_khong_hong_ca_file()
    {
        var csv = Header + $"Câu A,{new string('x', 5_001)},,\nCâu B,ngắn gọn,,\n";

        var result = QuestionCsvImporter.Parse(Utf8(csv));

        Assert.Single(result.Errors);
        Assert.Equal("sample_answer", result.Errors[0].Column);
        Assert.Equal("Câu B", Assert.Single(result.Questions).QuestionText);
    }

    [Fact]
    public void Cau_hoi_qua_dai_thi_bao_loi_dong_do()
    {
        var result = QuestionCsvImporter.Parse(Utf8(Header + $"{new string('x', 2_001)},,,\n"));

        Assert.Empty(result.Questions);
        Assert.Equal("question_text", Assert.Single(result.Errors).Column);
    }

    [Fact]
    public void Dong_trang_cuoi_file_khong_tinh_va_khong_bao_loi()
    {
        // Excel hay để lại dòng trắng ở cuối. Tính nó là "dòng thiếu câu hỏi" thì HR nào cũng thấy
        // một lỗi đỏ vô nghĩa ngay lần nhập đầu tiên.
        var result = QuestionCsvImporter.Parse(Utf8(Header + "Câu A,,,\n,,,\n\n"));

        Assert.Equal(1, result.TotalRows);
        Assert.Empty(result.Errors);
    }

    // ───────────────── ca chặn cả file ─────────────────

    [Fact]
    public void Thieu_cot_question_text_thi_bao_loi_kem_ten_cot_can_co()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => QuestionCsvImporter.Parse(Utf8("cau_hoi,dap_an\nCâu A,Đáp án\n")));

        Assert.Contains("question_text", ex.Message);
        Assert.Contains("sample_answer", ex.Message);
        Assert.Contains("nhom", ex.Message);
    }

    [Fact]
    public void Chi_co_header_khong_co_du_lieu_thi_bao_loi()
        => Assert.Throws<ArgumentException>(() => QuestionCsvImporter.Parse(Utf8(Header)));

    [Fact]
    public void File_rong_thi_bao_loi()
        => Assert.Throws<ArgumentException>(() => QuestionCsvImporter.Parse(Utf8("")));

    [Fact]
    public void Vuot_200_dong_thi_bao_loi()
    {
        var csv = Header + string.Concat(Enumerable.Range(1, 201).Select(i => $"Câu {i},,,\n"));

        var ex = Assert.Throws<ArgumentException>(() => QuestionCsvImporter.Parse(Utf8(csv)));

        Assert.Contains("200", ex.Message);
    }

    [Fact]
    public void Dung_200_dong_thi_qua()
    {
        var csv = Header + string.Concat(Enumerable.Range(1, 200).Select(i => $"Câu {i},,,\n"));

        Assert.Equal(200, QuestionCsvImporter.Parse(Utf8(csv)).Questions.Count);
    }

    // ───────────────── file mẫu ─────────────────

    [Fact]
    public void Template_co_BOM_utf8()
    {
        var bytes = QuestionCsvImporter.BuildTemplate();

        // Thiếu BOM thì Excel đọc theo bảng mã hệ thống ⇒ chính file DO TA phát ra hiện tiếng Việt lỗi.
        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "File mẫu thiếu BOM UTF-8 — Excel sẽ mở ra tiếng Việt lỗi.");
    }

    [Fact]
    public void Template_nhap_lai_chinh_no_thi_khong_co_loi_nao()
    {
        // Round-trip: một test này bắt được nửa số lỗi định dạng có thể có (tên cột lệch, thiếu bọc
        // nháy, giá trị is_required không tự đọc được...).
        var result = QuestionCsvImporter.Parse(QuestionCsvImporter.BuildTemplate());

        Assert.Empty(result.Errors);
        Assert.Equal(3, result.Questions.Count);
    }

    [Fact]
    public void Template_co_dong_mau_chua_dau_phay_va_xuong_dong()
    {
        // Dòng mẫu tồn tại để HR THẤY TẬN MẮT là dấu phẩy/xuống dòng không làm vỡ file.
        var result = QuestionCsvImporter.Parse(QuestionCsvImporter.BuildTemplate());

        Assert.Contains(result.Questions, q => q.SampleAnswer?.Contains(',') == true);
        Assert.Contains(result.Questions, q => q.SampleAnswer?.Contains('\n') == true);
        Assert.Contains(result.Questions, q => q.QuestionGroup is not null);
    }

    [Fact]
    public void Template_co_du_bon_cot()
    {
        var text = new UTF8Encoding(false).GetString(QuestionCsvImporter.BuildTemplate())
            .TrimStart('﻿');
        var header = text.Split('\n')[0].Trim();

        Assert.Equal("question_text,sample_answer,is_required,nhom", header);
    }
}
