// npc_wire.h — NPC 서버 링크 프로토콜의 C++17 참조 코덱 (B-03).
//
// 헤더 온리다. 의존은 표준 라이브러리뿐이고, 예외를 던지지 않는다.
//
// 배치의 근거는 docs/wire/layout_v2.md 이고, 그 표는 C# 코드에서 뽑는 생성물이다.
// 이 헤더가 표와 어긋나면 WireReferenceTests 가 깨진다 — 손으로 고치기 전에 표를 본다.
//
// 규칙 셋만 기억하면 된다.
//
//   1. 리틀엔디언이다. 아래 read_le/write_le 가 빅엔디언 기계에서도 맞게 돈다.
//   2. 패딩은 명시한다. 핸드셰이크에는 정렬 구멍이 있고, 원시 복사라 그 바이트도
//      그대로 나간다. 구조체의 pad_* 멤버가 그것이며 **쓰는 쪽은 0 으로 채운다.**
//   3. 배치는 int32 접두 + 고정 크기 원소다. 접두가 음수면 null 배열이고 빈 배치와 다르다.
//
// 검증 상태: 이 저장소 환경에는 C++ 컴파일러가 없어 **컴파일 확인 미실시**다.
// tools/check_wire_reference.ps1 이 컴파일러가 있으면 컴파일만 해 본다.

#ifndef NPC_WIRE_H
#define NPC_WIRE_H

#include <cstddef>
#include <cstdint>
#include <cstring>

namespace npc_wire {

// ---------------------------------------------------------------- 바이트 헬퍼
//
// memcpy + 시프트다. reinterpret_cast 로 구조체에 직접 겹치지 않는다 —
// 정렬되지 않은 접근과 strict aliasing 위반을 둘 다 피한다.

inline std::uint8_t read_u8(const std::uint8_t* p) { return *p; }

inline std::uint16_t read_u16(const std::uint8_t* p) {
    return static_cast<std::uint16_t>(p[0]) |
           static_cast<std::uint16_t>(static_cast<std::uint16_t>(p[1]) << 8);
}

inline std::uint32_t read_u32(const std::uint8_t* p) {
    return static_cast<std::uint32_t>(p[0]) |
           (static_cast<std::uint32_t>(p[1]) << 8) |
           (static_cast<std::uint32_t>(p[2]) << 16) |
           (static_cast<std::uint32_t>(p[3]) << 24);
}

inline std::uint64_t read_u64(const std::uint8_t* p) {
    return static_cast<std::uint64_t>(read_u32(p)) |
           (static_cast<std::uint64_t>(read_u32(p + 4)) << 32);
}

inline std::int16_t read_i16(const std::uint8_t* p) {
    return static_cast<std::int16_t>(read_u16(p));
}

inline std::int32_t read_i32(const std::uint8_t* p) {
    return static_cast<std::int32_t>(read_u32(p));
}

inline std::int64_t read_i64(const std::uint8_t* p) {
    return static_cast<std::int64_t>(read_u64(p));
}

inline float read_f32(const std::uint8_t* p) {
    // IEEE 754 binary32. 비트 패턴을 그대로 옮긴다.
    const std::uint32_t bits = read_u32(p);
    float value = 0.0f;
    std::memcpy(&value, &bits, sizeof(value));
    return value;
}

inline void write_u8(std::uint8_t* p, std::uint8_t v) { *p = v; }

inline void write_u16(std::uint8_t* p, std::uint16_t v) {
    p[0] = static_cast<std::uint8_t>(v);
    p[1] = static_cast<std::uint8_t>(v >> 8);
}

inline void write_u32(std::uint8_t* p, std::uint32_t v) {
    p[0] = static_cast<std::uint8_t>(v);
    p[1] = static_cast<std::uint8_t>(v >> 8);
    p[2] = static_cast<std::uint8_t>(v >> 16);
    p[3] = static_cast<std::uint8_t>(v >> 24);
}

inline void write_u64(std::uint8_t* p, std::uint64_t v) {
    write_u32(p, static_cast<std::uint32_t>(v));
    write_u32(p + 4, static_cast<std::uint32_t>(v >> 32));
}

inline void write_i16(std::uint8_t* p, std::int16_t v) {
    write_u16(p, static_cast<std::uint16_t>(v));
}

inline void write_i32(std::uint8_t* p, std::int32_t v) {
    write_u32(p, static_cast<std::uint32_t>(v));
}

inline void write_i64(std::uint8_t* p, std::int64_t v) {
    write_u64(p, static_cast<std::uint64_t>(v));
}

inline void write_f32(std::uint8_t* p, float v) {
    std::uint32_t bits = 0;
    std::memcpy(&bits, &v, sizeof(bits));
    write_u32(p, bits);
}

// ---------------------------------------------------------------- 프레임

constexpr std::size_t kHeaderSize = 8;
constexpr std::uint32_t kMaxPayload = 1u << 20;   // 1 MiB
constexpr std::uint8_t kMinVersion = 1;
constexpr std::uint8_t kMaxVersion = 2;

enum class MessageKind : std::uint8_t {
    None = 0,
    Hello = 1,
    HelloAck = 2,
    CommandBatch = 3,
    EventBatch = 4,
    Heartbeat = 5,
    Bye = 6,
};

struct FrameHeader {
    std::uint32_t payload_length;
    std::uint8_t kind;
    std::uint8_t version;
    std::uint16_t reserved;   // 0
};

// 프레임 헤더를 읽는다. 버전·길이 검사에 걸리면 false — 연결을 끊어야 한다.
// 페이로드가 아직 다 안 왔으면 need_more 가 true 이고, 그때는 더 읽고 다시 부른다.
inline bool read_header(const std::uint8_t* buffer, std::size_t size,
                        FrameHeader& out, bool& need_more) {
    need_more = false;

    if (size < kHeaderSize) {
        need_more = true;
        return true;
    }

    out.payload_length = read_u32(buffer);
    out.kind = read_u8(buffer + 4);
    out.version = read_u8(buffer + 5);
    out.reserved = read_u16(buffer + 6);

    // 버전을 길이보다 먼저 본다 — 버전이 다르면 길이 해석 자체를 믿을 수 없다.
    if (out.version < kMinVersion || out.version > kMaxVersion) return false;
    if (out.payload_length > kMaxPayload) return false;

    if (size < kHeaderSize + out.payload_length) need_more = true;

    return true;
}

inline void write_header(std::uint8_t* buffer, MessageKind kind,
                         std::uint8_t version, std::uint32_t payload_length) {
    write_u32(buffer, payload_length);
    write_u8(buffer + 4, static_cast<std::uint8_t>(kind));
    write_u8(buffer + 5, version);
    write_u16(buffer + 6, 0);
}

// ---------------------------------------------------------------- 명령 (v2)

constexpr std::size_t kCommandV2Size = 72;

struct CommandV2 {
    std::int64_t issued_at;
    std::int32_t npc;
    std::int32_t target_npc;
    std::int32_t target_player;
    std::int32_t amount;
    std::uint32_t correlation;
    float pos_x;
    float pos_y;
    float pos_z;
    std::uint32_t ext_a;
    std::uint32_t ext_b;
    std::uint32_t reserved;      // 항상 0
    std::uint16_t target_poi;
    std::uint16_t item;
    std::uint16_t animation;
    std::uint16_t dialogue;
    std::uint16_t archetype;
    std::uint16_t zone;
    std::uint16_t instance;      // 0 = 기본 월드
    std::uint16_t faction;       // 0 = 미지정
    std::uint8_t kind;
    std::uint8_t priority;
    std::uint8_t visual;
    std::uint8_t flags;
};

inline void decode(const std::uint8_t* p, CommandV2& c) {
    c.issued_at = read_i64(p);
    c.npc = read_i32(p + 8);
    c.target_npc = read_i32(p + 12);
    c.target_player = read_i32(p + 16);
    c.amount = read_i32(p + 20);
    c.correlation = read_u32(p + 24);
    c.pos_x = read_f32(p + 28);
    c.pos_y = read_f32(p + 32);
    c.pos_z = read_f32(p + 36);
    c.ext_a = read_u32(p + 40);
    c.ext_b = read_u32(p + 44);
    c.reserved = read_u32(p + 48);
    c.target_poi = read_u16(p + 52);
    c.item = read_u16(p + 54);
    c.animation = read_u16(p + 56);
    c.dialogue = read_u16(p + 58);
    c.archetype = read_u16(p + 60);
    c.zone = read_u16(p + 62);
    c.instance = read_u16(p + 64);
    c.faction = read_u16(p + 66);
    c.kind = read_u8(p + 68);
    c.priority = read_u8(p + 69);
    c.visual = read_u8(p + 70);
    c.flags = read_u8(p + 71);
}

inline void encode(std::uint8_t* p, const CommandV2& c) {
    write_i64(p, c.issued_at);
    write_i32(p + 8, c.npc);
    write_i32(p + 12, c.target_npc);
    write_i32(p + 16, c.target_player);
    write_i32(p + 20, c.amount);
    write_u32(p + 24, c.correlation);
    write_f32(p + 28, c.pos_x);
    write_f32(p + 32, c.pos_y);
    write_f32(p + 36, c.pos_z);
    write_u32(p + 40, c.ext_a);
    write_u32(p + 44, c.ext_b);
    write_u32(p + 48, c.reserved);
    write_u16(p + 52, c.target_poi);
    write_u16(p + 54, c.item);
    write_u16(p + 56, c.animation);
    write_u16(p + 58, c.dialogue);
    write_u16(p + 60, c.archetype);
    write_u16(p + 62, c.zone);
    write_u16(p + 64, c.instance);
    write_u16(p + 66, c.faction);
    write_u8(p + 68, c.kind);
    write_u8(p + 69, c.priority);
    write_u8(p + 70, c.visual);
    write_u8(p + 71, c.flags);
}

// ---------------------------------------------------------------- 이벤트 (v2)

constexpr std::size_t kEventV2Size = 80;

struct EventV2 {
    std::int64_t sequence;
    std::int64_t occurred_at;
    std::int32_t npc;
    std::int32_t other_npc;
    std::int32_t player;
    std::int32_t amount;
    std::uint32_t correlation;
    float pos_x;
    float pos_y;
    float pos_z;
    float heading;
    std::uint32_t ext_a;
    std::uint32_t ext_b;
    std::uint32_t reserved;      // 항상 0
    std::uint16_t poi;
    std::uint16_t zone;
    std::uint16_t item;
    std::uint16_t instance;
    std::uint16_t faction;
    std::int16_t hp;
    std::int16_t stamina;
    std::uint8_t kind;
    std::uint8_t code;
};

inline void decode(const std::uint8_t* p, EventV2& e) {
    e.sequence = read_i64(p);
    e.occurred_at = read_i64(p + 8);
    e.npc = read_i32(p + 16);
    e.other_npc = read_i32(p + 20);
    e.player = read_i32(p + 24);
    e.amount = read_i32(p + 28);
    e.correlation = read_u32(p + 32);
    e.pos_x = read_f32(p + 36);
    e.pos_y = read_f32(p + 40);
    e.pos_z = read_f32(p + 44);
    e.heading = read_f32(p + 48);
    e.ext_a = read_u32(p + 52);
    e.ext_b = read_u32(p + 56);
    e.reserved = read_u32(p + 60);
    e.poi = read_u16(p + 64);
    e.zone = read_u16(p + 66);
    e.item = read_u16(p + 68);
    e.instance = read_u16(p + 70);
    e.faction = read_u16(p + 72);
    e.hp = read_i16(p + 74);
    e.stamina = read_i16(p + 76);
    e.kind = read_u8(p + 78);
    e.code = read_u8(p + 79);
}

inline void encode(std::uint8_t* p, const EventV2& e) {
    write_i64(p, e.sequence);
    write_i64(p + 8, e.occurred_at);
    write_i32(p + 16, e.npc);
    write_i32(p + 20, e.other_npc);
    write_i32(p + 24, e.player);
    write_i32(p + 28, e.amount);
    write_u32(p + 32, e.correlation);
    write_f32(p + 36, e.pos_x);
    write_f32(p + 40, e.pos_y);
    write_f32(p + 44, e.pos_z);
    write_f32(p + 48, e.heading);
    write_u32(p + 52, e.ext_a);
    write_u32(p + 56, e.ext_b);
    write_u32(p + 60, e.reserved);
    write_u16(p + 64, e.poi);
    write_u16(p + 66, e.zone);
    write_u16(p + 68, e.item);
    write_u16(p + 70, e.instance);
    write_u16(p + 72, e.faction);
    write_i16(p + 74, e.hp);
    write_i16(p + 76, e.stamina);
    write_u8(p + 78, e.kind);
    write_u8(p + 79, e.code);
}

// ---------------------------------------------------------------- v1 배치
//
// v1 은 동결이다. 확장 슬롯이 없고 크기가 다르다 — 프레임의 Ver 가 1 이면 이쪽이다.

constexpr std::size_t kCommandV1Size = 56;
constexpr std::size_t kEventV1Size = 64;

// ---------------------------------------------------------------- 핸드셰이크
//
// pad_* 는 C# 구조체의 정렬 구멍이다. 원시 복사라 소켓에 그대로 나가므로
// 읽는 쪽은 건너뛰고 쓰는 쪽은 0 으로 채운다.

constexpr std::size_t kHashSize = 32;      // uint64 4개
constexpr std::size_t kNonceSize = 16;     // uint64 2개

constexpr std::size_t kHelloSize = 88;         // v1
constexpr std::size_t kHelloAckSize = 88;      // v1. 오프셋 12 에 4B, 82 에 6B 패딩
constexpr std::size_t kHelloV2Size = 216;      // 오프셋 12·36·52·68 에 각 4B 패딩
constexpr std::size_t kHelloAckV2Size = 160;   // 오프셋 155 에 5B 패딩
constexpr std::size_t kHeartbeatSize = 16;
constexpr std::size_t kByeSize = 1;

struct Hash256 {
    std::uint64_t a, b, c, d;
};

inline void decode_hash(const std::uint8_t* p, Hash256& h) {
    h.a = read_u64(p);
    h.b = read_u64(p + 8);
    h.c = read_u64(p + 16);
    h.d = read_u64(p + 24);
}

inline void encode_hash(std::uint8_t* p, const Hash256& h) {
    write_u64(p, h.a);
    write_u64(p + 8, h.b);
    write_u64(p + 16, h.c);
    write_u64(p + 24, h.d);
}

struct HelloV2 {
    std::int32_t protocol_version;
    std::int32_t min_protocol_version;
    std::uint16_t contract_major;
    std::uint16_t contract_minor;
    // 오프셋 12: 패딩 4B
    std::uint64_t features;
    std::int32_t tick_rate;
    std::int32_t time_scale;
    std::int32_t npc_count;
    // 오프셋 36: 패딩 4B
    std::int64_t start_tick;
    std::uint16_t start_game_minute_of_day;
    std::uint16_t shard_id;
    // 오프셋 52: 패딩 4B
    std::uint64_t zone_mask;
    std::uint32_t session_epoch;
    // 오프셋 68: 패딩 4B
    Hash256 master_data_structural;
    Hash256 master_data_content;
    Hash256 roster;
    std::uint64_t nonce_a, nonce_b;
    Hash256 auth;
};

inline void decode(const std::uint8_t* p, HelloV2& h) {
    h.protocol_version = read_i32(p);
    h.min_protocol_version = read_i32(p + 4);
    h.contract_major = read_u16(p + 8);
    h.contract_minor = read_u16(p + 10);
    h.features = read_u64(p + 16);
    h.tick_rate = read_i32(p + 24);
    h.time_scale = read_i32(p + 28);
    h.npc_count = read_i32(p + 32);
    h.start_tick = read_i64(p + 40);
    h.start_game_minute_of_day = read_u16(p + 48);
    h.shard_id = read_u16(p + 50);
    h.zone_mask = read_u64(p + 56);
    h.session_epoch = read_u32(p + 64);
    decode_hash(p + 72, h.master_data_structural);
    decode_hash(p + 104, h.master_data_content);
    decode_hash(p + 136, h.roster);
    h.nonce_a = read_u64(p + 168);
    h.nonce_b = read_u64(p + 176);
    decode_hash(p + 184, h.auth);
}

inline void encode(std::uint8_t* p, const HelloV2& h) {
    std::memset(p, 0, kHelloV2Size);          // 패딩을 0 으로. 이 한 줄이 규약이다
    write_i32(p, h.protocol_version);
    write_i32(p + 4, h.min_protocol_version);
    write_u16(p + 8, h.contract_major);
    write_u16(p + 10, h.contract_minor);
    write_u64(p + 16, h.features);
    write_i32(p + 24, h.tick_rate);
    write_i32(p + 28, h.time_scale);
    write_i32(p + 32, h.npc_count);
    write_i64(p + 40, h.start_tick);
    write_u16(p + 48, h.start_game_minute_of_day);
    write_u16(p + 50, h.shard_id);
    write_u64(p + 56, h.zone_mask);
    write_u32(p + 64, h.session_epoch);
    encode_hash(p + 72, h.master_data_structural);
    encode_hash(p + 104, h.master_data_content);
    encode_hash(p + 136, h.roster);
    write_u64(p + 168, h.nonce_a);
    write_u64(p + 176, h.nonce_b);
    encode_hash(p + 184, h.auth);
}

struct HelloAckV2 {
    std::int32_t protocol_version;
    std::uint16_t contract_major;
    std::uint16_t contract_minor;
    std::uint64_t features;
    std::int32_t time_scale;
    std::int32_t npc_count;
    Hash256 master_data_structural;
    Hash256 master_data_content;
    Hash256 roster;
    Hash256 auth;
    std::uint8_t accepted;
    std::uint8_t reject_code;
    std::uint8_t content_hash_warning;
    // 오프셋 155: 패딩 5B
};

inline void decode(const std::uint8_t* p, HelloAckV2& a) {
    a.protocol_version = read_i32(p);
    a.contract_major = read_u16(p + 4);
    a.contract_minor = read_u16(p + 6);
    a.features = read_u64(p + 8);
    a.time_scale = read_i32(p + 16);
    a.npc_count = read_i32(p + 20);
    decode_hash(p + 24, a.master_data_structural);
    decode_hash(p + 56, a.master_data_content);
    decode_hash(p + 88, a.roster);
    decode_hash(p + 120, a.auth);
    a.accepted = read_u8(p + 152);
    a.reject_code = read_u8(p + 153);
    a.content_hash_warning = read_u8(p + 154);
}

inline void encode(std::uint8_t* p, const HelloAckV2& a) {
    std::memset(p, 0, kHelloAckV2Size);
    write_i32(p, a.protocol_version);
    write_u16(p + 4, a.contract_major);
    write_u16(p + 6, a.contract_minor);
    write_u64(p + 8, a.features);
    write_i32(p + 16, a.time_scale);
    write_i32(p + 20, a.npc_count);
    encode_hash(p + 24, a.master_data_structural);
    encode_hash(p + 56, a.master_data_content);
    encode_hash(p + 88, a.roster);
    encode_hash(p + 120, a.auth);
    write_u8(p + 152, a.accepted);
    write_u8(p + 153, a.reject_code);
    write_u8(p + 154, a.content_hash_warning);
}

struct Heartbeat {
    std::int64_t tick;
    std::int64_t sequence;
};

inline void decode(const std::uint8_t* p, Heartbeat& h) {
    h.tick = read_i64(p);
    h.sequence = read_i64(p + 8);
}

inline void encode(std::uint8_t* p, const Heartbeat& h) {
    write_i64(p, h.tick);
    write_i64(p + 8, h.sequence);
}

// ---------------------------------------------------------------- 배치
//
// int32 접두 + 고정 크기 원소. 접두가 음수면 null 배열이라 원소가 없다 —
// 빈 배치(0)와 구분하되 둘 다 "할 일 없음" 이다.

inline bool batch_count(const std::uint8_t* payload, std::size_t size, std::int32_t& out) {
    if (size < 4) return false;
    out = read_i32(payload);
    return true;
}

// 명령 배치를 읽는다. 길이가 안 맞으면 false — 잘라 읽지 않는다.
// 잘라 읽으면 그 프레임부터 스트림 전체가 쓰레기가 된다.
inline bool read_command_batch(const std::uint8_t* payload, std::size_t size,
                               CommandV2* out, std::size_t capacity, std::size_t& count) {
    count = 0;

    std::int32_t declared = 0;
    if (!batch_count(payload, size, declared)) return false;
    if (declared <= 0) return declared == 0;

    const std::size_t n = static_cast<std::size_t>(declared);
    if (n > capacity) return false;
    if (size < 4 + n * kCommandV2Size) return false;

    for (std::size_t i = 0; i < n; ++i) {
        decode(payload + 4 + i * kCommandV2Size, out[i]);
    }

    count = n;
    return true;
}

inline bool read_event_batch(const std::uint8_t* payload, std::size_t size,
                             EventV2* out, std::size_t capacity, std::size_t& count) {
    count = 0;

    std::int32_t declared = 0;
    if (!batch_count(payload, size, declared)) return false;
    if (declared <= 0) return declared == 0;

    const std::size_t n = static_cast<std::size_t>(declared);
    if (n > capacity) return false;
    if (size < 4 + n * kEventV2Size) return false;

    for (std::size_t i = 0; i < n; ++i) {
        decode(payload + 4 + i * kEventV2Size, out[i]);
    }

    count = n;
    return true;
}

// 이벤트 배치를 쓴다. 반환은 쓴 바이트 수.
inline std::size_t write_event_batch(std::uint8_t* payload, const EventV2* items,
                                     std::size_t count) {
    write_i32(payload, static_cast<std::int32_t>(count));

    for (std::size_t i = 0; i < count; ++i) {
        encode(payload + 4 + i * kEventV2Size, items[i]);
    }

    return 4 + count * kEventV2Size;
}

// ---------------------------------------------------------------- 크기 확인
//
// 이 헤더는 구조체를 소켓에 그대로 겹치지 않으므로 sizeof 가 맞을 필요는 없다.
// 대신 상수가 표와 같은지 못 박는다 — 이 값이 틀리면 오프셋 계산이 전부 어긋난다.

static_assert(kCommandV2Size == 72, "WireCommandV2 는 72 B 다");
static_assert(kEventV2Size == 80, "WireEventV2 는 80 B 다");
static_assert(kCommandV1Size == 56, "WireCommand(v1) 는 56 B 다");
static_assert(kEventV1Size == 64, "WireEvent(v1) 는 64 B 다");
static_assert(kHelloSize == 88, "WireHello 는 88 B 다");
static_assert(kHelloAckSize == 88, "WireHelloAck 는 88 B 다");
static_assert(kHelloV2Size == 216, "WireHelloV2 는 216 B 다");
static_assert(kHelloAckV2Size == 160, "WireHelloAckV2 는 160 B 다");
static_assert(kHeartbeatSize == 16, "WireHeartbeat 는 16 B 다");
static_assert(kByeSize == 1, "WireBye 는 1 B 다");
static_assert(kHeaderSize == 8, "프레임 헤더는 8 B 다");
static_assert(sizeof(float) == 4, "float 가 IEEE 754 binary32 여야 한다");

}  // namespace npc_wire

#endif  // NPC_WIRE_H
