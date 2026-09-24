"""List baseline buckets reachable from NPC placements and zone defaults."""

import argparse
import json
from pathlib import Path


def read_json(path: Path) -> dict:
    with path.open(encoding="utf-8") as source:
        return json.load(source)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--masterdata", type=Path, default=Path("masterdata"))
    parser.add_argument("--out", type=Path, default=Path("reachable-buckets.txt"))
    args = parser.parse_args()

    zones = {
        zone["id"]: (zone["default_region_state"], zone["default_climate"])
        for zone in read_json(args.masterdata / "zones.json")["zones"]
    }
    npcs = read_json(args.masterdata / "npc_instances.json")["npcs"]
    times = read_json(args.masterdata / "context_buckets.json")["dimensions"]["time_of_day"]["values"]
    combinations = {(npc["archetype"], *zones[npc["zone"]]) for npc in npcs}
    buckets = sorted(
        f"{archetype}@{time}.{region}.{climate}"
        for archetype, region, climate in combinations
        for time in times
    )
    args.out.write_text("\n".join(buckets) + "\n", encoding="utf-8")
    print(f"baseline reachable buckets: {len(buckets)} -> {args.out}")


if __name__ == "__main__":
    main()
