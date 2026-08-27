# app/fbank.py — đặc trưng fbank 80 chiều TƯƠNG THÍCH KALDI, viết bằng numpy thuần.
#
# VÌ SAO PHẢI TỰ VIẾT: model nhúng giọng nói (`app/multi_voice.py`) là bản ONNX xuất từ
# 3D-Speaker/WeSpeaker — cả hai được HUẤN LUYỆN trên fbank của Kaldi, nên đầu vào phải là ĐÚNG
# thứ đó. Thư viện sinh fbank Kaldi trong hệ sinh thái Python đều kéo theo torch
# (`torchaudio.compliance.kaldi`) hoặc thêm một wheel C++ mới (`kaldi-native-fbank`), mà image
# AIService cố ý giữ ~594 MB KHÔNG có torch. numpy đã là dependency sẵn (insightface), và toàn
# bộ phép tính dưới đây chỉ là FFT + nhân ma trận.
#
# 🔴 SAI SỐ Ở ĐÂY KHÔNG NÉM LỖI — nó chỉ làm vector nhúng thành rác, và rác thì vẫn phân cụm
# được, vẫn ra một con số, vẫn cắm được cờ cho HR. Nên bộ này được đối chiếu với bản dựng
# THAM CHIẾU (`torchaudio.compliance.kaldi.fbank`) trong `tests/test_fbank_kaldi_parity.py`
# bằng vector kỳ vọng ghi cứng — xem docstring file đó về cách tái sinh.
#
# Tham số mặc định = đúng bộ Kaldi mà cả hai model dùng lúc huấn luyện:
#   frame 25 ms / hop 10 ms · 80 mel bin · dither 0 · preemph 0.97 · cửa sổ "povey" ·
#   snip_edges=true · remove_dc_offset=true · low 20 Hz / high = Nyquist · log(max(x, eps)).
import numpy as np

SAMPLE_RATE = 16000
FRAME_LENGTH = 400          # 25 ms @ 16 kHz
FRAME_SHIFT = 160           # 10 ms @ 16 kHz
N_FFT = 512                 # Kaldi làm tròn LÊN luỹ thừa 2 gần nhất của FRAME_LENGTH
NUM_MEL_BINS = 80
LOW_FREQ = 20.0
PREEMPH = 0.97
# Kaldi lấy sàn log = FLT_EPSILON (float32), KHÔNG phải eps của float64. Dùng nhầm eps float64
# (2.2e-16) làm sàn thấp đi ~9 đơn vị log, tức khung im lặng ra giá trị khác hẳn lúc huấn luyện.
LOG_FLOOR = float(np.finfo(np.float32).eps)


def _povey_window(length: int) -> np.ndarray:
    """Cửa sổ "povey" của Kaldi = Hann mũ 0,85 (KHÔNG phải Hann/Hamming thuần).

    Kaldi: ``window(i) = pow(0.5 - 0.5*cos(2*pi*i/(N-1)), 0.85)``.
    """
    n = np.arange(length, dtype=np.float64)
    return np.power(0.5 - 0.5 * np.cos(2.0 * np.pi * n / (length - 1)), 0.85)


def _mel_scale(freq):
    """Thang mel của Kaldi (HTK): ``1127 * ln(1 + f/700)``."""
    return 1127.0 * np.log(1.0 + np.asarray(freq, dtype=np.float64) / 700.0)


def _mel_filterbank(num_bins: int = NUM_MEL_BINS, n_fft: int = N_FFT,
                    sample_rate: int = SAMPLE_RATE, low_freq: float = LOW_FREQ,
                    high_freq: float | None = None) -> np.ndarray:
    """Ma trận lọc mel tam giác ``[num_bins, n_fft//2]`` theo đúng ``MelBanks`` của Kaldi.

    ⚠ Số cột là ``n_fft//2`` = **256**, KHÔNG phải 257. Kaldi tính phổ công suất 257 điểm nhưng
    ``MelBanks`` chỉ khai vector dài ``n_fft/2`` ⇒ bin Nyquist bị BỎ. Lấy 257 sẽ lệch nhẹ ở bin
    cuối — đủ để vector nhúng trôi mà không có triệu chứng nào.
    """
    if high_freq is None or high_freq <= 0:
        high_freq = sample_rate / 2.0

    num_fft_bins = n_fft // 2
    fft_bin_width = sample_rate / float(n_fft)

    mel_low = _mel_scale(low_freq)
    mel_high = _mel_scale(high_freq)
    mel_delta = (mel_high - mel_low) / (num_bins + 1)

    # mel của TÂM từng bin FFT — tính một lần, dùng lại cho mọi filter.
    fft_freqs = fft_bin_width * np.arange(num_fft_bins, dtype=np.float64)
    fft_mels = _mel_scale(fft_freqs)

    banks = np.zeros((num_bins, num_fft_bins), dtype=np.float64)
    for i in range(num_bins):
        left = mel_low + i * mel_delta
        center = left + mel_delta
        right = center + mel_delta
        # Sườn lên rồi sườn xuống; ngoài (left, right) = 0. So sánh NGẶT hai đầu như Kaldi
        # (`mel > left && mel < right`) để bin rơi đúng biên nhận trọng số 0.
        up = (fft_mels - left) / (center - left)
        down = (right - fft_mels) / (right - center)
        weights = np.minimum(up, down)
        banks[i] = np.where((fft_mels > left) & (fft_mels < right), weights, 0.0)
    return banks


# Ma trận lọc dựng MỘT LẦN lúc import: nó chỉ phụ thuộc hằng số, mà dựng lại mỗi câu trả lời là
# lặp vòng 80 × 256 cho không.
_MEL_BANKS = _mel_filterbank()
_WINDOW = _povey_window(FRAME_LENGTH)


def compute_fbank(pcm: np.ndarray, *, scale_to_int16: bool) -> np.ndarray:
    """PCM 16 kHz (float, biên độ [-1, 1]) → fbank ``[T, 80]`` float32.

    ``scale_to_int16`` — theo metadata ``normalize_samples`` của chính file ONNX:
      • ``False`` (3D-Speaker, ``normalize_samples=1``): giữ biên độ [-1, 1].
      • ``True`` (WeSpeaker, ``normalize_samples=0``): nhân 32768 về thang int16.
    Sai vế này KHÔNG ném lỗi — log-mel chỉ dịch đi một hằng số, và vì bước sau trừ trung bình
    theo thời gian (CMN) nên phần lớn hằng số đó bị khử ⇒ vector nhúng vẫn "trông hợp lý" mà
    lệch. Đó là lý do nó là tham số BẮT BUỘC (không có mặc định) chứ không phải cờ tuỳ chọn.
    """
    x = np.asarray(pcm, dtype=np.float64).reshape(-1)
    if scale_to_int16:
        x = x * 32768.0

    # snip_edges=true: chỉ lấy khung NẰM TRỌN trong tín hiệu, không đệm hai đầu.
    if x.shape[0] < FRAME_LENGTH:
        return np.zeros((0, NUM_MEL_BINS), dtype=np.float32)
    num_frames = 1 + (x.shape[0] - FRAME_LENGTH) // FRAME_SHIFT

    # Dựng ma trận khung [T, 400] bằng stride trick — vòng for Python trên hàng nghìn khung là
    # phần chậm nhất của cả detector.
    idx = np.arange(FRAME_LENGTH)[None, :] + FRAME_SHIFT * np.arange(num_frames)[:, None]
    frames = x[idx]

    # remove_dc_offset: trừ trung bình CỦA TỪNG KHUNG (không phải của cả tín hiệu).
    frames = frames - frames.mean(axis=1, keepdims=True)

    # Preemphasis của Kaldi: chạy NGƯỢC từ cuối, và mẫu ĐẦU dùng chính nó làm mẫu trước
    # (`data[0] -= coeff * data[0]`), không phải 0.
    prev = np.concatenate([frames[:, :1], frames[:, :-1]], axis=1)
    frames = frames - PREEMPH * prev

    frames = frames * _WINDOW

    spectrum = np.fft.rfft(frames, n=N_FFT, axis=1)
    # use_power=true → |X|²; bỏ bin Nyquist để khớp bề rộng _MEL_BANKS (xem _mel_filterbank).
    power = (spectrum.real ** 2 + spectrum.imag ** 2)[:, :N_FFT // 2]

    mel_energies = power @ _MEL_BANKS.T
    return np.log(np.maximum(mel_energies, LOG_FLOOR)).astype(np.float32)
