"""Distributed TTS single-flight: two coordinators model two AIService replicas."""

import asyncio

import pytest

from app.tts_redis import TtsCoordinationTimeout, TtsRedisCoordinator


class FakeRedis:
    def __init__(self) -> None:
        self.values: dict[str, str] = {}
        self.down = False

    async def set(self, key, value, *, nx=False, px=None, ex=None):
        del px, ex
        if self.down:
            raise ConnectionError("redis down")
        if nx and key in self.values:
            return False
        self.values[key] = value
        return True

    async def get(self, key):
        if self.down:
            raise ConnectionError("redis down")
        return self.values.get(key)

    async def delete(self, key):
        if self.down:
            raise ConnectionError("redis down")
        return int(self.values.pop(key, None) is not None)

    async def eval(self, _script, _num_keys, key, token):
        if self.down:
            raise ConnectionError("redis down")
        if self.values.get(key) == token:
            del self.values[key]
            return 1
        return 0

    async def aclose(self):
        return None


def coordinator(redis: FakeRedis, *, wait=0.5) -> TtsRedisCoordinator:
    return TtsRedisCoordinator(
        "redis://test",
        client=redis,
        wait_timeout_seconds=wait,
        poll_interval_seconds=0.005,
        failure_cooldown_seconds=1.0,
    )


@pytest.mark.asyncio
async def test_hai_replica_chi_mot_replica_goi_vendor():
    redis = FakeRedis()
    replica_a = coordinator(redis)
    replica_b = coordinator(redis)
    object_store: dict[str, bytes] = {}
    owner_started = asyncio.Event()
    release_owner = asyncio.Event()
    vendor_calls = 0

    async def read_cache():
        return object_store.get("question")

    async def owner_create():
        nonlocal vendor_calls
        vendor_calls += 1
        owner_started.set()
        await release_owner.wait()
        object_store["question"] = b"mp3"
        return b"mp3", "miss"

    async def waiter_must_not_create():
        raise AssertionError("replica chờ không được gọi vendor")

    owner = asyncio.create_task(replica_a.get_or_create(
        "tts/key.mp3", read_cache, owner_create))
    await owner_started.wait()
    waiter = asyncio.create_task(replica_b.get_or_create(
        "tts/key.mp3", read_cache, waiter_must_not_create))
    await asyncio.sleep(0.02)

    assert vendor_calls == 1
    release_owner.set()
    assert await owner == (b"mp3", "miss")
    assert await waiter == (b"mp3", "hit")


@pytest.mark.asyncio
async def test_waiter_het_tran_khong_goi_vendor_trung():
    redis = FakeRedis()
    owner_coordinator = coordinator(redis, wait=1.0)
    waiter_coordinator = coordinator(redis, wait=0.02)
    owner_started = asyncio.Event()
    release_owner = asyncio.Event()
    waiter_vendor_calls = 0

    async def read_cache():
        return None

    async def owner_create():
        owner_started.set()
        await release_owner.wait()
        return b"mp3", "miss-nostore"

    async def waiter_create():
        nonlocal waiter_vendor_calls
        waiter_vendor_calls += 1
        return b"duplicate", "miss"

    owner = asyncio.create_task(owner_coordinator.get_or_create(
        "tts/key.mp3", read_cache, owner_create))
    await owner_started.wait()

    with pytest.raises(TtsCoordinationTimeout):
        await waiter_coordinator.get_or_create(
            "tts/key.mp3", read_cache, waiter_create)
    assert waiter_vendor_calls == 0

    release_owner.set()
    await owner


@pytest.mark.asyncio
async def test_redis_hong_fail_open_ngay_va_circuit_breaker():
    redis = FakeRedis()
    redis.down = True
    instance = coordinator(redis)
    create_calls = 0

    async def read_cache():
        return None

    async def create_audio():
        nonlocal create_calls
        create_calls += 1
        return b"mp3", "miss"

    assert await instance.get_or_create(
        "tts/key.mp3", read_cache, create_audio) == (b"mp3", "miss")
    assert await instance.get_or_create(
        "tts/key-2.mp3", read_cache, create_audio) == (b"mp3", "miss")
    assert create_calls == 2


@pytest.mark.asyncio
async def test_ready_signal_cu_khong_lam_quay_vong_vo_han():
    redis = FakeRedis()
    instance = coordinator(redis)
    lock_key, ready_key = instance._keys("tts/key.mp3")
    del lock_key
    redis.values[ready_key] = "1"
    create_calls = 0

    async def read_cache():
        return None

    async def create_audio():
        nonlocal create_calls
        create_calls += 1
        return b"new", "miss"

    assert await instance.get_or_create(
        "tts/key.mp3", read_cache, create_audio) == (b"new", "miss")
    assert create_calls == 1

