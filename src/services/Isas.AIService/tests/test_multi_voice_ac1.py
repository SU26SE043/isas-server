# tests/test_multi_voice_ac1.py — AC1/B5: detector ≥2 giọng nói (cờ `multi_voice`).
#
# Cách dựng ca: thay `SpeakerEmbedder.embed` bằng một hàm trả vector THEO KỊCH BẢN, còn mọi thứ
# khác (cắt cửa sổ · gom cụm average-linkage · quy thời lượng · hai ngưỡng) chạy THẬT. Làm vậy vì
# phần đáng vỡ nằm ở LUẬT QUYẾT ĐỊNH chứ không ở ONNX; và chạy model thật trong unit test sẽ buộc
# CI phải có file 28 MB.
#
# ⚠ Bộ này KHÔNG chứng minh ngưỡng đúng với giọng người thật — đó là việc của bảng hiệu chuẩn
# (7 ghi âm THẬT + 18 ca ghép + 4 ca dương tổng hợp) trong báo cáo AC1/B5.
import re

import numpy as np
import pytest

from app import multi_voice
from app.config import settings
from app.multi_voice import (
    MultiVoiceContext, build_note, detect_from_pcm, maybe_report_multi_voice,
    plan_windows, resolve_context,
)

SR = multi_voice.SAMPLE_RATE


@pytest.fixture(autouse=True)
def _bat_co(monkeypatch):
    """Mặc định cho cả file: cờ BẬT + có đích callback. Ca kill-switch tự tắt lại."""
    monkeypatch.setattr(settings, "multi_voice_enabled", True)
    monkeypatch.setattr(settings, "campaign_callback_base", "http://campaign:8080")


def job(**kw):
    """Job chấm B2B hợp lệ (camelCase như `_field` đọc mặc định)."""
    base = {
        "answerId": "11111111-1111-1111-1111-111111111111",
        "sessionId": "22222222-2222-2222-2222-222222222222",
        "campaignId": "33333333-3333-3333-3333-333333333333",
        "candidateId": "44444444-4444-4444-4444-444444444444",
        "attemptNo": 1,
    }
    base.update(kw)
    return base


def pcm(sec):
    """PCM đủ dài; nội dung không quan trọng vì `embed` đã bị thay."""
    return np.zeros(int(sec * SR), dtype=np.float32)


def spans(sec):
    return [(0.0, sec)]


def fake_embed(pattern, dim=32):
    """Sinh `embed` trả vector theo `pattern`: cùng ký tự = cùng "người".

    Hai người là hai trục TRỰC GIAO ⇒ khoảng cách cosine giữa tâm hai cụm = 1,0 (vượt mọi ngưỡng
    hợp lý). Ca cần "khác nhau ít" thì dùng `fake_embed_angle`.
    """
    def _embed(batch):
        n = batch.shape[0]
        out = np.zeros((n, dim))
        for i in range(n):
            axis = 0 if pattern[i % len(pattern)] == "A" else 1
            out[i, axis] = 1.0
        return out
    return _embed


def fake_embed_angle(pattern, separation, dim=32):
    """Như trên nhưng ĐẶT TRƯỚC khoảng cách cosine giữa hai tâm = `separation`."""
    cos = 1.0 - separation
    a = np.zeros(dim); a[0] = 1.0
    b = np.zeros(dim); b[0] = cos; b[1] = float(np.sqrt(max(0.0, 1 - cos ** 2)))

    def _embed(batch):
        return np.stack([a if pattern[i % len(pattern)] == "A" else b
                         for i in range(batch.shape[0])])
    return _embed


# ── Luật quyết định ───────────────────────────────────────────────────────────────────────

def test_mot_giong_khong_gan_co(monkeypatch):
    """Toàn bộ cửa sổ cùng một người ⇒ KHÔNG cờ.

    ⚠ Ghi lại một hành vi phản trực giác: vì luôn CẮT THÀNH 2 CỤM, audio một giọng vẫn cho ra
    `second_speaker_sec > 0` — cắt k=2 trên thứ không có ranh giới thì buộc phải sinh ra một cụm
    thứ hai nào đó. Thứ chặn cờ ở đây là `separation = 0`. Nói cách khác `second_speaker_sec` MỘT
    MÌNH không bao giờ là bằng chứng có người thứ hai; đó là lý do hai ngưỡng phải ĐỒNG THỜI đạt.
    """
    monkeypatch.setattr(multi_voice._embedder, "embed", fake_embed("A"))
    r = detect_from_pcm(pcm(20), spans(20))
    assert r.detected is False
    assert r.separation == pytest.approx(0.0, abs=1e-9)


def test_hai_giong_du_3s_gan_co(monkeypatch):
    """Người thứ hai nói đủ dài + hai giọng tách bạch ⇒ CÓ cờ."""
    # 20s tiếng nói ⇒ 25 cửa sổ (1,5s/bước 0,75s). Cứ 4 cửa sổ thì 2 thuộc người B ⇒ B chiếm
    # khoảng nửa buổi, thừa ngưỡng 3s.
    monkeypatch.setattr(multi_voice._embedder, "embed", fake_embed("AABB"))
    r = detect_from_pcm(pcm(20), spans(20))
    assert r.detected is True
    assert r.second_speaker_sec >= settings.multi_voice_min_second_sec
    assert r.separation >= settings.multi_voice_separation_threshold


def test_giong_thu_hai_duoi_3s_khong_gan_co(monkeypatch):
    """Giọng thứ hai CHỈ 2 cửa sổ (= 1,5s < 3s) ⇒ KHÔNG cờ, dù hai giọng khác hẳn nhau.

    🔴 Đây là lá chắn chống biến "một tiếng ho / một từ lạ / một cửa sổ ngoại lai" thành một cáo
    buộc gian lận — và trên bảng hiệu chuẩn nó chặn 18/25 ca âm, tức nó gánh phần lớn công việc
    chứ không phải ngưỡng separation.
    """
    pattern = ["A"] * 24
    pattern[10] = pattern[11] = "B"
    monkeypatch.setattr(multi_voice._embedder, "embed", fake_embed(pattern))
    r = detect_from_pcm(pcm(20), spans(20))
    assert r.second_speaker_sec == pytest.approx(2 * settings.multi_voice_hop_sec)
    assert r.second_speaker_sec < settings.multi_voice_min_second_sec
    assert r.separation > settings.multi_voice_separation_threshold, (
        "ca này phải TRƯỢT vì THỜI LƯỢNG, không phải vì separation — nếu separation cũng thấp thì "
        "test không còn đo được cổng 3s nữa")
    assert r.detected is False


def test_hai_giong_qua_giong_nhau_khong_gan_co(monkeypatch):
    """Đủ thời lượng nhưng hai cụm quá GIỐNG nhau ⇒ KHÔNG cờ.

    Đây là ca "cùng một người đổi ngữ điệu / khoảng cách mic" — nguồn dương-tính-giả lớn nhất
    theo bảng hiệu chuẩn (ghép hai bản ghi thật của cùng người đạt tới 0,407).
    """
    monkeypatch.setattr(multi_voice._embedder, "embed",
                        fake_embed_angle("AABB", separation=0.30))
    r = detect_from_pcm(pcm(20), spans(20))
    assert r.second_speaker_sec >= settings.multi_voice_min_second_sec
    assert r.separation == pytest.approx(0.30, abs=1e-6)
    assert r.detected is False


def test_ngay_tren_va_ngay_duoi_nguong_separation(monkeypatch):
    """Ngưỡng separation phải cắt ĐÚNG chỗ nó khai (biên trên/dưới)."""
    th = settings.multi_voice_separation_threshold
    for sep, expect in ((th + 0.02, True), (th - 0.02, False)):
        monkeypatch.setattr(multi_voice._embedder, "embed",
                            fake_embed_angle("AABB", separation=sep))
        assert detect_from_pcm(pcm(20), spans(20)).detected is expect


def test_khong_du_hai_cua_so_thi_khong_ket_luan(monkeypatch):
    """Audio quá ngắn (< 2 cửa sổ) ⇒ không có gì để phân cụm ⇒ KHÔNG cờ, không ném."""
    monkeypatch.setattr(multi_voice._embedder, "embed", fake_embed("AB"))
    r = detect_from_pcm(pcm(1.8), spans(1.8))
    assert r.detected is False and r.num_windows < 2


def test_bo_qua_khoang_lang_khi_cat_cua_so(monkeypatch):
    """Cửa sổ cắt trên trục TIẾNG NÓI đã nối, không trên audio gốc.

    Cắt trên audio gốc thì cửa sổ rơi trọn vào khoảng lặng, mà vector nhúng của sự im lặng là
    nhiễu thuần — nó tự gom thành một "cụm" trông y hệt người thứ hai.
    """
    monkeypatch.setattr(multi_voice._embedder, "embed", fake_embed("A"))
    # 60s audio nhưng chỉ 6s có tiếng nói, nằm rải rác.
    sp = [(0.0, 2.0), (25.0, 27.0), (55.0, 57.0)]
    r = detect_from_pcm(pcm(60), sp)
    # 6s tiếng nói ⇒ 7 cửa sổ, KHÔNG phải ~78 cửa sổ của 60s audio.
    assert r.num_windows == 7


# ── Trần chi phí ──────────────────────────────────────────────────────────────────────────

def test_tran_cua_so_duoc_ap_voi_audio_dai():
    """Audio rất dài vẫn bị kẹp về trần cửa sổ (chặn CPU)."""
    starts = plan_windows(600.0, 1.5, 0.75, settings.multi_voice_max_windows)
    assert len(starts) == settings.multi_voice_max_windows


def test_vuot_tran_thi_lay_mau_DEU_khong_cat_duoi():
    """🔴 Vượt trần phải lấy mẫu ĐỀU trên TOÀN BỘ, không cắt đuôi.

    Cắt đuôi biến "trần chi phí" thành "chỉ nghe phần đầu câu trả lời" ⇒ người thứ hai nói ở nửa
    sau KHÔNG BAO GIỜ bị phát hiện, mà triệu chứng lại là một con số hoàn toàn hợp lý.
    """
    total = 300.0
    starts = plan_windows(total, 1.5, 0.75, 40)
    assert len(starts) == 40
    # Cửa sổ cuối phải chạm gần cuối bản ghi, không dừng ở ~40*0.75 = 30s.
    assert starts[-1] > total - 5.0
    assert starts[0] == 0.0
    # Khoảng cách giữa các mốc gần đều nhau.
    gaps = np.diff(starts)
    assert gaps.max() - gaps.min() <= 0.75 + 1e-9


def test_duoi_tran_thi_giu_nguyen_do_phan_giai():
    starts = plan_windows(20.0, 1.5, 0.75, 80)
    # 20s tiếng nói ⇒ (20 - 1,5) // 0,75 + 1 = 25 cửa sổ.
    assert len(starts) == 25 and starts[1] == pytest.approx(0.75)


# ── Ba cổng chạy ──────────────────────────────────────────────────────────────────────────

def test_kill_switch_tat_thi_khong_lam_gi(monkeypatch):
    monkeypatch.setattr(settings, "multi_voice_enabled", False)
    assert resolve_context(job()) is None


@pytest.mark.asyncio
async def test_kill_switch_tat_thi_KHONG_nap_model_va_KHONG_goi_callback(monkeypatch):
    """Cờ tắt ⇒ không chạm model, không tải audio, không gửi cờ.

    Nạp model là 28 MB + vài trăm ms; gửi cờ là một cáo buộc gian lận. Cả hai TUYỆT ĐỐI không
    được xảy ra khi tính năng đang tắt.
    """
    monkeypatch.setattr(settings, "multi_voice_enabled", False)
    goi = []
    monkeypatch.setattr(multi_voice, "post_flag",
                        lambda *a, **k: goi.append("flag"))
    monkeypatch.setattr(type(multi_voice._embedder), "session",
                        property(lambda self: pytest.fail("KHÔNG được nạp model khi cờ tắt")))

    async def ensure_audio():
        pytest.fail("KHÔNG được tải audio khi cờ tắt")

    assert await maybe_report_multi_voice(job(), ensure_audio) is False
    assert goi == []


def test_bo_qua_job_B2C_thieu_campaign_id():
    """Van BC-6: B2C là luyện tập, KHÔNG có giám sát chống gian lận."""
    assert resolve_context(job(campaignId=None)) is None
    j = job(); del j["campaignId"]
    assert resolve_context(j) is None


def test_bo_qua_khi_thieu_candidate_id():
    """`CandidateId` đi CẶP với `CampaignId` — thiếu nó thì cờ không về được đúng ứng viên."""
    assert resolve_context(job(candidateId=None)) is None


@pytest.mark.parametrize("attempt", [2, 3, "2"])
def test_bo_qua_attempt_khac_1(attempt):
    """E10 chấm CÙNG answer N lần ⇒ chạy mọi attempt là nhân bản cả chi phí lẫn cờ."""
    assert resolve_context(job(attemptNo=attempt)) is None


@pytest.mark.parametrize("attempt", [1, "1", None])
def test_chay_o_attempt_1_va_job_cu_khong_khai_attempt(attempt):
    """Job cũ không mang `attemptNo` ⇒ mặc định 1 (giống worker.py), vẫn chạy."""
    assert resolve_context(job(attemptNo=attempt)) is not None


def test_bo_qua_khi_thieu_answer_id_hoac_session_id():
    assert resolve_context(job(answerId=None)) is None
    assert resolve_context(job(sessionId=None)) is None


# ── Hợp đồng dây: PascalCase (hàng đợi) lẫn camelCase ─────────────────────────────────────

def test_doc_duoc_ca_PascalCase_lan_camelCase():
    """🔴 `ScoringJobPublisher.cs` serialize KHÔNG kèm options ⇒ khoá trên hàng đợi là
    PascalCase. Chỉ đọc camelCase thì `CampaignId` luôn None ⇒ MỌI job bị coi là B2C ⇒ detector
    không bao giờ chạy, mà không có lỗi nào nổ ở đâu cả (đúng lớp bug `focusCriteria` bị pydantic
    nuốt · `adaptiveMaxQuestions` vs `maxQuestions`)."""
    pascal = {
        "AnswerId": "11111111-1111-1111-1111-111111111111",
        "SessionId": "22222222-2222-2222-2222-222222222222",
        "CampaignId": "33333333-3333-3333-3333-333333333333",
        "CandidateId": "44444444-4444-4444-4444-444444444444",
        "AttemptNo": 1,
    }
    ctx = resolve_context(pascal)
    assert ctx is not None
    assert ctx.campaign_id == "33333333-3333-3333-3333-333333333333"
    assert ctx.candidate_id == "44444444-4444-4444-4444-444444444444"
    assert ctx.answer_id == "11111111-1111-1111-1111-111111111111"
    assert ctx.session_id == "22222222-2222-2222-2222-222222222222"

    camel = resolve_context(job())
    assert camel is not None and camel.campaign_id == ctx.campaign_id


def test_pascal_case_cung_ton_trong_cong_attempt():
    """Cổng attempt phải đọc được `AttemptNo` PascalCase — nếu không, mọi attempt 2..N của
    đường hàng đợi đều lọt qua và cờ bị nhân bản."""
    assert resolve_context({
        "AnswerId": "a", "SessionId": "s", "CampaignId": "c", "CandidateId": "d",
        "AttemptNo": 2,
    }) is None


# ── Ghi chú tất định (khoá dedup của B4) ──────────────────────────────────────────────────

def test_note_tat_dinh_va_lam_tron_giay():
    """Note là KHOÁ DEDUP phía CampaignService `(SessionId, SignalType, Note)`.

    Đường chấm CỐ Ý chạy lại (self-consistency + StuckAnswerRepublisher) nên cùng một sự kiện âm
    thanh tới đó nhiều lần; note đổi theo từng lượt là dedup mất tác dụng hoàn toàn và HR thấy
    `multi_voice: 3` cho MỘT lần nghi vấn."""
    a = build_note("abc", 4.4)
    b = build_note("abc", 4.4)
    assert a == b == "answer abc: ~4s giọng thứ hai"
    # Số lẻ float khác nhau chút vẫn phải ra CÙNG chuỗi (đây chính là tác dụng của làm tròn).
    assert build_note("abc", 4.4001) == a
    # ...nhưng chênh tới nửa giây thì PHẢI ra chuỗi khác, nếu không việc làm tròn đã nuốt
    # mất chính thông tin nó cần chuyển tải.
    assert build_note("abc", 3.4) != a


# ── An toàn: KHÔNG được kéo lượt chấm chết theo ───────────────────────────────────────────

@pytest.mark.asyncio
async def test_detector_nem_loi_thi_nuot_khong_lan_ra_ngoai(monkeypatch):
    """Lượt chấm là ĐƯỜNG TIỀN (PAY-13) — detector thử nghiệm hỏng không được làm hỏng nó."""
    async def ensure_audio():
        return "/tmp/khong-ton-tai.webm"

    def no(_):
        raise RuntimeError("model hỏng")
    monkeypatch.setattr(multi_voice, "_detect_file", no)
    assert await maybe_report_multi_voice(job(), ensure_audio) is False


@pytest.mark.asyncio
async def test_tai_audio_hong_thi_nuot(monkeypatch):
    async def ensure_audio():
        raise OSError("S3 sập")
    assert await maybe_report_multi_voice(job(), ensure_audio) is False


@pytest.mark.asyncio
async def test_callback_hong_thi_nuot(monkeypatch):
    """Gửi cờ hỏng cũng KHÔNG được ném — cờ là phụ phẩm, chấm mới là việc chính."""
    monkeypatch.setattr(multi_voice, "_detect_file",
                        lambda p: multi_voice.MultiVoiceResult(True, 5.0, 0.9, 10))

    async def post(ctx, note):
        raise RuntimeError("CampaignService 500")
    monkeypatch.setattr(multi_voice, "post_flag", post)

    async def ensure_audio():
        return "/tmp/x.webm"
    assert await maybe_report_multi_voice(job(), ensure_audio) is False


@pytest.mark.asyncio
async def test_thieu_dich_callback_thi_bo_qua_va_KHONG_tai_audio(monkeypatch):
    """`CAMPAIGN_CALLBACK_BASE` rỗng ⇒ bỏ qua kèm cảnh báo, và không tốn một lượt tải audio nào.

    Cấu hình thiếu mà im lặng đúng là cách `USAGE_SINK_BASE`/`PROMPT_REGISTRY_BASE` từng làm F22
    và F21 tắt câm nhiều ngày."""
    monkeypatch.setattr(settings, "campaign_callback_base", "")

    async def ensure_audio():
        pytest.fail("không được tải audio khi chưa có đích gửi")
    assert await maybe_report_multi_voice(job(), ensure_audio) is False


@pytest.mark.asyncio
async def test_khong_phat_hien_thi_khong_gui_co(monkeypatch):
    monkeypatch.setattr(multi_voice, "_detect_file",
                        lambda p: multi_voice.MultiVoiceResult(False, 1.0, 0.1, 10))
    goi = []
    monkeypatch.setattr(multi_voice, "post_flag", lambda *a: goi.append(a))

    async def ensure_audio():
        return "/tmp/x.webm"
    assert await maybe_report_multi_voice(job(), ensure_audio) is False
    assert goi == []


@pytest.mark.asyncio
async def test_phat_hien_thi_gui_dung_payload(monkeypatch):
    """Cờ phải về ĐÚNG buổi thi, với `signalType` khớp whitelist AiSignals của CampaignService."""
    monkeypatch.setattr(multi_voice, "_detect_file",
                        lambda p: multi_voice.MultiVoiceResult(True, 4.4, 0.9, 12))
    gui = {}

    async def post(ctx: MultiVoiceContext, note: str):
        gui["ctx"] = ctx
        gui["note"] = note
    monkeypatch.setattr(multi_voice, "post_flag", post)

    async def ensure_audio():
        return "/tmp/x.webm"
    assert await maybe_report_multi_voice(job(), ensure_audio) is True
    assert gui["ctx"].campaign_id == "33333333-3333-3333-3333-333333333333"
    assert gui["ctx"].session_id == "22222222-2222-2222-2222-222222222222"
    assert gui["ctx"].candidate_id == "44444444-4444-4444-4444-444444444444"
    assert gui["note"] == "answer 11111111-1111-1111-1111-111111111111: ~4s giọng thứ hai"
    assert multi_voice.SIGNAL_TYPE == "multi_voice"


def test_signal_type_khop_whitelist_campaignservice():
    """`multi_voice` phải khớp CHÍNH XÁC chuỗi trong `SessionFlagController.AiSignals`.

    Lệch một ký tự ⇒ CampaignService trả 400 `Unknown signal_type` cho MỌI cờ, mà detector thì
    nuốt lỗi callback ⇒ tính năng chết hoàn toàn im lặng."""
    from pathlib import Path
    ctrl = (Path(__file__).resolve().parents[2]
            / "Isas.CampaignService/Controllers/SessionFlagController.cs")
    if not ctrl.exists():
        pytest.skip("không tìm thấy SessionFlagController.cs (chạy ngoài cây nguồn đầy đủ)")
    assert f'"{multi_voice.SIGNAL_TYPE}"' in ctrl.read_text(encoding="utf-8")


def test_co_scale_int16_khop_MODEL_ma_Dockerfile_ghim():
    """`multi_voice_scale_int16` phải khớp `normalize_samples` của ĐÚNG model Dockerfile đang ghim.

    🔴 Vì sao cần một test có vẻ vòng vo thế này: đặt sai cờ **không ném lỗi và không có triệu
    chứng**. fbank chỉ lệch một hằng số log(32768²), mà bước CMN ngay sau đó trừ trung bình theo
    thời gian nên khử gần hết hằng số ấy ⇒ vector nhúng vẫn "trông hợp lý", vẫn phân cụm được, vẫn
    ra một con số, vẫn cắm được cờ gian lận — chỉ là kém chính xác hơn, âm thầm.

    Phát hiện qua mutation: lật cờ này chạy qua XANH toàn bộ 1137 test, vì mọi test fbank đều
    truyền `scale_to_int16` TƯỜNG MINH còn test multi_voice thì thay hẳn `embed`. Không có gì nối
    GIÁ TRỊ MẶC ĐỊNH với model thật.

    Khoá bằng cách đọc URL trong Dockerfile (nguồn sự thật về model nào được nạp) rồi đối chiếu với
    `normalize_samples` mà chính file ONNX đó khai:
      • WeSpeaker VoxCeleb CAM++  → normalize_samples=0 → PHẢI nhân 32768 → scale_int16=True
      • 3D-Speaker CAM++ (campplus) → normalize_samples=1 → mẫu ở [-1,1] → scale_int16=False
    """
    from pathlib import Path

    dockerfile = (Path(__file__).resolve().parents[1] / "Dockerfile").read_text(encoding="utf-8")
    pinned = [ln for ln in dockerfile.splitlines()
              if "speaker-embedding.onnx" in ln or ".onnx" in ln and "sherpa-onnx" in ln]
    joined = "\n".join(pinned) + dockerfile

    known = {"wespeaker": True, "3dspeaker": False, "campplus": False}
    matched = [flag for key, flag in known.items() if key in joined.lower()]
    assert matched, ("Dockerfile không ghim model nhúng giọng nào mà test này nhận ra — thêm model "
                     "mới thì phải bổ sung `normalize_samples` của nó vào `known` ở đây")
    assert len(set(matched)) == 1, f"Dockerfile ghim nhiều họ model mâu thuẫn nhau: {matched}"
    assert settings.multi_voice_scale_int16 is matched[0], (
        f"model Dockerfile ghim cần scale_int16={matched[0]} nhưng config mặc định "
        f"{settings.multi_voice_scale_int16}")


def test_dockerfile_ghim_ca_URL_lan_sha256_va_TAI_LUC_BUILD():
    """Model quyết định một cáo buộc gian lận ⇒ phải ghim checksum, không chỉ URL.

    Thiếu sha256 thì "cùng một commit" vẫn ra hai image khác nhau nếu upstream thay file — và
    không có gì báo cho ai biết. Phải tải lúc BUILD: một lượt tải 28 MB nằm giữa đường chấm là
    thêm một cách hỏng mới cho đường tiền, và nó sẽ hỏng đúng lúc mạng đang tệ.
    """
    from pathlib import Path

    dockerfile = (Path(__file__).resolve().parents[1] / "Dockerfile").read_text(encoding="utf-8")
    assert "speaker-embedding.onnx" in dockerfile
    assert "https://" in dockerfile
    # sha256 64 ký tự hex phải có mặt (dù ghim bằng ARG hay `ADD --checksum=`).
    assert re.search(r"\b[0-9a-f]{64}\b", dockerfile), "model phải ghim sha256"
    # Và checksum phải THẬT SỰ được đối chiếu — ghim một chuỗi hex rồi không so là trang trí.
    assert "hashlib.sha256" in dockerfile or "--checksum=sha256:" in dockerfile, (
        "sha256 phải được ĐỐI CHIẾU lúc build, không chỉ ghi ra")
    assert settings.multi_voice_model_path == "/app/models/speaker-embedding.onnx", (
        "đường dẫn model trong config phải khớp chỗ Dockerfile đặt file")


# 🔴 GIÁ TRỊ MẶC ĐỊNH của kill-switch — bổ sung sau khi mutation của người kiểm cho thấy lật
# `multi_voice_enabled` từ False sang True vẫn XANH 1140/1140: cả ba chỗ chạm cờ này đều
# `monkeypatch.setattr` tường minh, nên KHÔNG chỗ nào phủ giá trị MẶC ĐỊNH. Bẫy này đã cắn repo
# trước đây (đổi mặc định `MaxFollowUps` cũng xanh vì mọi test tự dựng options).
#
# ⚠ Phải đọc mặc định KHAI BÁO (`model_fields`), KHÔNG đọc `settings` lúc chạy: file này có
# fixture `autouse` bật cờ cho mọi test, nên assert trên instance sẽ luôn thấy True — tôi đã tự
# vấp đúng chỗ đó. `model_fields` cũng miễn nhiễm với `.env` và biến môi trường của máy chạy test.
#
# Vì sao đáng khoá: B5 là detector EXPERIMENTAL và chính bảng hiệu chuẩn khuyến nghị CHƯA bật
# (25 ca âm đều của MỘT người — F0 median 116–158 Hz; mọi ca dương đều là giọng tổng hợp `say`).
# Bật nhầm nghĩa là phát cờ CÁO BUỘC GIAN LẬN cho HR từ một mô hình chưa hiệu chuẩn trên người
# thật. Bật phải là hành động TƯỜNG MINH qua env, không bao giờ là hệ quả phụ của một lần refactor.
def test_kill_switch_mac_dinh_khai_bao_phai_TAT():
    from app.config import Settings
    assert Settings.model_fields["multi_voice_enabled"].default is False, (
        "MULTI_VOICE_ENABLED phải mặc định TẮT — đọc bảng hiệu chuẩn AC1/B5 trước khi đổi"
    )
