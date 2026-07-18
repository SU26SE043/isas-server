# app/storage.py — helper tải object từ SeaweedFS/S3 (dùng chung).
#
# Gom client boto3 (trước đây inline trong worker.py) thành 1 fetch tái dùng cho main.py —
# endpoint HTTP (vd /face-verify) trước giờ KHÔNG có client S3. worker.py vẫn giữ client
# riêng của nó (module-level) cho luồng chấm audio; module này chỉ cấp thêm `get_object_bytes`.
#
# boto3 import LAZY (trong _client) để test import được app.main mà KHÔNG cần boto3 —
# test monkeypatch get_object_bytes nên client thật không bao giờ được dựng.
import io
from functools import lru_cache

from app.config import settings


@lru_cache(maxsize=1)
def _client():
    import boto3  # lazy: chỉ cần khi thực sự tải file

    return boto3.client(
        "s3",
        endpoint_url=settings.s3_endpoint,
        aws_access_key_id=settings.s3_access_key,
        aws_secret_access_key=settings.s3_secret_key,
    )


def get_object_bytes(key: str) -> bytes:
    """Tải object theo key từ bucket cấu hình → bytes (giữ trong RAM).

    Ảnh face nhỏ nên đọc thẳng bytes để decode (không cần temp-file như audio worker)."""
    buf = io.BytesIO()
    _client().download_fileobj(settings.s3_bucket, key, buf)
    return buf.getvalue()


def _is_not_found(ex: Exception) -> bool:
    """Phân biệt "object không tồn tại" với lỗi S3 THẬT.

    Không import botocore ở module-level (giữ boto3 lazy như _client) → nhận diện qua
    dict `response` của ClientError. SeaweedFS trả 404/NoSuchKey giống S3."""
    response = getattr(ex, "response", None)
    if not isinstance(response, dict):
        return False
    code = str((response.get("Error") or {}).get("Code", ""))
    status = (response.get("ResponseMetadata") or {}).get("HTTPStatusCode")
    return code in {"404", "NoSuchKey"} or status == 404


def try_get_object_bytes(key: str) -> bytes | None:
    """Như get_object_bytes nhưng object KHÔNG tồn tại → None thay vì ném.

    Dùng cho cache TTS: "chưa có" là đường đi BÌNH THƯỜNG (cache miss), không phải lỗi.
    Cố tình chỉ 1 round-trip (GET) thay vì HEAD-rồi-GET — có object thì lấy luôn.
    Lỗi S3 KHÁC (mạng/credential/bucket sai) vẫn ném ra: nuốt hết sẽ biến sự cố hạ tầng
    thành "cache miss vĩnh viễn" → gọi vendor mỗi request và âm thầm đốt tiền."""
    try:
        return get_object_bytes(key)
    except Exception as ex:
        if _is_not_found(ex):
            return None
        raise


def put_object_bytes(key: str, data: bytes, content_type: str) -> None:
    """Ghi bytes lên bucket cấu hình theo key.

    GEN-5: caller giữ KEY (không lưu full URL). GEN-4 vẫn được tôn trọng — đây là object
    storage, KHÔNG phải DB; AIService không ghi bảng nào."""
    _client().put_object(
        Bucket=settings.s3_bucket,
        Key=key,
        Body=data,
        ContentType=content_type,
    )
