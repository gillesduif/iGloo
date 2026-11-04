#!/usr/bin/env python3
"""Create only the new disposable Mint v24 GPT identifiers."""

import json
import os
from pathlib import Path
import uuid


LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUTPUT = LAB / "mint-fresh-v24-ids.json"
OLD = {
    "4331a9f3-af31-4ed8-9bff-07c5400a42a4",  # v23 selected disk
    "052eaaab-ae24-495d-8a79-8f567a1969d9",  # v23 Linux ESP
    "9025bbb3-4a9a-452c-b4f6-b4959b51cdca",  # v23 decoy
}


def main() -> None:
    if not LAB.is_dir() or OUTPUT.exists() or OUTPUT.is_symlink():
        raise RuntimeError("Expected an existing Mint lab and a new v24 identity file")
    selected = {str(uuid.uuid4()) for _ in range(3)}
    if len(selected) != 3 or selected & OLD:
        raise RuntimeError("Fresh selected target, ESP, and decoy GUIDs are required")
    target, esp, decoy = sorted(selected)
    identities = {
        "targetDiskGuid": target,
        "linuxEspPartuuid": esp,
        "decoyDiskGuid": decoy,
        # These are read-only properties of the preserved v20 OEMDRV backing.
        "seedDiskGuid": "65de9ca9-a4b6-4a0f-b027-e15dfcd9e570",
        "seedPartuuid": "d80f6663-7280-4d6d-8b18-cad62e3f0c94",
        "targetBytes": 40 * 1024**3,
    }
    descriptor = os.open(OUTPUT, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        os.fchmod(stream.fileno(), 0o600)
        json.dump(identities, stream, indent=2)
        stream.write("\n")
        stream.flush()
        os.fsync(stream.fileno())
    print(f"Fresh Mint v24 target, ESP, and decoy identifiers saved to {OUTPUT}")


if __name__ == "__main__":
    main()
