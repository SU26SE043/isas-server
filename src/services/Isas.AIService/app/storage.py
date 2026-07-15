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
