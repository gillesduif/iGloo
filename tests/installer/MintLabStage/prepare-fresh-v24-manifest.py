#!/usr/bin/env python3
"""Derive a fresh Mint lab manifest with keyed and promptable WPA inputs.

The target GUID must come from the newly created disposable GPT. This script
does not allocate media or reuse the v23 target identity.
"""

import argparse
import datetime
import json
import os
import pathlib
import uuid


LAB = pathlib.Path("/home/gillesduif/igloo-mint-22.3-lab")
BASE_MANIFEST = LAB / "mint-fresh-v23-manifest.input.json"


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("base_manifest", type=pathlib.Path)
    parser.add_argument("output", type=pathlib.Path)
    parser.add_argument("target_guid")
    parser.add_argument("target_bytes", type=int)
    parser.add_argument("staging_root", type=pathlib.Path)
    args = parser.parse_args()

    if args.base_manifest.resolve(strict=True) != BASE_MANIFEST or not args.base_manifest.is_file():
        raise ValueError("The manifest source must be the retained v23 lab input")
    if args.output.parent.resolve(strict=True) != LAB or "fresh-v24" not in args.output.name:
        raise ValueError("The new manifest must stay in the Mint v24 lab")
    if args.staging_root.parent.resolve(strict=True) != LAB:
        raise ValueError("The new stage must stay in the Mint lab")
    target_guid = uuid.UUID(args.target_guid)
    if target_guid.int == 0 or args.target_bytes < 30 * 1024**3:
        raise ValueError("Fresh disposable target identity and capacity are required")
    manifest = json.loads(args.base_manifest.read_text(encoding="utf-8"))
    if manifest.get("distroId") != "linuxmint-cinnamon":
        raise ValueError("Expected a Linux Mint manifest")
    hardware = manifest["hardware"]
    if uuid.UUID(hardware["targetDiskGuid"]) == target_guid:
        raise ValueError("The target GUID must differ from the prior lab run")
    if hardware.get("installMode") != "dual-boot":
        raise ValueError("Mint lab must exercise the supported dual-boot path")
    if not hardware.get("needsNonFreeCodecs") or not any(
        package.get("nativePackage") == "tree" and package.get("autoInstall")
        for package in manifest.get("suggestedPackages", [])
    ):
        raise ValueError("Mint lab must retain selected codec and package installation")
    if not any(browser.get("name") == "Google Chrome" and browser.get("credentialsBlob")
               for browser in manifest.get("browsers", [])):
        raise ValueError("Mint lab must retain the selected browser credential envelope")
    if not args.staging_root.is_absolute() or "fresh-v24" not in args.staging_root.name:
        raise ValueError("A new absolute v24 staging root is required")
    if manifest["files"].get("stagingPath") == str(args.staging_root):
        raise ValueError("The new stage must differ from the previous run")
    networks = manifest["wifiNetworks"]
    if len(networks) != 1 or networks[0].get("security") != "wpa-psk" or not networks[0].get("psk"):
        raise ValueError("Expected one previously selected keyed WPA fixture")
    if networks[0]["ssid"] == "IglooLabPromptOnly":
        raise ValueError("Wi-Fi fixtures must have distinct SSIDs")

    hardware["targetDiskGuid"] = str(target_guid)
    hardware["targetDiskBytes"] = args.target_bytes
    manifest["files"]["stagingPath"] = str(args.staging_root)
    manifest["generatedAtUtc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    networks.append({
        "ssid": "IglooLabPromptOnly",
        "security": "wpa-psk",
        "psk": None,
        "isPrimary": False,
        "hidden": False,
    })

    # This lab manifest contains a password hash, a WPA key, and a browser
    # credential envelope. Create it privately and never print their values.
    fd = os.open(args.output, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w", encoding="utf-8") as output:
        os.fchmod(output.fileno(), 0o600)
        json.dump(manifest, output, indent=2)
        output.write("\n")
        output.flush()
        os.fsync(output.fileno())
    print("Prepared a private Mint manifest for a fresh exact target and two selected WPA profiles")


if __name__ == "__main__":
    main()
