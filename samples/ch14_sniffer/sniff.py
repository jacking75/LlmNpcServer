#!/usr/bin/env python3
"""
14장 — 와이어 프레임 스니퍼.

NPC 서버와 게임서버 사이에 끼어 앉아 오가는 프레임을 세고 찍는다.
읽기만 하고 아무것도 고치지 않는 <b>투명 중계</b>다.

    [NPC 서버] --> :7013 (이 스크립트) --> :7010 [게임서버]

C# 을 한 줄도 안 쓴다. 프레임 헤더 8바이트만 알면 되기 때문이고,
그 8바이트가 이 프로토콜에서 <b>언어에 안 묶인 유일한 부분</b>이다.

    0      1      2      3      4      5      6      7      8 ...
    +------+------+------+------+------+------+------+------+---------+
    |      PayloadLength (u32)   | Kind | Ver  |  Reserved   | Payload |
    +------+------+------+------+------+------+------+------+---------+
      little-endian               u8     u8      u16 = 0     MemoryPack

사용법
    python samples/ch14_sniffer/sniff.py --listen 7013 --forward 7010

    # NPC 서버는 스니퍼로 보낸다
    dotnet run -c Release --project src/Npc.Host -- --link tcp --gs-port 7013 ...
"""

import argparse
import socket
import struct
import threading
import time
from collections import Counter

HEADER = 8
KINDS = {
    0: "None",
    1: "Hello",
    2: "HelloAck",
    3: "CommandBatch",
    4: "EventBatch",
    5: "Heartbeat",
    6: "Bye",
}

stats = Counter()
bytes_by_kind = Counter()
lock = threading.Lock()
start = time.time()


def pump(src, dst, label, verbose):
    """한 방향을 중계하면서 프레임 경계를 센다."""
    buf = bytearray()

    try:
        while True:
            chunk = src.recv(65536)
            if not chunk:
                break

            dst.sendall(chunk)          # 그대로 흘려보낸다 — 투명 중계
            buf += chunk

            # 버퍼 앞에서 완전한 프레임을 떼어낸다. TCP 는 경계를 안 지켜 주므로
            # 이 재조립이 정상 경로다 (C# 쪽 FrameCodec.TryReadFrame 과 같은 논리).
            while len(buf) >= HEADER:
                length, kind, version, reserved = struct.unpack_from("<IBBH", buf, 0)

                # 받아 줄 범위다. v2 는 확장 슬롯이 붙은 배치이고(B-02), 원소가 커진다 —
                # WireCommand 56B → 72B · WireEvent 64B → 80B. 여기서는 프레임만 세므로
                # 원소 크기를 몰라도 되지만, 버전은 찍어 둔다.
                if not (1 <= version <= 2):
                    print(f"[{label}] 와이어 버전이 범위를 벗어난다: {version}")
                    return
                if length > (1 << 20):
                    print(f"[{label}] 페이로드 상한 초과: {length}B — 경계가 밀렸다")
                    return
                if len(buf) < HEADER + length:
                    break               # 아직 다 안 왔다

                name = KINDS.get(kind, f"?{kind}")

                with lock:
                    stats[(label, name)] += 1
                    bytes_by_kind[(label, name)] += HEADER + length

                if verbose and name not in ("Heartbeat",):
                    print(f"[{label}] v{version} {name:<13} payload {length:6}B")

                del buf[: HEADER + length]
    except OSError:
        pass
    finally:
        try:
            dst.shutdown(socket.SHUT_WR)
        except OSError:
            pass


def report():
    elapsed = time.time() - start
    print()
    print(f"  프레임 집계  ({elapsed:.1f}초)")
    print(f"  {'방향':<8} {'종류':<14} {'프레임':>8} {'바이트':>12} {'평균':>8}")
    print("  " + "-" * 54)

    with lock:
        for (label, name), count in sorted(stats.items(), key=lambda kv: -kv[1]):
            total = bytes_by_kind[(label, name)]
            print(f"  {label:<8} {name:<14} {count:>8} {total:>12,} {total // count:>8}")

    print()
    print("  CommandBatch 한 프레임 = NPC 서버의 FlushAsync 한 번이다 (N8).")
    print("  EventBatch 안에는 이벤트가 여러 개 들어 있다 — 프레임 수와 이벤트 수는 다르다.")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--listen", type=int, default=7013, help="NPC 서버가 붙을 포트")
    ap.add_argument("--forward-host", default="127.0.0.1")
    ap.add_argument("--forward", type=int, default=7010, help="진짜 게임서버 포트")
    ap.add_argument("--verbose", action="store_true", help="프레임마다 한 줄씩 찍는다")
    ap.add_argument("--seconds", type=int, default=0, help="이 초 뒤에 집계를 찍고 끝낸다")
    args = ap.parse_args()

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", args.listen))
    server.listen(1)

    print(f"sniffer  :{args.listen}  ->  {args.forward_host}:{args.forward}")
    print("NPC 서버를 --gs-port {} 로 띄운다.".format(args.listen))
    print()

    if args.seconds:
        threading.Timer(args.seconds, lambda: (report(), __import__("os")._exit(0))).start()

    try:
        while True:
            client, addr = server.accept()
            print(f"[link] NPC 서버 접속: {addr}")

            upstream = socket.create_connection((args.forward_host, args.forward))
            client.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
            upstream.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)

            threading.Thread(target=pump, args=(client, upstream, "npc→gs", args.verbose), daemon=True).start()
            threading.Thread(target=pump, args=(upstream, client, "gs→npc", args.verbose), daemon=True).start()
    except KeyboardInterrupt:
        report()


if __name__ == "__main__":
    main()
