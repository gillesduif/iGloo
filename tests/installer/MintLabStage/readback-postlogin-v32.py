#!/usr/bin/env python3
"""Read back Mint v32's completed native Chrome import and protected disks."""

import importlib.util
import json
import os
from pathlib import Path
import subprocess


HERE = Path(__file__).resolve().parent
LAB = Path("/home/gillesduif/igloo-mint-22.3-lab")
OUT = LAB / "mint-postlogin-v32"
DECOY = LAB / "mint-decoy-fresh-v28.raw"
SYNTHETIC = LAB / "mint-postlogin-v31/synthetic-envelope.json"


def load_module(path: Path, name: str):
    spec = importlib.util.spec_from_file_location(name, path)
    if spec is None or spec.loader is None:
        raise RuntimeError(f"Could not load {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def main() -> None:
    if os.geteuid() or not DECOY.is_file() or DECOY.is_symlink():
        raise RuntimeError("Expected root and exact Mint decoy disk")
    if "disk identifier (guid): 7a9c98ef-c49c-42c7-8d41-c6bcb6ca8b03" not in \
            subprocess.check_output(["sgdisk", "-p", str(DECOY)], text=True).lower():
        raise RuntimeError("Mint decoy identity changed")
    subprocess.run(["sgdisk", "-v", str(DECOY)], check=True,
                   stdout=subprocess.DEVNULL)
    runner = (LAB / "run-postlogin-v32.sh").read_text()
    if 'readonly=on,file="$decoy"' not in runner or '-drive if=none,id=seed,' in runner:
        raise RuntimeError("Mint v32 did not boot with its decoy read-only")

    (OUT / "root-mnt").mkdir(mode=0o700, exist_ok=True)
    base = load_module(HERE / "readback-postlogin-v31.py", "mint_v31_readback")
    base.OUT = OUT
    base.CHILD = OUT / "target-child.qcow2"
    base.PARENT = LAB / "mint-postlogin-v31/target-child.qcow2"
    base.main()

    evidence = OUT / "offline-readback"
    result = json.loads((evidence / "result.json").read_text())
    if (result.get("protectedWindowsPartitionsUnchanged") != [True, True]
            or result.get("chromeFlatpakInstalled") is not True
            or result.get("credentialStorePending") is not False
            or result.get("credentialReceiptStatus") != "consumed"
            or result.get("nativeChromeLoginDataCopied") is not True):
        raise RuntimeError("Mint v32 native import or protected-disk readback failed")
    receipt = json.loads((evidence / "credentials.done.json").read_text())
    synthetic = json.loads(SYNTHETIC.read_text())
    if (receipt.get("selectionSha256") != synthetic.get("selectionSha256")
            or receipt.get("status") != "consumed"):
        raise RuntimeError("Mint credential receipt differs from the synthetic selection")
    verifier = load_module(HERE.parents[1] / "agent/browser_credential_v10_verify.py",
                           "browser_credential_v10_verify")
    verifier.verify(evidence / "Login Data", synthetic["url"],
                    synthetic["username"], synthetic["passwordSha256"])
    print("PASS: Mint v32 native import, private consumed receipt, Chrome UI screenshot, "
          "protected Windows partitions, and read-only decoy GPT")


if __name__ == "__main__":
    main()
