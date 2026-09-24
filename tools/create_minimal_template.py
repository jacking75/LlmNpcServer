"""Rebuild the checked-in small-world example from current master data.

This is an authoring helper, not part of the server runtime.
"""

import copy
import json
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "masterdata"
TARGET = ROOT / "samples" / "worlds" / "minimal"
TARGET.mkdir(parents=True, exist_ok=True)


def read(name):
    return json.loads((SOURCE / f"{name}.json").read_text(encoding="utf-8"))


def write(name, data):
    data.pop("_comment", None)
    (TARGET / f"{name}.json").write_text(
        json.dumps(data, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


for name in ("world_flags", "dialogue_lines", "factions"):
    write(name, read(name))

actions = read("actions")
selected_actions = {"MoveTo", "Gather", "Sleep", "Wait", "Farm", "Observe", "Gossip", "Wander", "Flee", "Attack", "Trade", "Emote", "Rest"}
actions["actions"] = [a for a in actions["actions"] if a["id"] in selected_actions]
for code, action in enumerate(actions["actions"], 1):
    action["code"] = code
write("actions", actions)

zones = read("zones")
zones["zones"] = [copy.deepcopy(z) for z in zones["zones"] if z["id"] in ("town_center", "farmlands")]
for code, zone in enumerate(zones["zones"], 1):
    zone["code"] = code
    zone["adjacent"] = [z["id"] for z in zones["zones"] if z["id"] != zone["id"]]
    zone["capacity"] = 60
write("zones", zones)

all_pois = read("pois")
pois = all_pois.copy()
picked = [
    *[p for p in all_pois["pois"] if p["type"] == "home" and p["zone"] == "town_center"][:4],
    next(p for p in all_pois["pois"] if p["subtype"] == "farm"),
    next(p for p in all_pois["pois"] if p["subtype"] == "guardhouse"),
    next(p for p in all_pois["pois"] if p["subtype"] == "bakery"),
    *[next(p for p in all_pois["pois"] if p["type"] == kind) for kind in ("market", "tavern", "temple", "gate")],
    next(p for p in all_pois["pois"] if p["type"] == "field" and p["subtype"] != "farm"),
]
pois["pois"] = [copy.deepcopy(p) for p in picked]
for code, poi in enumerate(pois["pois"], 1):
    poi["code"] = code
    poi["zone"] = "farmlands" if poi["type"] == "field" else "town_center"
    poi["allowed_archetypes"] = []
    poi["resources"] = [r for r in poi["resources"] if r in ("wheat", "timber")]
write("pois", pois)

archetypes = read("archetypes")
archetypes["archetypes"] = [copy.deepcopy(a) for a in archetypes["archetypes"] if a["id"] in ("villager", "farmer", "town_guard")]
for code, archetype in enumerate(archetypes["archetypes"]):
    archetype["code"] = code
    archetype["population_weight"] = (0.3, 0.2, 0.5)[code]
    archetype["allowed_actions"] = [a for a in archetype["allowed_actions"] if a in selected_actions]
write("archetypes", archetypes)

buckets = read("context_buckets")
buckets["total_keys"] = 3 * 72
write("context_buckets", buckets)

items = read("items")
wanted_items = ("wheat", "flour", "bread", "hoe", "guard_spear", "water", "coin", "timber")
items["items"] = [copy.deepcopy(i) for i in items["items"] if i["id"] in wanted_items]
for code, item in enumerate(items["items"], 1):
    item["code"] = code
items["recipes"] = [r for r in items["recipes"] if r["id"] in ("flour", "bread")]
write("items", items)

fallback = read("fallback_plans")
fallback["plans"] = [f for f in fallback["plans"] if f["archetype"] in ("villager", "farmer", "town_guard")]
for plan in fallback["plans"]:
    for step in plan["steps"]:
        for key, value in step["args"].items():
            if value == "nearest:merchant":
                step["args"][key] = "nearest:farmer"
            elif value == "$patrol_route":
                step["args"][key] = "$gate"
write("fallback_plans", fallback)

interrupts = read("interrupts")
interrupts["rules"] = interrupts["rules"][:4]
write("interrupts", interrupts)

overrides = read("npc_overrides")
overrides["overrides"] = []
write("npc_overrides", overrides)

for locale in ("ko-KR", "en-US"):
    target = TARGET / "localization"
    target.mkdir(exist_ok=True)
    keys = {f"npc.{a['id']}" for a in archetypes["archetypes"]}
    keys |= {f"poi.{p['subtype']}" for p in pois["pois"]}
    keys |= {f"item.{i['id']}" for i in items["items"]}
    keys |= {f"zone.{z['id']}" for z in zones["zones"]}
    keys |= {f"dialogue.{d['id']}" for d in read("dialogue_lines")["lines"]}
    original = json.loads((SOURCE / "localization" / f"{locale}.json").read_text(encoding="utf-8"))
    translated = {key: original[key] for key in sorted(keys) if key in original}
    (target / f"{locale}.json").write_text(json.dumps(translated, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

shutil.copytree(SOURCE / "prompt", TARGET / "prompt", dirs_exist_ok=True)

print(f"Created {TARGET}")
