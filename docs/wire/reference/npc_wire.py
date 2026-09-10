"""NPC 서버 링크 프로토콜 — 파이썬 참조 코덱 (B-03).

이 파일은 **다른 언어로 게임서버를 구현할 때 베끼는 것**이다. 배치의 근거는
``docs/wire/layout_v2.md`` 이고, 이 코덱이 맞는지는 ``docs/wire/vectors_v2/`` 의
골든 바이트로 스스로 확인한다::

    python docs/wire/reference/npc_wire.py

**손으로 고치기 전에 읽는다.** 오프셋 표는 C# 코드에서 뽑는 생성물이라, 표가 바뀌면
이 파일도 같이 바뀌어야 한다. ``WireReferenceTests`` 가 두 쪽의 크기·오프셋을 대조한다.

규칙 세 가지만 기억하면 된다.

* **리틀엔디언**이다. 예외는 ``WireNonce`` 하나로, HMAC 입력이라 빅엔디언으로 푼다
  (그 변환은 인증을 구현할 때 하는 것이고, 와이어에 실린 순서는 여기 적힌 대로다).
* **패딩은 명시한다.** v1/v2 핸드셰이크에는 정렬 구멍이 있고, 원시 복사라 그 바이트도
  그대로 나간다. 아래 포맷 문자열의 ``x`` 가 그것이다. **쓰는 쪽은 0 으로 채운다.**
* **배치는 int32 접두 + 고정 크기 원소**다. 원소 사이에 태그가 없다.
  접두가 **음수면 null 배열**이고 빈 배치(0)와 다르다.
"""

from __future__ import annotations

import pathlib
import struct
import sys

# ---------------------------------------------------------------- 프레임

HEADER_SIZE = 8
HEADER_FORMAT = "<IBBH"          # PayloadLength, Kind, Ver, Reserved

MAX_PAYLOAD = 1 << 20            # 1 MiB. 길이 접두 프로토콜에서 이 검사를 빼지 않는다
MIN_VERSION = 1
MAX_VERSION = 2

KIND_HELLO = 1
KIND_HELLO_ACK = 2
KIND_COMMAND_BATCH = 3
KIND_EVENT_BATCH = 4
KIND_HEARTBEAT = 5
KIND_BYE = 6

KIND_NAMES = {
    KIND_HELLO: "Hello",
    KIND_HELLO_ACK: "HelloAck",
    KIND_COMMAND_BATCH: "CommandBatch",
    KIND_EVENT_BATCH: "EventBatch",
    KIND_HEARTBEAT: "Heartbeat",
    KIND_BYE: "Bye",
}


def read_frame(buffer: bytes | bytearray):
    """버퍼 앞에서 완전한 프레임 하나를 떼어낸다.

    반환은 ``(kind, version, payload, rest)``. 아직 다 안 왔으면 ``None`` 이다 —
    TCP 는 경계를 지켜 주지 않으므로 **더 읽고 다시 부르는 것이 정상 경로**다.
    """
    if len(buffer) < HEADER_SIZE:
        return None

    length, kind, version, _reserved = struct.unpack_from(HEADER_FORMAT, buffer, 0)

    # 버전을 길이보다 먼저 본다 — 버전이 다르면 길이 해석 자체를 믿을 수 없다.
    if not (MIN_VERSION <= version <= MAX_VERSION):
        raise ValueError(f"와이어 버전이 범위를 벗어난다: {version}")
    if length > MAX_PAYLOAD:
        raise ValueError(f"페이로드가 상한을 넘는다: {length}B — 프레임 경계가 밀렸다")

    total = HEADER_SIZE + length
    if len(buffer) < total:
        return None

    return kind, version, bytes(buffer[HEADER_SIZE:total]), bytes(buffer[total:])


def write_frame(kind: int, version: int, payload: bytes) -> bytes:
    """프레임 하나를 만든다."""
    if not (MIN_VERSION <= version <= MAX_VERSION):
        raise ValueError(f"와이어 버전이 범위를 벗어난다: {version}")
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("페이로드가 상한을 넘는다")

    return struct.pack(HEADER_FORMAT, len(payload), kind, version, 0) + payload


# ---------------------------------------------------------------- 메시지 배치
#
# 포맷 문자열의 '<' 는 리틀엔디언 + **정렬 없음**이다. 그래서 패딩을 'x' 로 직접 적는다.
# 이름 목록의 순서가 곧 struct.unpack 결과의 순서다.

COMMAND_V2_FORMAT = "<q4iI3f3I8H4B"
COMMAND_V2_SIZE = 72
COMMAND_V2_FIELDS = (
    "IssuedAt",
    "Npc", "TargetNpc", "TargetPlayer", "Amount",
    "Correlation",
    "PosX", "PosY", "PosZ",
    "ExtA", "ExtB", "Reserved",
    "TargetPoi", "Item", "Animation", "Dialogue", "Archetype", "Zone", "Instance", "Faction",
    "Kind", "Priority", "Visual", "Flags",
)

EVENT_V2_FORMAT = "<2q4iI4f3I5H2h2B"
EVENT_V2_SIZE = 80
EVENT_V2_FIELDS = (
    "Sequence", "OccurredAt",
    "Npc", "OtherNpc", "Player", "Amount",
    "Correlation",
    "PosX", "PosY", "PosZ", "Heading",
    "ExtA", "ExtB", "Reserved",
    "Poi", "Zone", "Item", "Instance", "Faction",
    "Hp", "Stamina",
    "Kind", "Code",
)

COMMAND_V1_FORMAT = "<q4iI3f6H4B"
COMMAND_V1_SIZE = 56
COMMAND_V1_FIELDS = (
    "IssuedAt",
    "Npc", "TargetNpc", "TargetPlayer", "Amount",
    "Correlation",
    "PosX", "PosY", "PosZ",
    "TargetPoi", "Item", "Animation", "Dialogue", "Archetype", "Zone",
    "Kind", "Priority", "Visual", "Flags",
)

EVENT_V1_FORMAT = "<2q4iI4f3H2h2B"
EVENT_V1_SIZE = 64
EVENT_V1_FIELDS = (
    "Sequence", "OccurredAt",
    "Npc", "OtherNpc", "Player", "Amount",
    "Correlation",
    "PosX", "PosY", "PosZ", "Heading",
    "Poi", "Zone", "Item",
    "Hp", "Stamina",
    "Kind", "Code",
)

# 핸드셰이크. 'x' 가 정렬 구멍이고 **쓰는 쪽은 0 으로 채운다**.
HELLO_FORMAT = "<4iq8Q"
HELLO_SIZE = 88

HELLO_ACK_FORMAT = "<3i4x8Q2B6x"
HELLO_ACK_SIZE = 88

HELLO_V2_FORMAT = "<2i2H4xQ3i4xq2H4xQI4x18Q"
HELLO_V2_SIZE = 216
HELLO_V2_FIELDS = (
    "ProtocolVersion", "MinProtocolVersion",
    "ContractMajor", "ContractMinor",
    "Features",
    "TickRate", "TimeScale", "NpcCount",
    "StartTick",
    "StartGameMinuteOfDay", "ShardId",
    "ZoneMask",
    "SessionEpoch",
    "MasterDataStructural.A", "MasterDataStructural.B",
    "MasterDataStructural.C", "MasterDataStructural.D",
    "MasterDataContent.A", "MasterDataContent.B",
    "MasterDataContent.C", "MasterDataContent.D",
    "Roster.A", "Roster.B", "Roster.C", "Roster.D",
    "Nonce.A", "Nonce.B",
    "Auth.A", "Auth.B", "Auth.C", "Auth.D",
)

HELLO_ACK_V2_FORMAT = "<i2HQ2i16Q3B5x"
HELLO_ACK_V2_SIZE = 160

HEARTBEAT_FORMAT = "<2q"
HEARTBEAT_SIZE = 16

BYE_FORMAT = "<B"
BYE_SIZE = 1

# 크기를 여기서 못 박는다. C# 의 static_assert 자리다.
for _name, _format, _size in (
    ("WireCommandV2", COMMAND_V2_FORMAT, COMMAND_V2_SIZE),
    ("WireEventV2", EVENT_V2_FORMAT, EVENT_V2_SIZE),
    ("WireCommand", COMMAND_V1_FORMAT, COMMAND_V1_SIZE),
    ("WireEvent", EVENT_V1_FORMAT, EVENT_V1_SIZE),
    ("WireHello", HELLO_FORMAT, HELLO_SIZE),
    ("WireHelloAck", HELLO_ACK_FORMAT, HELLO_ACK_SIZE),
    ("WireHelloV2", HELLO_V2_FORMAT, HELLO_V2_SIZE),
    ("WireHelloAckV2", HELLO_ACK_V2_FORMAT, HELLO_ACK_V2_SIZE),
    ("WireHeartbeat", HEARTBEAT_FORMAT, HEARTBEAT_SIZE),
    ("WireBye", BYE_FORMAT, BYE_SIZE),
):
    if struct.calcsize(_format) != _size:
        raise AssertionError(
            f"{_name}: 포맷 크기 {struct.calcsize(_format)}B != 선언 {_size}B"
        )


# ---------------------------------------------------------------- 배치

def read_batch(payload: bytes, item_format: str, item_size: int, fields):
    """``int32`` 접두 + 고정 크기 원소를 읽는다.

    **음수 접두는 null 배열**이라 ``None`` 을 준다. 빈 배치(0)는 ``[]`` 다 — 둘은 다르다.
    길이가 안 맞으면 예외다. **잘라 읽지 않는다** — 프레임 경계가 밀린 스트림을 조용히
    해석하면 그때부터 전부 쓰레기다.
    """
    if len(payload) < 4:
        raise ValueError("배치 접두가 없다")

    count = struct.unpack_from("<i", payload, 0)[0]

    if count < 0:
        return None
    if count == 0:
        return []

    needed = 4 + count * item_size
    if len(payload) < needed:
        raise ValueError(f"배치가 잘렸다: {len(payload)}B < {needed}B")

    items = []
    for i in range(count):
        values = struct.unpack_from(item_format, payload, 4 + i * item_size)
        items.append(dict(zip(fields, values, strict=True)))

    return items


def write_batch(items, item_format: str, fields) -> bytes:
    """배치 하나를 만든다."""
    out = bytearray(struct.pack("<i", len(items)))
    for item in items:
        out += struct.pack(item_format, *(item[name] for name in fields))
    return bytes(out)


def read_command_batch(payload: bytes, version: int = 2):
    """명령 배치. **프레임의 ``Ver`` 로 고른다** — 협상 결과로 고르지 않는다."""
    if version >= 2:
        return read_batch(payload, COMMAND_V2_FORMAT, COMMAND_V2_SIZE, COMMAND_V2_FIELDS)
    return read_batch(payload, COMMAND_V1_FORMAT, COMMAND_V1_SIZE, COMMAND_V1_FIELDS)


def read_event_batch(payload: bytes, version: int = 2):
    """이벤트 배치."""
    if version >= 2:
        return read_batch(payload, EVENT_V2_FORMAT, EVENT_V2_SIZE, EVENT_V2_FIELDS)
    return read_batch(payload, EVENT_V1_FORMAT, EVENT_V1_SIZE, EVENT_V1_FIELDS)


# ---------------------------------------------------------------- 자체 시험

VECTORS = pathlib.Path(__file__).resolve().parent.parent / "vectors_v2"


def _load(name: str) -> bytes:
    text = (VECTORS / f"{name}.hex").read_text(encoding="utf-8")
    return bytes.fromhex("".join(text.split()))


def self_test() -> int:
    """골든 벡터로 이 코덱을 확인한다. 통과하면 0."""
    failures = []

    commands = read_command_batch(_load("command_batch_v2"), version=2)
    if commands is None or len(commands) != 2:
        failures.append(f"command_batch_v2: 원소 수가 다르다 ({commands and len(commands)})")
    else:
        first = commands[0]
        # 값은 tests/Npc.Tests/Wire/WireWriterTests.cs 의 Command(1) 이다.
        expected = {"Npc": 3, "Instance": 19, "Faction": 20, "ExtA": 11, "ExtB": 12, "Reserved": 0}
        for key, value in expected.items():
            if first[key] != value:
                failures.append(f"command_batch_v2[0].{key}: {first[key]} != {value}")
        if abs(first["PosX"] - 8.5) > 1e-6:
            failures.append(f"command_batch_v2[0].PosX: {first['PosX']} != 8.5")

    events = read_event_batch(_load("event_batch_v2"), version=2)
    if events is None or len(events) != 3:
        failures.append("event_batch_v2: 원소 수가 다르다")
    else:
        third = events[2]
        expected = {"Npc": 6, "Instance": 20, "Faction": 21, "Hp": 22, "Stamina": -23}
        for key, value in expected.items():
            if third[key] != value:
                failures.append(f"event_batch_v2[2].{key}: {third[key]} != {value}")

    empty = read_command_batch(_load("command_batch_v2_empty"), version=2)
    if empty != []:
        failures.append(f"command_batch_v2_empty: {empty} != []")

    hello = struct.unpack(HELLO_V2_FORMAT, _load("hello_v2"))
    hello_map = dict(zip(HELLO_V2_FIELDS, hello, strict=True))
    for key, value in (
        ("ProtocolVersion", 2),
        ("MinProtocolVersion", 1),
        ("ContractMajor", 1),
        ("ContractMinor", 2),
        ("TickRate", 10),
        ("TimeScale", 600),
        ("NpcCount", 5000),
        ("StartTick", 1234567),
        ("StartGameMinuteOfDay", 361),
        ("MasterDataStructural.A", 0x0102030405060708),
        ("Nonce.A", 17),
        ("Auth.D", 16),
    ):
        if hello_map[key] != value:
            failures.append(f"hello_v2.{key}: {hello_map[key]} != {value}")

    ack = struct.unpack(HELLO_ACK_V2_FORMAT, _load("hello_ack_v2"))
    if ack[0] != 2:
        failures.append(f"hello_ack_v2.ProtocolVersion: {ack[0]} != 2")

    beat = struct.unpack(HEARTBEAT_FORMAT, _load("heartbeat"))
    if beat != (1234567, 89012):
        failures.append(f"heartbeat: {beat} != (1234567, 89012)")

    bye = struct.unpack(BYE_FORMAT, _load("bye"))
    if bye != (0,):                       # LinkByeCode.Shutdown = 0
        failures.append(f"bye: {bye} != (0,)")

    # 왕복. 읽기만 맞고 쓰기가 틀린 코덱은 상대만 깨뜨린다.
    if commands is not None:
        again = write_batch(commands, COMMAND_V2_FORMAT, COMMAND_V2_FIELDS)
        if again != _load("command_batch_v2"):
            failures.append("command_batch_v2: 다시 쓴 바이트가 다르다")

    for line in failures:
        print(f"FAIL {line}")

    if failures:
        print(f"\n{len(failures)}건 실패.")
        return 1

    print("참조 코덱(python): 골든 벡터 전부 통과.")
    return 0


if __name__ == "__main__":
    sys.exit(self_test())
