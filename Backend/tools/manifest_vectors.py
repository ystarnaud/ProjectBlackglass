#!/usr/bin/env python3
"""Writes Backend/contracts/manifest-v1 (S9-B4): the manifest format v1 golden vectors.

This is an independent implementation of the canonical form, the retention ranking and receipt v1, written from the README's
rules, not from the C# code. The C# domain tests (GoldenVectorTests) and Stage 11's Unity EditMode tests must reproduce every
expected file byte for byte. Re-running it must leave the files unchanged; a change to a vector is a format change and needs
the owner's approval.

Usage: python3 Backend/tools/manifest_vectors.py
"""
import hashlib
import json
import os

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "contracts", "manifest-v1")
ALPHABET = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz"
CATALOGUE = "sha256:" + "5f2c" * 16
LOOT = "sha256:" + "a1b2" * 16
ATTEMPT = "0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41"
SESSION = "0192f1a5-0000-7000-8000-000000000001"


def b58(n):
    s = ""
    while n:
        n, r = divmod(n, 58)
        s = ALPHABET[r] + s
    return s


def unb58(s):
    n = 0
    for c in s:
        n = n * 58 + ALPHABET.index(c)
    return n


def dumps(value):
    # Ordinal key order, no whitespace, minimal escapes, raw UTF-8 (json.dumps escapes exactly '"', '\\' and U+0000-U+001F).
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def canonical_manifest(m):
    m = json.loads(json.dumps(m))
    m["objectives"].sort(key=lambda o: o["objectiveId"])
    m["operatives"].sort(key=lambda o: o["slot"])
    for o in m["operatives"]:
        o["collected"].sort(key=lambda c: (c["containerIndex"], c["line"]))
        o["equipment"].sort(key=lambda e: e["slot"])
        loot = sorted([c for c in o["consumed"] if c["source"] == "loot"], key=lambda c: (c["containerIndex"], c["line"]))
        owned = sorted([c for c in o["consumed"] if c["source"] == "owned"], key=lambda c: c["definitionId"])
        o["consumed"] = loot + owned
        held_loot = sorted([h for h in o["held"] if h["origin"] == "loot"], key=lambda h: (h["containerIndex"], h["line"]))
        items = sorted([h for h in o["held"] if h["origin"] == "owned" and "itemId" in h], key=lambda h: unb58(h["itemId"]))
        stacks = sorted([h for h in o["held"] if h["origin"] == "owned" and "itemId" not in h], key=lambda h: h["definitionId"])
        o["held"] = held_loot + items + stacks
    return dumps(m)


def manifest(outcome="success", operatives=None, objectives=None, **overrides):
    m = {
        "formatVersion": 1, "attemptId": ATTEMPT, "accountId": b58(1001), "processSessionId": SESSION, "buildRevision": "r1",
        "catalogueVersion": CATALOGUE, "integrity": "ok", "outcome": outcome, "outcomeSequence": 8812, "serverElapsedMs": 1200000,
        "layoutHash": "cf2e14dece9cc2a6", "lootTableVersion": 1, "lootHash": LOOT,
        "objectives": objectives if objectives is not None else [], "operatives": operatives if operatives is not None else [bare(11, 0)],
    }
    m.update(overrides)
    return m


def bare(op, slot, alive=True):
    return {"operativeId": b58(op), "slot": slot, "alive": alive, "consumed": [], "held": [], "collected": [], "equipment": []}


def full():
    first = {
        "operativeId": b58(11), "slot": 0, "alive": True,
        "consumed": [{"definitionId": "item.medkit", "source": "owned", "quantity": 1},
                     {"definitionId": "item.medkit", "source": "loot", "containerIndex": 0, "line": 0, "quantity": 1}],
        "held": [{"origin": "owned", "itemId": b58(102)}, {"origin": "owned", "itemId": b58(101)},
                 {"origin": "owned", "definitionId": "item.medkit", "quantity": 2},
                 {"origin": "loot", "containerIndex": 0, "line": 1, "quantity": 1}, {"origin": "loot", "containerIndex": 0, "line": 0, "quantity": 1}],
        "collected": [{"containerIndex": 0, "line": 1, "quantity": 1}, {"containerIndex": 0, "line": 0, "quantity": 2}],
        "equipment": [{"slot": "weapon", "itemId": b58(101)}, {"slot": "armor", "itemId": b58(102)}],
    }
    second = {
        "operativeId": b58(12), "slot": 1, "alive": False, "consumed": [],
        "held": [{"origin": "owned", "itemId": b58(103)}, {"origin": "loot", "containerIndex": 2, "line": 0, "quantity": 1}],
        "collected": [{"containerIndex": 2, "line": 0, "quantity": 1}],
        "equipment": [{"slot": "weapon", "itemId": b58(103)}, {"slot": "utility", "containerIndex": 2, "line": 0}],
    }
    return manifest("success", [second, first], [{"objectiveId": "intel", "state": "failed"}, {"objectiveId": "extract", "state": "completed"}])


def pretty(m):
    return json.dumps(m, indent=2, ensure_ascii=False).replace("\n", "\r\n\t").encode("utf-8")


def compact(m):
    return json.dumps(m, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def reverse_keys(value):
    if isinstance(value, dict):
        return {k: reverse_keys(value[k]) for k in reversed(list(value.keys()))}
    if isinstance(value, list):
        return [reverse_keys(v) for v in value]
    return value


def reverse_lists(value):
    if isinstance(value, dict):
        return {k: reverse_lists(v) for k, v in value.items()}
    if isinstance(value, list):
        return [reverse_lists(v) for v in reversed(value)]
    return value


def maxed():
    ops = []
    for slot in range(8):
        o = bare(1000 + slot, 7 - slot)
        ops.append(o)
    ops[0]["collected"] = [{"containerIndex": 65535, "line": 65535, "quantity": 2147483647}]
    ops[0]["held"] = [{"origin": "loot", "containerIndex": 65535, "line": 65535, "quantity": 2147483647}]
    ops[1]["held"] = [{"origin": "owned", "itemId": "NQm6nKp8qFC"}]  # long.MaxValue
    return manifest("expired", ops, [{"objectiveId": "a" * 64, "state": "active"}], outcomeSequence=2147483647, serverElapsedMs=86400000,
                    lootTableVersion=2147483647, buildRevision="R" + "x" * 63)


VALID = [
    ("01-minimal", lambda: compact(manifest())),
    ("02-full", lambda: compact(full())),
    ("03-success", lambda: compact(manifest("success", objectives=[{"objectiveId": "extract", "state": "completed"}]))),
    ("04-defeat", lambda: compact(manifest("defeat", objectives=[{"objectiveId": "extract", "state": "failed"}]))),
    ("05-abandoned", lambda: compact(manifest("abandoned"))),
    ("06-disconnected", lambda: compact(manifest("disconnected", [bare(11, 0, alive=False)]))),
    ("07-expired", lambda: compact(manifest("expired"))),
    ("08-escapes", lambda: compact(manifest()).replace(b'"buildRevision":"r1"', b'"buildRevision":"\\u0072\\u0031"')
        .replace(b'"outcome":"success"', b'"outcome":"succ\\u0065ss"')),
    ("09-max-bounds", lambda: compact(maxed())),
    ("10-reverse-lists", lambda: compact(reverse_lists(full()))),
    ("11-whitespace", lambda: pretty(full())),
    ("12-key-order", lambda: compact(reverse_keys(full()))),
    ("13-ledger-mismatch", lambda: compact(manifest("defeat", integrity="ledgerMismatch"))),
]

INVALID = [
    ("01-not-json-trailing-comma", lambda: compact(manifest())[:-1] + b",}", "NotJson"),
    ("02-not-json-leading-zero", lambda: compact(manifest()).replace(b'"outcomeSequence":8812', b'"outcomeSequence":08812'), "NotJson"),
    ("03-too-deep", lambda: compact(manifest()).replace(b'"alive":true', b'"alive":{"a":[{}]}'), "TooDeep"),
    ("04-wrong-type", lambda: compact(manifest()).replace(b'"alive":true', b'"alive":"true"'), "WrongType"),
    ("05-duplicate-key", lambda: compact(manifest()).replace(b'{"formatVersion":1', b'{"formatVersion":1,"formatVersion":1'), "DuplicateKey"),
    ("06-unknown-member", lambda: compact(manifest(seed=5)), "UnknownMember"),
    ("07-missing-member", lambda: compact({k: v for k, v in manifest().items() if k != "lootHash"}), "MissingMember"),
    ("08-unsupported-format", lambda: compact(manifest(formatVersion=2)), "UnsupportedFormat"),
    ("09-float", lambda: compact(manifest()).replace(b'"outcomeSequence":8812', b'"outcomeSequence":8812.5'), "NotInteger"),
    ("10-exponent", lambda: compact(manifest()).replace(b'"outcomeSequence":8812', b'"outcomeSequence":8.812e3'), "NotInteger"),
    ("11-negative-zero", lambda: compact(manifest()).replace(b'"outcomeSequence":8812', b'"outcomeSequence":-0'), "NotInteger"),
    ("12-over-bound", lambda: compact(manifest(serverElapsedMs=86400001)), "OutOfBounds"),
    ("13-uppercase-uuid", lambda: compact(manifest(attemptId=ATTEMPT.upper())), "NonCanonicalId"),
    ("14-non-canonical-base58", lambda: compact(manifest(accountId="1" + b58(1001))), "NonCanonicalId"),
    ("15-non-ascii-objective", lambda: compact(manifest(objectives=[{"objectiveId": "intél", "state": "active"}])), "BadValue"),
    ("16-bad-enum", lambda: compact(manifest("victory")), "BadEnum"),
    ("17-infra-void", lambda: compact(manifest("infra_void")), "BackendOnlyOutcome"),
    ("18-duplicate-entry", lambda: compact(manifest(operatives=[dict(bare(11, 0), held=[{"origin": "loot", "containerIndex": 0, "line": 0, "quantity": 1}] * 2)])), "DuplicateEntry"),
    ("19-uuid-not-v7", lambda: compact(manifest(attemptId="0192f1a4-5c2e-4b1d-9a0e-3f6c1d2b8e41")), "NonCanonicalId"),
    ("20-split-stack", lambda: compact(manifest(operatives=[dict(bare(11, 0), held=[{"origin": "owned", "definitionId": "item.medkit", "quantity": 1}] * 2)])), "DuplicateEntry"),
]


def salted(salt, account, ci, line):
    return hashlib.sha256(salt + account.to_bytes(8, "big", signed=True) + ci.to_bytes(4, "big", signed=True) + line.to_bytes(4, "big", signed=True)).digest()


def rank(v):
    terms = v["terms"]["lootRetention"]
    salt = bytes.fromhex(v["saltHex"])
    account = unb58(v["accountId"])
    eligible = [c for c in v["candidates"] if c["category"] in terms["eligibleCategories"]]
    eligible.sort(key=lambda c: (terms["categoryPriority"].index(c["category"]), salted(salt, account, c["containerIndex"], c["line"]),
                                 c["containerIndex"], c["line"]))
    return [{"containerIndex": c["containerIndex"], "line": c["line"], "definitionId": c["definitionId"],
             "units": min(c["quantity"], terms["maxUnitsPerEntry"])} for c in eligible[:terms["maxEntries"]]]


def terms(max_entries, units, eligible, priority):
    return {"covers": ["defeat", "abandoned", "disconnected", "expired"],
            "consumableRefund": {"percent": 50, "rounding": "floor", "destination": "stash"},
            "lootRetention": {"maxEntries": max_entries, "maxUnitsPerEntry": units, "eligibleCategories": eligible,
                              "rank": ["categoryPriority", "saltedHash"], "categoryPriority": priority},
            "progressXp": {"percent": 40, "basis": "requiredObjectivesCompleted"}}


RETENTION = [
    ("01-priority-then-hash", {
        "saltHex": "".join("%02x" % ((i * 7 + 3) % 256) for i in range(32)), "accountId": b58(1001),
        "terms": terms(2, 3, ["Consumable", "Armor", "Utility", "Weapon"], ["Weapon", "Armor", "Utility", "Consumable"]),
        "candidates": [{"containerIndex": 0, "line": 0, "definitionId": "item.medkit", "category": "Consumable", "quantity": 5},
                       {"containerIndex": 0, "line": 1, "definitionId": "item.service-rifle", "category": "Weapon", "quantity": 1},
                       {"containerIndex": 1, "line": 0, "definitionId": "item.light-vest", "category": "Armor", "quantity": 1},
                       {"containerIndex": 2, "line": 0, "definitionId": "item.combat-blade", "category": "Weapon", "quantity": 1}]}),
    ("02-hash-decides-and-caps-units", {
        "saltHex": "00" * 31 + "01", "accountId": b58(73),
        "terms": terms(3, 2, ["Consumable"], ["Consumable", "Weapon", "Armor", "Utility"]),
        "candidates": [{"containerIndex": c, "line": l, "definitionId": "item.medkit", "category": "Consumable", "quantity": 1 + c + l}
                       for c in range(3) for l in range(2)] + [{"containerIndex": 9, "line": 0, "definitionId": "item.light-vest", "category": "Armor", "quantity": 1}]}),
]


def receipt_bytes(d):
    def ops(items):
        return sorted(items, key=lambda x: (unb58(x["operativeId"]), x.get("definitionId", "")))

    def ids(values):
        return sorted(values, key=unb58)

    ins = d["insurance"]
    if ins["covered"]:
        insurance = {"applied": ins["applied"], "covered": True, "policyId": ins["policyId"], "policyVersion": ins["policyVersion"],
                     "refunds": sorted(ins["refunds"], key=lambda r: r["definitionId"]),
                     "retained": [dict(r, itemIds=ids(r["itemIds"])) for r in sorted(ins["retained"], key=lambda r: r["definitionId"])],
                     "xp": ops(ins["xp"])}
    else:
        insurance = {"covered": False}
    out = {"accountId": d["accountId"], "attemptId": d["attemptId"], "catalogueVersion": d["catalogueVersion"], "consumed": ops(d["consumed"]),
           "energy": d["energy"], "granted": [dict(g, itemIds=ids(g["itemIds"])) for g in ops(d["granted"])], "insurance": insurance,
           "inventoryVersion": d["inventoryVersion"], "operatives": ops(d["operatives"]), "outcome": d["outcome"], "receiptVersion": 1,
           "settledAt": d["settledAt"], "xp": ops(d["xp"])}
    return dumps(out)


RECEIPTS = [
    ("01-success", {
        "attemptId": ATTEMPT, "accountId": b58(1001), "outcome": "success", "settledAt": "2026-10-17T12:30:00.123456Z", "catalogueVersion": CATALOGUE,
        "energy": {"cost": 10, "state": "settled", "refunded": 0},
        "consumed": [{"operativeId": b58(11), "definitionId": "item.medkit", "quantity": 1}],
        "granted": [{"operativeId": b58(12), "definitionId": "item.servo-boots", "quantity": 1, "itemIds": [b58(501)]},
                    {"operativeId": b58(11), "definitionId": "item.service-rifle", "quantity": 2, "itemIds": [b58(503), b58(500)]},
                    {"operativeId": b58(11), "definitionId": "item.medkit", "quantity": 1, "itemIds": []}],
        "xp": [{"operativeId": b58(12), "amount": 150}, {"operativeId": b58(11), "amount": 150}],
        "operatives": [{"operativeId": b58(12), "alive": False}, {"operativeId": b58(11), "alive": True}],
        "insurance": {"covered": True, "policyId": "standard", "policyVersion": 1, "applied": False, "refunds": [], "retained": [], "xp": []},
        "inventoryVersion": 6}),
    ("02-insured-defeat", {
        "attemptId": ATTEMPT, "accountId": b58(1001), "outcome": "defeat", "settledAt": "2026-10-17T12:30:00.000001Z", "catalogueVersion": CATALOGUE,
        "energy": {"cost": 10, "state": "settled", "refunded": 0},
        "consumed": [{"operativeId": b58(11), "definitionId": "item.medkit", "quantity": 3}],
        "granted": [], "xp": [],
        "operatives": [{"operativeId": b58(11), "alive": False}],
        "insurance": {"covered": True, "policyId": "standard", "policyVersion": 1, "applied": True,
                      "refunds": [{"definitionId": "item.medkit", "quantity": 1}],
                      "retained": [{"definitionId": "item.service-rifle", "quantity": 1, "itemIds": [b58(900)]},
                                   {"definitionId": "item.medkit", "quantity": 2, "itemIds": []}],
                      "xp": [{"operativeId": b58(11), "amount": 60}]},
        "inventoryVersion": 9}),
    ("03-void", {
        "attemptId": ATTEMPT, "accountId": b58(1001), "outcome": "infra_void", "settledAt": "2026-10-17T13:00:00.000000Z", "catalogueVersion": CATALOGUE,
        "energy": {"cost": 10, "state": "refunded", "refunded": 10}, "consumed": [], "granted": [], "xp": [],
        "operatives": [{"operativeId": b58(12), "alive": None}, {"operativeId": b58(11), "alive": None}],
        "insurance": {"covered": False}, "inventoryVersion": 7}),
]


def write(path, data):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(data)


def main():
    for name, make in VALID:
        raw = make()
        canonical = canonical_manifest(json.loads(raw))
        write(os.path.join(ROOT, "valid", name + ".input.json"), raw)
        write(os.path.join(ROOT, "valid", name + ".canonical.json"), canonical)
        write(os.path.join(ROOT, "valid", name + ".hash.txt"), ("sha256:" + hashlib.sha256(canonical).hexdigest() + "\n").encode())
    for name, make, code in INVALID:
        write(os.path.join(ROOT, "invalid", name + ".input.json"), make())
        write(os.path.join(ROOT, "invalid", name + ".code.txt"), (code + "\n").encode())
    for name, v in RETENTION:
        v = dict(v, expected=rank(v))
        write(os.path.join(ROOT, "retention", name + ".json"), json.dumps(v, indent=2).encode() + b"\n")
    for name, d in RECEIPTS:
        write(os.path.join(ROOT, "receipt", name + ".input.json"), json.dumps(d, indent=2).encode() + b"\n")
        write(os.path.join(ROOT, "receipt", name + ".receipt.json"), receipt_bytes(d))
    print("wrote", ROOT)


if __name__ == "__main__":
    main()
