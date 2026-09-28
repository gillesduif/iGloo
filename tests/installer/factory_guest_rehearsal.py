#!/usr/bin/env python3
"""Explicit disposable-VM experiment; never imported by migration dispatch.

The external controller must first prove the QEMU configuration and file-backed
disk ancestry. Guest checks complement that proof; DMI alone is not containment.
No factory package privileges are made available to an iGloo target session.
"""
import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import sys
import time
import uuid


def require(value, code):
    if not value:
        raise ValueError(code)


def sha(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest().upper()


def read_json(path):
    return json.loads(Path(path).read_bytes())


def run(argv):
    result = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=300, check=False)
    require(result.returncode == 0, "ObserverFailed:" + argv[0])
    return result.stdout


def checkpoint(directory, name, value):
    payload = json.dumps(value, sort_keys=True, separators=(",", ":")).encode()
    path = directory / (name + ".json")
    with path.open("xb") as output:
        output.write(payload); output.flush(); os.fsync(output.fileno())
    fd = os.open(directory, os.O_RDONLY | os.O_DIRECTORY)
    try:
        os.fsync(fd)
    finally:
        os.close(fd)
    observed = run(["/usr/bin/sha256sum", str(path)]).decode().split()[0].upper()
    require(observed == hashlib.sha256(payload).hexdigest().upper(), "CheckpointReopenChanged")


def environment():
    require(os.geteuid() == 0 and Path("/sys/class/dmi/id/sys_vendor").read_text().strip() == "QEMU", "NotDisposableFactory")
    require(sorted(p.name for p in Path("/sys/class/net").iterdir()) == ["lo"], "FactoryHasNetworkInterface")
    require(not Path("/sys/firmware/efi").exists(), "FactoryHasEfi")
    require(Path("/sys/block/vda/serial").read_text().strip() == "IGLOO-FACTORY", "FactoryDiskSerialMismatch")
    require(sorted(p.name for p in Path("/sys/block").iterdir() if not p.name.startswith(("loop", "ram"))) == ["sr0", "vda"], "UnexpectedFactoryDevice")
    mounts = read_json_bytes(run(["/usr/bin/findmnt", "--json", "--mountpoint", "/input", "--output", "SOURCE,FSTYPE,OPTIONS"]))["filesystems"]
    require(len(mounts) == 1 and mounts[0]["source"] == "/dev/sr0" and mounts[0]["fstype"] == "iso9660" and
            "ro" in mounts[0]["options"].split(","), "FactoryInputNotReadonlyOpticalMedia")
    return {"Mount": mounts, "BlockDevices": read_json_bytes(run(["/usr/bin/lsblk", "--json", "--output", "NAME,TYPE,PKNAME,FSTYPE,SERIAL"])),
            "Kernel": run(["/usr/bin/uname", "-a"]).decode().strip(), "Network": ["lo"], "Efi": False}


def read_json_bytes(data):
    return json.loads(data)


def qualify_inputs(expected_manifest, expected_request):
    require(sha("/input/bundle/package-set.json") == expected_manifest and sha("/input/request.json") == expected_request,
            "ExternalInputPinMismatch")
    spec = importlib.util.spec_from_file_location("factory_offline", "/input/native/offline_bundle.py")
    bundle = importlib.util.module_from_spec(spec); spec.loader.exec_module(bundle)
    request = read_json("/input/request.json"); manifest = read_json("/input/bundle/package-set.json")
    policy = read_json("/input/policy.json"); keyring = Path("/input/bundle/debian-archive-keyring.gpg")
    require(sha(keyring) == request["KeyringSha256"] and sha("/input/policy.json") == request["PolicySha256"], "SourceTrustMismatch")
    repos, records = bundle.acquire_metadata(Path("/input/bundle"), request, keyring, False, dt.datetime.now(dt.timezone.utc))
    require(repos == manifest["Repositories"], "RepositoryGenerationChanged")
    selected, standard, apt, report = bundle.apt_solve(Path("/input/bundle"), request, policy, keyring, records)
    require(selected == manifest["Packages"] and standard == manifest["StandardPackages"] and
            report == manifest["DependencyPolicy"] and bundle.repository_aliases(selected, records) == manifest["RepositoryAliases"],
            "FactoryAptSolutionChanged")
    for package in [*selected, *manifest["RepositoryAliases"], *manifest["BootstrapArchives"]]:
        bundle.verify_file(Path("/input/bundle") / package["RepositoryId"], package["File"])
    return manifest, {"AptVersion": apt, "PackageCount": len(selected), "DependencyPolicy": report,
                      "ManifestSha256": expected_manifest, "KeyringSha256": request["KeyringSha256"]}


def build(argv):
    parser = argparse.ArgumentParser()
    parser.add_argument("--external-package-set-sha256", required=True)
    parser.add_argument("--external-request-sha256", required=True)
    args = parser.parse_args(argv)
    observation = environment()
    manifest, inputs = qualify_inputs(args.external_package_set_sha256, args.external_request_sha256)
    build_id = str(uuid.UUID(manifest["BuildId"]))
    evidence = Path("/factory-evidence") / build_id
    evidence.mkdir(parents=True, exist_ok=False)
    destination = Path("/factory") / ("root-" + build_id)
    destination.parent.mkdir(exist_ok=True)
    require(not destination.exists() and not destination.is_symlink(), "FactoryRootNotFresh")
    runtime = {}
    for tool in ("mmdebstrap", "apt-get", "dpkg", "gpgv", "python3"):
        path = Path("/usr/bin") / tool
        runtime[tool] = {"Sha256": sha(path), "Version": run([str(path), "--version"]).decode().splitlines()[0]}
    require(runtime["mmdebstrap"]["Version"] == "mmdebstrap 1.5.7", "FactoryBuilderVersionChanged")
    require(runtime["mmdebstrap"]["Sha256"] == "C8A55D9731E81C00A78DB8824854A099ADBDB516F83F88B6D546D4D17B03EA1A", "FactoryBuilderHashChanged")
    checkpoint(evidence, "inputs-verified", {"Environment": observation, "Inputs": inputs, "Runtime": runtime})
    includes = ",".join(p["Name"] + (":amd64" if p["Architecture"] == "amd64" else "") + "=" + p["Version"] for p in manifest["Packages"])
    command = ["/usr/bin/mmdebstrap", "--mode=root", "--variant=custom", "--format=directory", "--architectures=amd64",
               "--components=main,contrib,non-free,non-free-firmware", "--keyring=/input/bundle/debian-archive-keyring.gpg",
               '--aptopt=APT::Install-Recommends "true"', '--aptopt=APT::Install-Suggests "false"',
               '--aptopt=Acquire::Retries "0"', '--aptopt=Acquire::Languages "none"',
               "--hook-dir=/usr/share/mmdebstrap/hooks/file-mirror-automount", "--include=" + includes,
               "--verbose", "trixie", str(destination)]
    for repo in manifest["Repositories"]:
        command.append("deb [arch=amd64] file:///input/bundle/" + repo["Id"] + " " + repo["Suite"] + " main contrib non-free non-free-firmware")
    checkpoint(evidence, "build-intent", {"BuildId": build_id, "Argv": command, "Destination": str(destination),
                                         "InputManifestSha256": args.external_package_set_sha256, "State": "IntentDurable"})
    environment()
    start = time.monotonic(); timed_out = False
    with (evidence / "mmdebstrap.log").open("xb") as log:
        child = subprocess.Popen(command, stdin=subprocess.DEVNULL, stdout=log, stderr=log,
                                 env={"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "DEBIAN_FRONTEND": "noninteractive"})
        while child.poll() is None:
            if time.monotonic() - start > 1800 or log.tell() > 64 * 1024 * 1024:
                timed_out = True; child.terminate(); break
            time.sleep(1)
        try:
            code = child.wait(timeout=30)
        except subprocess.TimeoutExpired:
            child.kill(); code = child.wait()
        log.flush(); os.fsync(log.fileno())
    checkpoint(evidence, "build-command-result", {"ExitCode": code, "TimedOut": timed_out,
               "State": "PendingIndependentReadback" if code == 0 else "FailedWithPossiblePartialRoot",
               "LogSha256": sha(evidence / "mmdebstrap.log"), "RootExists": destination.exists()})
    print(json.dumps({"BuildId": build_id, "ExitCode": code, "Root": str(destination), "Evidence": str(evidence)}), flush=True)
    return 0 if code == 0 and not timed_out else 1


if __name__ == "__main__":
    sys.exit(build(sys.argv[1:]))
