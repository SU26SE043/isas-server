"""Redis coordination for TTS cache misses across AIService replicas.

MP3 bytes remain in object storage. Redis only holds short-lived locks and ready
signals, so losing Redis never loses audio and a Redis outage can fail open.
"""

from __future__ import annotations

import asyncio
import hashlib
import logging
import secrets
import time
from collections.abc import Awaitable, Callable

from redis.asyncio import Redis


logger = logging.getLogger(__name__)

CacheReader = Callable[[], Awaitable[bytes | None]]
AudioCreator = Callable[[], Awaitable[tuple[bytes, str]]]

_RELEASE_IF_OWNER = """
if redis.call('get', KEYS[1]) == ARGV[1] then
  return redis.call('del', KEYS[1])
end
return 0
"""


class TtsCoordinationTimeout(TimeoutError):
    """Another replica is still synthesizing after the bounded wait."""


class TtsRedisCoordinator:
    """Distributed single-flight with a short fail-open circuit breaker."""

    def __init__(
        self,
        redis_url: str,
        *,
        key_prefix: str = "isas:tts:",
        lock_ttl_seconds: float = 120.0,
        wait_timeout_seconds: float = 8.0,
        poll_interval_seconds: float = 0.1,
        ready_ttl_seconds: int = 300,
        socket_timeout_seconds: float = 0.25,
        failure_cooldown_seconds: float = 5.0,
        client: Redis | None = None,
    ) -> None:
        self._redis_url = redis_url.strip()
        self._key_prefix = key_prefix
        self._lock_ttl_ms = max(1, round(lock_ttl_seconds * 1000))
        self._wait_timeout_seconds = max(0.0, wait_timeout_seconds)
        self._poll_interval_seconds = max(0.01, poll_interval_seconds)
        self._ready_ttl_seconds = max(1, ready_ttl_seconds)
        self._socket_timeout_seconds = max(0.05, socket_timeout_seconds)
        self._failure_cooldown_seconds = max(0.0, failure_cooldown_seconds)
        self._client = client
        self._circuit_open_until = 0.0

    @property
    def enabled(self) -> bool:
        return bool(self._redis_url or self._client is not None)

    def _get_client(self) -> Redis:
        if self._client is None:
            self._client = Redis.from_url(
                self._redis_url,
                decode_responses=True,
                socket_connect_timeout=self._socket_timeout_seconds,
                socket_timeout=self._socket_timeout_seconds,
                health_check_interval=30,
                retry_on_timeout=False,
            )
        return self._client

    def _keys(self, cache_key: str) -> tuple[str, str]:
        digest = hashlib.sha256(cache_key.encode("utf-8")).hexdigest()
        return (
            f"{self._key_prefix}lock:{digest}",
            f"{self._key_prefix}ready:{digest}",
        )

    def _redis_available(self) -> bool:
        return self.enabled and time.monotonic() >= self._circuit_open_until

    def _mark_redis_failure(self, operation: str, error: Exception) -> None:
        self._circuit_open_until = time.monotonic() + self._failure_cooldown_seconds
        logger.warning(
            "Redis TTS lỗi khi %s; fail-open %.1fs: %s",
            operation,
            self._failure_cooldown_seconds,
            error,
        )

    async def _try_acquire(self, lock_key: str, token: str) -> bool | None:
        """Return True/False, or None when Redis is unavailable (fail-open)."""
        if not self._redis_available():
            return None
        try:
            return bool(await self._get_client().set(
                lock_key, token, nx=True, px=self._lock_ttl_ms))
        except Exception as error:
            self._mark_redis_failure("lấy lock", error)
            return None

    async def _is_ready(self, ready_key: str) -> bool | None:
        if not self._redis_available():
            return None
        try:
            return bool(await self._get_client().get(ready_key))
        except Exception as error:
            self._mark_redis_failure("đọc ready signal", error)
            return None

    async def _mark_ready(self, ready_key: str) -> None:
        if not self._redis_available():
            return
        try:
            await self._get_client().set(
                ready_key, "1", ex=self._ready_ttl_seconds)
        except Exception as error:
            # Audio đã nằm trong object storage; thiếu signal chỉ làm waiter thử lock lại.
            self._mark_redis_failure("ghi ready signal", error)

    async def _clear_stale_ready(self, ready_key: str) -> None:
        if not self._redis_available():
            return
        try:
            await self._get_client().delete(ready_key)
        except Exception as error:
            self._mark_redis_failure("xóa ready signal cũ", error)

    async def _release(self, lock_key: str, token: str) -> None:
        # Nếu chính owner vừa gặp lỗi khi ghi ready signal thì circuit đang mở, nhưng vẫn nên thử
        # nhả lease đúng MỘT lần. Compare-token Lua an toàn; thất bại thì TTL là lớp cuối.
        if not self.enabled:
            return
        try:
            await self._get_client().eval(_RELEASE_IF_OWNER, 1, lock_key, token)
        except Exception as error:
            # TTL vẫn bảo đảm lock tự hết; không được che lấp kết quả TTS đã thành công.
            self._mark_redis_failure("nhả lock", error)

    async def _run_as_owner(
        self,
        lock_key: str,
        ready_key: str,
        token: str,
        read_cache: CacheReader,
        create_audio: AudioCreator,
    ) -> tuple[bytes, str]:
        try:
            # Double-check sau khi lấy lock: replica trước có thể vừa ghi xong giữa hai bước.
            cached = await read_cache()
            if cached:
                await self._mark_ready(ready_key)
                return cached, "hit"

            audio, cache_state = await create_audio()
            if cache_state == "miss":
                await self._mark_ready(ready_key)
            return audio, cache_state
        finally:
            await self._release(lock_key, token)

    async def get_or_create(
        self,
        cache_key: str,
        read_cache: CacheReader,
        create_audio: AudioCreator,
    ) -> tuple[bytes, str]:
        """Join another replica's synthesis, or become the sole producer.

        Redis failure is fail-open and calls ``create_audio`` immediately. A healthy
        Redis lock never times out into another vendor call: waiters return a bounded
        error so the frontend can use its speech fallback while the owner finishes.
        """
        if not self.enabled:
            return await create_audio()

        lock_key, ready_key = self._keys(cache_key)
        token = secrets.token_hex(16)
        deadline = time.monotonic() + self._wait_timeout_seconds

        while True:
            ready = await self._is_ready(ready_key)
            if ready is None:
                return await create_audio()
            if ready:
                cached = await read_cache()
                if cached:
                    return cached, "hit"
                # Object bị xoá nhưng signal còn TTL: dọn để không quay nóng vô hạn.
                await self._clear_stale_ready(ready_key)

            acquired = await self._try_acquire(lock_key, token)
            if acquired is None:
                return await create_audio()
            if acquired:
                return await self._run_as_owner(
                    lock_key, ready_key, token, read_cache, create_audio)

            remaining = deadline - time.monotonic()
            if remaining <= 0:
                # Một lần kiểm tra storage cuối tránh báo timeout đúng lúc owner vừa ghi xong.
                cached = await read_cache()
                if cached:
                    return cached, "hit"
                raise TtsCoordinationTimeout(
                    "TTS đang được một AIService replica khác tổng hợp")
            await asyncio.sleep(min(self._poll_interval_seconds, remaining))

    async def close(self) -> None:
        if self._client is None:
            return
        try:
            await self._client.aclose()
        finally:
            self._client = None
