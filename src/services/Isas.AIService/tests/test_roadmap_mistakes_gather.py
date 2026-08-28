# tests/test_roadmap_mistakes_gather.py — MIS1-B2: AI gom LỖI thành CHỦ ĐỀ, và việc gom đó
# KIỂM ĐƯỢC. Bảy chỗ phải đúng đồng thời (5 trong build_roadmap_prompt · response_schema ·
# vòng chuẩn hoá + lọc/retry/drop) — file này khoá tầng response_schema + roadmap_quality.py +
# provider (retry/drop/raise), bổ sung cho tests/test_roadmap.py (prompt content) và
# tests/test_roadmap_mode.py (golden hash).
import json
from unittest.mock import AsyncMock

import pytest

from app.providers.gemini import GeminiProvider
from app.roadmap_quality import filter_milestone_mistakes


def _fake_gemini_response(payload: dict):
    resp = AsyncMock()
    resp.text = json.dumps(payload)
    return resp


# ══════════════════ (1) filter_milestone_mistakes — hàm thuần, roadmap_quality.py ══════════════

def test_filter_mistakes_known_ids_rong_khong_loc_gi():
    milestones = [{"title": "M1", "mistakeIds": ["bịa-hoàn-toàn"], "lessons": []}]
    filtered, empty = filter_milestone_mistakes(milestones, [])
    assert filtered == milestones  # y hệt object gốc, không đụng gì
    assert empty == []


def test_filter_mistakes_loai_id_la_giu_id_that():
    milestones = [{"title": "M1", "mistakeIds": ["m1", "id-bịa"], "lessons": []}]
    filtered, empty = filter_milestone_mistakes(milestones, ["m1", "m2"])
    assert filtered[0]["mistakeIds"] == ["m1"]
    assert empty == []  # còn ít nhất 1 id thật → không phải khiếm khuyết


def test_filter_mistakes_rong_ngay_tu_dau_LA_khiem_khuyet():
    """⚠ NGỮ NGHĨA KHÁC filter_milestone_criteria: focusCriteria rỗng ngay từ đầu KHÔNG bị coi là
    khiếm khuyết, nhưng mistakeIds rỗng ngay từ đầu THÌ CÓ — milestone không rút ra từ lỗi nào là
    vô nghĩa với luật gom chủ đề TỪ LỖI."""
    milestones = [{"title": "M1", "mistakeIds": [], "lessons": []}]
    filtered, empty = filter_milestone_mistakes(milestones, ["m1"])
    assert filtered[0]["mistakeIds"] == []
    assert empty == ["M1"]


def test_filter_mistakes_toan_bo_id_bia_cung_LA_khiem_khuyet():
    milestones = [{"title": "M1", "mistakeIds": ["bịa1", "bịa2"], "lessons": []}]
    filtered, empty = filter_milestone_mistakes(milestones, ["m1"])
    assert filtered[0]["mistakeIds"] == []
    assert empty == ["M1"]


def test_filter_mistakes_loc_ca_lesson_level():
    """Chỉ thị GOM CHỦ ĐỀ TỪ LỖI đòi lessons[].mistakeIds là tập con — id lạ lọt ở tầng lesson thì
    lời hứa "id không bịa được" chỉ đúng một nửa."""
    milestones = [{
        "title": "M1", "mistakeIds": ["m1"],
        "lessons": [{"title": "L1", "mistakeIds": ["m1", "id-bịa"]}],
    }]
    filtered, empty = filter_milestone_mistakes(milestones, ["m1", "m2"])
    assert filtered[0]["lessons"][0]["mistakeIds"] == ["m1"]
    assert empty == []


def test_filter_mistakes_lesson_rong_sau_loc_KHONG_phai_khiem_khuyet():
    """mistakeIds ở lesson là BỔ SUNG tuỳ chọn (chỉ milestone mới bắt buộc) — lesson không bám
    riêng lỗi nào vẫn hợp lệ, không kích hoạt retry."""
    milestones = [{
        "title": "M1", "mistakeIds": ["m1"],
        "lessons": [{"title": "L1", "mistakeIds": []}],
    }]
    filtered, empty = filter_milestone_mistakes(milestones, ["m1"])
    assert filtered[0]["lessons"][0]["mistakeIds"] == []
    assert empty == []  # milestone vẫn có m1 → không khiếm khuyết


def test_filter_mistakes_khop_CHINH_XAC_khong_casefold():
    """Id do .NET MINT, không phải chữ tự do model gõ — khớp hoa/thường lệch KHÔNG được coi là
    hợp lệ (khác `filter_milestone_criteria`, cố ý)."""
    milestones = [{"title": "M1", "mistakeIds": ["M1"], "lessons": []}]  # "M1" ≠ "m1"
    filtered, empty = filter_milestone_mistakes(milestones, ["m1"])
    assert filtered[0]["mistakeIds"] == []
    assert empty == ["M1"]


# ══════════════════ (2) response_schema — mistakeIds CÓ ĐIỀU KIỆN, cả hai cấp ══════════════════

@pytest.mark.asyncio
async def test_response_schema_khong_co_mistakes_thi_khong_khai_mistakeIds(monkeypatch):
    captured: dict = {}

    async def fake_generate(self, operation, *, contents, config, model=None,
                            defer_report=False):
        captured["config"] = config
        return _fake_gemini_response(
            {"milestones": [{"title": "M1", "focusCriteria": [], "lessons": [{"title": "L1"}]}]})

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    provider = GeminiProvider()

    await provider.generate_roadmap("BE", "Junior", None, None)

    m_props = captured["config"].response_schema["properties"]["milestones"]["items"]["properties"]
    assert "mistakeIds" not in m_props
    l_props = m_props["lessons"]["items"]["properties"]
    assert "mistakeIds" not in l_props


@pytest.mark.asyncio
async def test_response_schema_co_mistakes_thi_khai_mistakeIds_ca_hai_cap(monkeypatch):
    captured: dict = {}

    async def fake_generate(self, operation, *, contents, config, model=None,
                            defer_report=False):
        captured["config"] = config
        return _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1", "mistakeIds": ["m1"]}]}]})

    monkeypatch.setattr(GeminiProvider, "_generate", fake_generate)
    provider = GeminiProvider()

    await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    m_props = captured["config"].response_schema["properties"]["milestones"]["items"]["properties"]
    assert m_props["mistakeIds"] == {"type": "array", "items": {"type": "string"}}
    l_props = m_props["lessons"]["items"]["properties"]
    assert l_props["mistakeIds"] == {"type": "array", "items": {"type": "string"}}
    # KHÔNG thêm vào required (milestone có thể chưa gom được lỗi nào khi model trả lời).
    required = captured["config"].response_schema["properties"]["milestones"]["items"]["required"]
    assert "mistakeIds" not in required
    lesson_required = m_props["lessons"]["items"]["required"]
    assert "mistakeIds" not in lesson_required


# ══════════════════ (3) provider — mistakeIds đi ĐƯỢC qua vòng chuẩn hoá tới response ══════════

@pytest.mark.asyncio
async def test_provider_mistakeIds_hop_le_di_qua_chuan_hoa_toi_response():
    """🔑 Test này chính là thứ bắt lỗi THIẾU mục 3 (response_schema) hoặc mục 4 (vòng chuẩn hoá):
    thiếu MỘT trong hai thì mistakeIds không bao giờ ra tới đây."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1", "m2"],
             "lessons": [{"title": "L1", "mistakeIds": ["m1"]}]}]})
    )

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai 1"},
                  {"id": "m2", "criterionName": "SQL", "reasoning": "sai 2"}])

    assert milestones[0]["mistakeIds"] == ["m1", "m2"]
    assert milestones[0]["lessons"][0]["mistakeIds"] == ["m1"]


@pytest.mark.asyncio
async def test_provider_model_tra_id_la_bi_loai_khoi_mistakeIds():
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1", "id-model-bịa"],
             "lessons": [{"title": "L1"}]}]})
    )

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    assert milestones[0]["mistakeIds"] == ["m1"]
    assert provider._client.aio.models.generate_content.await_count == 1  # còn id thật, không retry


# ══════════════════ (4) retry — CHUNG thang với criteria, mang theo mistakes ═══════════════════

@pytest.mark.asyncio
async def test_provider_milestone_rong_sau_loc_retry_dung_1_luot_va_mang_theo_mistakes():
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["id-bịa"],
             "lessons": [{"title": "L1"}]}]}),
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]}]}),
    ])

    given_mistakes = [{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}]
    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None, mistakes=given_mistakes)

    assert milestones[0]["mistakeIds"] == ["m1"]
    assert provider._client.aio.models.generate_content.await_count == 2

    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert "BỊ TRẢ LẠI" in prompt_lan_2
    assert "M1" in prompt_lan_2
    # Lượt viết lại PHẢI mang theo mistakes= — mất nó thì mistake_block biến mất khỏi prompt lượt
    # 2 và model không còn gì để trỏ ngược.
    assert "---LỖI CỦA ỨNG VIÊN" in prompt_lan_2


@pytest.mark.asyncio
async def test_provider_khong_truyen_criteria_nhung_co_mistakes_van_retry_duoc():
    """⚠ Bẫy đã biết: nếu retry vẫn còn nằm TRONG `if known_names:` thì caller không gửi criteria
    sẽ KHÔNG có đường retry nào cho mistakes. Test này bắt đúng lỗi đó."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": ["tên tự do, không có rubric"],
             "mistakeIds": ["id-bịa"], "lessons": [{"title": "L1"}]}]}),
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": ["tên tự do, không có rubric"],
             "mistakeIds": ["m1"], "lessons": [{"title": "L1"}]}]}),
    ])

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None, criteria=None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    assert milestones[0]["mistakeIds"] == ["m1"]
    assert provider._client.aio.models.generate_content.await_count == 2
    # criteria=None ⇒ focusCriteria KHÔNG bị lọc (hành vi cũ, độc lập với nhánh mistakes).
    assert milestones[0]["focusCriteria"] == ["tên tự do, không có rubric"]


@pytest.mark.asyncio
async def test_provider_ca_criteria_lan_mistakes_cung_rong_chi_1_luot_retry_chung():
    """CẤM: KHÔNG dựng thang retry thứ hai — cả hai loại khiếm khuyết cùng lúc vẫn chỉ 2 lời gọi
    Gemini (1 lượt gốc + 1 lượt viết lại), không phải 3 hay 4."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": ["id-bịa-criteria"], "mistakeIds": ["id-bịa-mistake"],
             "lessons": [{"title": "L1"}]}]}),
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": ["Tư duy giải quyết vấn đề"], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]}]}),
    ])

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        criteria=[{"criterionId": "id-1", "name": "Tư duy giải quyết vấn đề"}],
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    assert provider._client.aio.models.generate_content.await_count == 2
    assert milestones[0]["focusCriteria"] == ["Tư duy giải quyết vấn đề"]
    assert milestones[0]["mistakeIds"] == ["m1"]

    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    # Feedback gộp CẢ HAI loại khiếm khuyết trong CÙNG một lượt viết lại (không phải hai lượt
    # riêng) — cả hai câu nhận xét đều phải có mặt.
    assert "BỊ TRẢ LẠI" in prompt_lan_2
    assert "không còn tiêu chí hợp lệ nào sau khi lọc" in prompt_lan_2  # milestone_no_criteria
    assert "không gom được lỗi nào sau khi lọc" in prompt_lan_2         # milestone_no_mistakes
    assert "M1" in prompt_lan_2


# ══════════════════ (5) hết lượt — criteria GIỮ, mistakes DROP ═════════════════════════════════

@pytest.mark.asyncio
async def test_provider_het_luot_mistakes_van_rong_thi_DROP_milestone_giu_cai_khac():
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1 (không gom được lỗi)", "focusCriteria": [], "mistakeIds": ["id-bịa"],
             "lessons": [{"title": "L1"}]},
            {"title": "M2 (gom đúng)", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L2"}]},
        ]})
    )

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    # Cả hai lượt (gốc + retry) đều trả CÙNG payload ⇒ M1 vẫn rỗng sau lượt 2 ⇒ bị DROP hẳn,
    # M2 (không liên quan tới khiếm khuyết) giữ nguyên.
    assert len(milestones) == 1
    assert milestones[0]["title"] == "M2 (gom đúng)"
    assert provider._client.aio.models.generate_content.await_count == 2


@pytest.mark.asyncio
async def test_provider_hai_milestone_trung_title_drop_dung_cai_rong_khong_giet_nham():
    """⚠ CẤM đã cảnh báo: drop PHẢI theo THAM CHIẾU đối tượng, không theo title — nếu lỡ so khớp
    lại theo title (thay vì dùng CHÍNH `mistakeIds` đã lọc trên milestone) thì 2 milestone trùng
    title "M" sẽ bị giết NHẦM cả hai (hoặc đúng cái không đáng)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M", "focusCriteria": [], "mistakeIds": ["id-bịa"],
             "lessons": [{"title": "L1 (rỗng, phải bị drop)"}]},
            {"title": "M", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L2 (hợp lệ, phải sống)"}]},
        ]})
    )

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    assert len(milestones) == 1
    assert milestones[0]["mistakeIds"] == ["m1"]
    assert milestones[0]["lessons"][0]["title"] == "L2 (hợp lệ, phải sống)"


@pytest.mark.asyncio
async def test_provider_het_luot_criteria_van_rong_thi_GIU_milestone_focusCriteria_rong():
    """Đối chứng: `criteria` KHÔNG drop milestone (khác `mistakes`) — focusCriteria rỗng vẫn có
    nghĩa, mistakeIds rỗng thì không."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": ["tên hoàn toàn bịa"], "lessons": [{"title": "L1"}]}]})
    )

    milestones = await provider.generate_roadmap(
        "BA", "Junior", None, None,
        criteria=[{"criterionId": "id-1", "name": "Phân tích yêu cầu"}])

    assert len(milestones) == 1               # milestone vẫn được giữ
    assert milestones[0]["focusCriteria"] == []  # nhưng focusCriteria bịa đã bị lọc sạch


@pytest.mark.asyncio
async def test_provider_drop_sach_raise_voi_prefix_co_dinh():
    """⚠ Prefix CỐ ĐỊNH để .NET/test phân biệt được với lỗi AI thường."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["id-bịa"],
             "lessons": [{"title": "L1"}]}]})
    )

    with pytest.raises(ValueError, match=r"^ROADMAP_ALL_MILESTONES_DROPPED"):
        await provider.generate_roadmap(
            "BE", "Junior", None, None,
            mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai"}])

    assert provider._client.aio.models.generate_content.await_count == 2  # đúng 1 lượt retry


# ══════════════════ (6) không truyền mistakes ⇒ hành vi cũ, không raise ════════════════════════

@pytest.mark.asyncio
async def test_provider_khong_truyen_mistakes_thi_khong_loc_khong_raise():
    """Model trả `mistakeIds` rỗng/không có ở mọi milestone — vô hại khi `mistakes` không được
    truyền (known_ids rỗng ⇒ filter_milestone_mistakes bỏ qua hoàn toàn)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "lessons": [{"title": "L1"}]}]})
    )

    milestones = await provider.generate_roadmap("BE", "Junior", None, None)

    assert len(milestones) == 1
    assert milestones[0]["mistakeIds"] == []
    assert provider._client.aio.models.generate_content.await_count == 1  # không retry, không raise


# ══ (7) REC1-B5 — MỌI mistakeId đã cấp PHẢI được gán (thiếu → retry, trùng → chỉ log) ═══════════
# Khác mục (4)/(5): ở đó milestone GOM SAI (toàn id bịa) rồi rỗng SAU LỌC. Ở đây id là THẬT (nằm
# trong known_ids) nhưng chưa từng xuất hiện ở BẤT KỲ milestone nào — luật 4 GOM CHỦ ĐỀ TỪ LỖI
# ("mỗi lỗi CHỈ nên thuộc một milestone") nay siết bắt buộc thành "MỌI lỗi PHẢI được gán".

@pytest.mark.asyncio
async def test_provider_thieu_mistake_id_duoc_gan_thi_retry_dung_1_luot_lan_sau_du():
    """XONG-KHI test 1: bài giảng thiếu phần về một lỗi ⇒ có lượt viết lại, và lượt sau đạt.

    Lượt 1: model chỉ gom m1 vào M1, BỎ SÓT m2 hoàn toàn (m2 không xuất hiện ở milestone nào).
    Lượt 2: model gom đủ cả hai. Dùng CHUNG thang retry đã có (không dựng thang riêng cho ca
    này) — mutation-check: đếm await_count."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(side_effect=[
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]}]}),
        _fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1", "m2"],
             "lessons": [{"title": "L1"}]}]}),
    ])

    milestones = await provider.generate_roadmap(
        "BE", "Junior", None, None,
        mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai 1"},
                  {"id": "m2", "criterionName": "SQL", "reasoning": "sai 2"}])

    assert milestones[0]["mistakeIds"] == ["m1", "m2"]
    assert provider._client.aio.models.generate_content.await_count == 2  # ĐÚNG 1 lượt viết lại

    prompt_lan_2 = provider._client.aio.models.generate_content.await_args_list[1].kwargs["contents"]
    assert "BỊ TRẢ LẠI" in prompt_lan_2
    assert "CHƯA được gán vào milestone nào" in prompt_lan_2
    assert "m2" in prompt_lan_2


@pytest.mark.asyncio
async def test_provider_het_luot_van_thieu_mistake_id_thi_log_roi_di_tiep_khong_raise(caplog):
    """XONG-KHI test 2: hết lượt vẫn thiếu ⇒ log rồi ĐI TIẾP, KHÔNG raise (mẫu :2148 — criteria/
    mistakes rỗng-sau-lọc; ở đây là id THIẾU, không phải milestone hỏng, nên KHÔNG drop gì).

    Mutation-check: dựng thang retry thứ hai riêng cho ca này ⇒ await_count lệch khỏi 2 (sẽ thành
    3/4 nếu ai đó thêm một vòng gọi lại nữa cho riêng missing_mistake_ids)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]}]})  # MỌI lượt đều bỏ sót m2
    )

    with caplog.at_level("ERROR", logger="app.providers.gemini"):
        milestones = await provider.generate_roadmap(
            "BE", "Junior", None, None,
            mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai 1"},
                      {"id": "m2", "criterionName": "SQL", "reasoning": "sai 2"}])

    assert len(milestones) == 1               # KHÔNG raise, KHÔNG drop milestone đã có
    assert milestones[0]["mistakeIds"] == ["m1"]
    assert provider._client.aio.models.generate_content.await_count == 2  # đúng attempts=2, không hơn
    assert any("m2" in r.message and "KHÔNG được gán" in r.message for r in caplog.records)


@pytest.mark.asyncio
async def test_provider_id_trung_o_hai_milestone_chi_warning_khong_retry(caplog):
    """TRÙNG id (cùng id ở ≥2 milestone) — dạy hai lần thì thừa, không sai: CHỈ log.warning, KHÔNG
    tiêu lượt retry. Đối chứng với ca THIẾU ngay trên (ĐÓ mới retry, CA NÀY thì không)."""
    provider = GeminiProvider()
    provider._client.aio.models.generate_content = AsyncMock(
        return_value=_fake_gemini_response({"milestones": [
            {"title": "M1", "focusCriteria": [], "mistakeIds": ["m1"],
             "lessons": [{"title": "L1"}]},
            {"title": "M2", "focusCriteria": [], "mistakeIds": ["m1"],  # TRÙNG — m1 ở cả 2 milestone
             "lessons": [{"title": "L2"}]},
        ]})
    )

    with caplog.at_level("WARNING", logger="app.providers.gemini"):
        milestones = await provider.generate_roadmap(
            "BE", "Junior", None, None,
            mistakes=[{"id": "m1", "criterionName": "SQL", "reasoning": "sai 1"}])

    assert len(milestones) == 2
    assert provider._client.aio.models.generate_content.await_count == 1  # KHÔNG retry vì trùng
    assert any("m1" in r.message and "nhiều hơn một milestone" in r.message
               for r in caplog.records)
