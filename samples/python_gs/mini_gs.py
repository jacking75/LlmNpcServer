#!/usr/bin/env python3
"""번들 하나로 NPC 서버에 붙는 표준 라이브러리 게임서버 예제.

프로덕션용 월드가 아니다. 인증·경로 탐색·전투 판정은 README 에서 설명한다.
"""

from __future__ import annotations

import argparse
import heapq
import json
import math
import select
import socket
import struct
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "docs" / "wire" / "reference"))
import npc_wire as wire  # noqa: E402


EVENT = {
    "TickSync": 1, "GameTimeChanged": 2, "NpcSpawned": 3, "NpcDespawned": 4,
    "NpcArrived": 6, "NpcActionCompleted": 7, "NpcInventoryChanged": 10,
    "ZoneStateChanged": 16, "WeatherChanged": 17,
}
TICK_SECONDS = 0.1
FEATURES = (1 << 1) | (1 << 2) | (1 << 5)  # GlobalIds · ExtSlots · SessionEpoch


def hash_words(hex_hash: str) -> list[int]:
    """WireHash.FromHex 와 같은 16자리씩 네 단어 분할."""
    value = hex_hash.removeprefix("sha256:")
    if len(value) != 64:
        raise ValueError("번들 해시가 SHA-256 64자 hex 가 아니다")
    return [int(value[i:i + 16], 16) for i in range(0, 64, 16)]


def recv_frame(sock: socket.socket, buffer: bytearray, deadline: float):
    """TCP 수신 조각을 합쳐 정확히 한 프레임을 돌려준다."""
    while True:
        frame = wire.read_frame(buffer)
        if frame is not None:
            kind, version, payload, rest = frame
            buffer[:] = rest
            return kind, version, payload
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            raise TimeoutError("HelloAck 3초 제한을 넘었다")
        ready, _, _ = select.select([sock], [], [], remaining)
        if ready:
            part = sock.recv(65536)
            if not part:
                raise ConnectionError("상대가 연결을 닫았다")
            buffer.extend(part)


def send_events(sock: socket.socket, events: list[dict], sequence: int) -> int:
    """시퀀스를 한 번만 부여하고 프레임당 256건 이하로 보낸다."""
    for start in range(0, len(events), 256):
        batch = []
        for event in events[start:start + 256]:
            sequence += 1
            fields = {name: 0 for name in wire.EVENT_V2_FIELDS}
            fields.update(event)
            fields["Sequence"] = sequence
            batch.append(fields)
        payload = wire.write_batch(batch, wire.EVENT_V2_FORMAT, wire.EVENT_V2_FIELDS)
        sock.sendall(wire.write_frame(wire.KIND_EVENT_BATCH, 2, payload))
    return sequence


class MiniWorld:
    def __init__(self, bundle: dict, time_scale: int):
        self.bundle = bundle
        self.time_scale = time_scale
        self.tick = 0
        self.sequence = 0
        self.pending = []
        self.order = 0
        self.npcs = {npc["npc_id"]: npc for npc in bundle["roster"]["npcs"]}
        self.pois = {poi["code"]: poi for poi in bundle["pois"]}
        self.position = {npc_id: dict(npc["spawn"]) for npc_id, npc in self.npcs.items()}
        self.recipe = {recipe["item"]: recipe for recipe in bundle["recipes"]}
        self.time_ranges = list(bundle["time"]["time_of_day_hours"].items())
        self.old_period = self.period()

    def minute_of_day(self) -> int:
        seconds = self.bundle["time"]["start_game_hour"] * 3600
        seconds += self.tick * self.time_scale // self.bundle["tick_rate_hz"]
        return (seconds // 60) % 1440

    def period(self) -> int:
        hour = self.minute_of_day() // 60
        for index, (_, bounds) in enumerate(self.time_ranges):
            start, end = bounds
            if start <= hour < end or (start > end and (hour >= start or hour < end)):
                return index
        raise ValueError(f"시간대 범위에 {hour}시가 없다")

    def event(self, kind: str, npc: int = 0, corr: int = 0, **fields) -> dict:
        return {"Kind": EVENT[kind], "OccurredAt": self.tick, "Npc": npc,
                "Correlation": corr, **fields}

    def resync(self) -> list[dict]:
        events = []
        for npc in self.bundle["roster"]["npcs"]:
            pos = self.position[npc["npc_id"]]
            events.append(self.event(
                "NpcSpawned", npc["npc_id"], Poi=npc["home_poi"],
                Zone=npc["zone"], Instance=npc["instance"],
                PosX=pos["x"], PosY=pos["y"], PosZ=pos["z"]))
        for zone in self.bundle["zones"]:
            events.append(self.event("ZoneStateChanged", Zone=zone["code"],
                                     Code=zone["default_region_state"]))
            events.append(self.event("WeatherChanged", Zone=zone["code"],
                                     Code=zone["default_climate"]))
        return events

    def schedule(self, due: int, events: list[dict]) -> None:
        self.order += 1
        heapq.heappush(self.pending, (due, self.order, events))

    def handle(self, command: dict) -> None:
        kind, npc, corr = command["Kind"], command["Npc"], command["Correlation"]
        if npc not in self.npcs:
            return
        if kind == 1:  # Spawn
            self.schedule(self.tick + 1, [self.event("NpcSpawned", npc, corr,
                Poi=self.npcs[npc]["home_poi"], Zone=self.npcs[npc]["zone"],
                Instance=self.npcs[npc]["instance"], **self._pos_fields(npc))])
        elif kind == 2:  # Despawn
            self.schedule(self.tick + 1, [self.event("NpcDespawned", npc, corr)])
        elif kind == 3:  # MoveTo: POI 는 시간 경과 뒤 도착한다.
            poi_code = command["TargetPoi"]
            if poi_code in self.pois:
                target = self.pois[poi_code]
                pos = self.position[npc]
                distance = math.dist((pos["x"], pos["y"], pos["z"]),
                                     tuple(target["pos"][axis] for axis in ("x", "y", "z")))
                seconds_per_meter = 0.36 if command["Flags"] == 1 else 0.6
                ticks = max(1, round(distance * seconds_per_meter * 10 / self.time_scale))
                self.position[npc] = dict(target["pos"])
                self.schedule(self.tick + ticks, [self.event(
                    "NpcArrived", npc, corr, Poi=poi_code, Zone=target["zone"],
                    **self._pos_fields(npc))])
            else:
                self.schedule(self.tick + 1, [self.event("NpcActionCompleted", npc, corr)])
        elif kind == 8:  # Interact: 진행 이벤트 뒤 완료 이벤트를 보낸다.
            item, count = command["Item"], max(1, command["Amount"])
            seconds = self.recipe.get(item, {}).get("duration_s", 300) * count
            ticks = max(1, seconds * 10 // self.time_scale)
            events = []
            if item:
                events.append(self.event("NpcInventoryChanged", npc, corr,
                                         Item=item, Amount=count))
            events.append(self.event("NpcActionCompleted", npc, corr,
                                     Item=item, Amount=command["Amount"]))
            self.schedule(self.tick + ticks, events)
        elif kind == 10:  # InventoryChange 의 성공은 Completed 가 아니다.
            self.schedule(self.tick + 1, [self.event("NpcInventoryChanged", npc, corr,
                Item=command["Item"], Amount=command["Amount"])])
        else:  # Stop · FaceTo · PlayAnimation · SetVisualState · Speak · Combat · Aggro
            self.schedule(self.tick + 1, [self.event("NpcActionCompleted", npc, corr)])

    def _pos_fields(self, npc: int) -> dict:
        pos = self.position[npc]
        return {"PosX": pos["x"], "PosY": pos["y"], "PosZ": pos["z"]}

    def step(self) -> list[dict]:
        self.tick += 1
        events = [self.event("TickSync")]
        period = self.period()
        if period != self.old_period:
            events.append(self.event("GameTimeChanged", Code=period))
            self.old_period = period
        while self.pending and self.pending[0][0] <= self.tick:
            _, _, due_events = heapq.heappop(self.pending)
            for event in due_events:
                event["OccurredAt"] = self.tick
            events.extend(due_events)
        return events


def run_session(sock: socket.socket, world: MiniWorld) -> None:
    bundle = world.bundle
    hashes = bundle["hashes"]
    hello = struct.pack(
        wire.HELLO_V2_FORMAT,
        2, 1, bundle["contract"]["major"], bundle["contract"]["minor"],
        FEATURES, bundle["tick_rate_hz"], world.time_scale,
        bundle["roster"]["npc_count"], world.tick, world.minute_of_day(),
        bundle["roster"]["shard"], bundle["roster"]["zone_mask"], 1,
        *hash_words(hashes["structural"]), *hash_words(hashes["content"]),
        *hash_words(hashes["roster"]), *([0] * 6))
    sock.sendall(wire.write_frame(wire.KIND_HELLO, 2, hello))
    buffer = bytearray()
    kind, version, payload = recv_frame(sock, buffer, time.monotonic() + 3)
    if kind != wire.KIND_HELLO_ACK or version != 2:
        raise ValueError(f"HelloAck v2 대신 kind={kind}, version={version} 이 왔다")
    ack = struct.unpack(wire.HELLO_ACK_V2_FORMAT, payload)
    if ack[-3] != 1:
        raise ValueError(f"Hello 거절: reject_code={ack[-2]}")
    print(f"NPC 서버 연결됨: {len(world.npcs)}명, tick={world.tick}", flush=True)
    # 이전 클라이언트의 미완료 명령에 응답하면 새 클라이언트에는 모르는 상관 ID 가 된다.
    # 이 최소 예제는 영속 명령 상태가 없으므로 새 세션에서 예약 응답을 버린다.
    world.pending.clear()
    world.sequence = send_events(sock, world.resync(), world.sequence)

    next_tick = time.monotonic() + TICK_SECONDS
    while True:
        wait = max(0.0, next_tick - time.monotonic())
        ready, _, _ = select.select([sock], [], [], wait)
        if ready:
            part = sock.recv(65536)
            if not part:
                return
            buffer.extend(part)
            while (frame := wire.read_frame(buffer)) is not None:
                kind, version, payload, rest = frame
                buffer[:] = rest
                if kind == wire.KIND_COMMAND_BATCH:
                    for command in wire.read_command_batch(payload, version) or []:
                        world.handle(command)
                elif kind == wire.KIND_BYE:
                    return
        if time.monotonic() >= next_tick:
            world.sequence = send_events(sock, world.step(), world.sequence)
            if world.tick % 10 == 0:
                sock.sendall(wire.write_frame(wire.KIND_HEARTBEAT, 2,
                    struct.pack(wire.HEARTBEAT_FORMAT, world.tick, world.sequence)))
            next_tick += TICK_SECONDS


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--bundle", required=True, type=Path)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=7010)
    parser.add_argument("--time-scale", type=int, default=60)
    args = parser.parse_args()
    bundle = json.loads(args.bundle.read_text(encoding="utf-8"))
    if bundle["bundle_version"] != 1 or args.time_scale < 1:
        parser.error("bundle_version=1 과 양수 --time-scale 이 필요하다")
    world = MiniWorld(bundle, args.time_scale)
    with socket.create_server((args.host, args.port), reuse_port=False) as listener:
        print(f"{args.host}:{args.port} 에서 NPC 서버를 기다린다", flush=True)
        while True:
            conn, _ = listener.accept()
            with conn:
                try:
                    run_session(conn, world)
                except (ConnectionError, OSError, TimeoutError, ValueError) as error:
                    print(f"연결 종료: {error}", file=sys.stderr, flush=True)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
