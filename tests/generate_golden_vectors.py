"""Generate cross-language golden vectors for the command-envelope digest (T08).

Canonical rule, taken verbatim from starter/contract_lab/bridge_contract.py:297:
    json.dumps(command, sort_keys=True, ensure_ascii=False,
               separators=(",", ":"), allow_nan=False).encode("utf-8")
    -> sha256 hex digest.

The C# implementation (Bridge.Contracts) must reproduce these exact bytes:
sorted keys, no spaces, raw UTF-8 (no \\u escapes for Chinese/emoji),
integers plain. Any divergence fails the golden test on both sides.
Run: python3 generate_golden_vectors.py  (writes golden-vectors.json)
"""
import hashlib
import json

VECTORS = [
    {
        "id": "click-basic",
        "command": {
            "schema_version": "1.0",
            "command_id": "11d497af-51d1-48ec-a5ab-c878e3cd6b4e",
            "device_id": "test-device",
            "lease_id": "523fe7e5-6ba0-4d6b-a662-e2cebe25f687",
            "epoch": 3,
            "sequence": 1,
            "operation": "input.click",
            "observation_id": "81c2158c-0af9-4e4d-aa04-f7a742989f59",
            "issued_at": "2026-11-13T12:00:00Z",
            "expires_at": "2026-11-13T12:00:15Z",
            "payload": {"x": 320, "y": 180},
        },
    },
    {
        "id": "type-text-zh-emoji",
        "command": {
            "schema_version": "1.0",
            "command_id": "22d497af-51d1-48ec-a5ab-c878e3cd6b4e",
            "device_id": "test-device",
            "lease_id": "523fe7e5-6ba0-4d6b-a662-e2cebe25f687",
            "epoch": 3,
            "sequence": 2,
            "operation": "input.type_text",
            "observation_id": "81c2158c-0af9-4e4d-aa04-f7a742989f59",
            "issued_at": "2026-11-13T12:00:00Z",
            "expires_at": "2026-11-13T12:00:15Z",
            "payload": {"text": "你好，Windows 👋\n换行"},
        },
    },
    {
        "id": "key-ctrl-c",
        "command": {
            "schema_version": "1.0",
            "command_id": "33d497af-51d1-48ec-a5ab-c878e3cd6b4e",
            "device_id": "test-device",
            "lease_id": "523fe7e5-6ba0-4d6b-a662-e2cebe25f687",
            "epoch": 3,
            "sequence": 3,
            "operation": "input.key",
            "observation_id": "81c2158c-0af9-4e4d-aa04-f7a742989f59",
            "issued_at": "2026-11-13T12:00:00Z",
            "expires_at": "2026-11-13T12:00:15Z",
            "payload": {"keys": ["CTRL", "C"]},
        },
    },
]

if __name__ == "__main__":
    out = []
    for v in VECTORS:
        canonical = json.dumps(
            v["command"], sort_keys=True, ensure_ascii=False,
            separators=(",", ":"), allow_nan=False).encode("utf-8")
        out.append({
            "id": v["id"],
            "command": v["command"],
            "canonical_json": canonical.decode("utf-8"),
            "canonical_sha256": hashlib.sha256(canonical).hexdigest(),
        })
    with open("golden-vectors.json", "w", encoding="utf-8") as f:
        json.dump({"canonical_rule": "json.dumps(command, sort_keys=True, ensure_ascii=False, separators=(',', ':'), allow_nan=False) -> utf-8 -> sha256 hex",
                   "vectors": out}, f, ensure_ascii=False, indent=2)
        f.write("\n")
    for v in out:
        print(v["id"], v["canonical_sha256"])
