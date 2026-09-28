#!/usr/bin/env python3
"""Real development import into a NEW regular-file EXT4 image, inside the VM.

Run in a private mount namespace. No caller device/path, physical disk or ESP is
accepted. This fixture cannot bypass DebianConfiguredRootStages.ImportSupport.
Failed fixtures and durable intents remain; there is no cleanup/retry shortcut.
"""
import datetime as dt
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import configured_root as artifact
from factory_artifact_verifier import authenticate
from factory_neutralization_rehearsal import checkpoint, environment


def command(argv, fds=(), timeout=120):
    result = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                            pass_fds=fds, timeout=timeout, check=False)
    if result.returncode:
        raise OSError("Fixture command failed: " + argv[0] + ": " + result.stderr.decode(errors="replace")[:1024])
    return result.stdout


def loop_witness(loop, image, expected):
    info = image.lstat()
    artifact.require(stat.S_ISREG(info.st_mode) and (info.st_dev, info.st_ino, info.st_size) == expected and info.st_nlink == 1,
                     "FixtureImageAncestryChanged")
    data = json.loads(command(["/usr/sbin/losetup", "--json", "--output", "NAME,BACK-FILE,BACK-INO,BACK-MAJ:MIN,RO", loop]))["loopdevices"]
    artifact.require(len(data) == 1 and data[0]["name"] == loop and data[0]["back-file"] == str(image) and
                     int(data[0]["back-ino"]) == info.st_ino and data[0]["back-maj:min"] == f"{os.major(info.st_dev)}:{os.minor(info.st_dev)}" and
                     not data[0]["ro"], "LoopNotExactNewImage")
    return data[0]


def mount_witness(target, loop, filesystem_uuid):
    rows = json.loads(command(["/usr/bin/findmnt", "--json", "--mountpoint", str(target), "--output", "TARGET,SOURCE,FSTYPE,OPTIONS,UUID,MAJ:MIN,ID,FSROOT,PROPAGATION"]))["filesystems"]
    s = os.stat(loop); r = rows[0]
    artifact.require(len(rows) == 1 and stat.S_ISBLK(s.st_mode) and r["source"] == loop and r["target"] == str(target) and
                     r["fstype"] == "ext4" and r["uuid"] == filesystem_uuid and r["fsroot"] == "/" and
                     r["maj:min"] == f"{os.major(s.st_rdev)}:{os.minor(s.st_rdev)}" and
                     r["propagation"] == "private" and {"rw", "nosuid", "nodev", "noexec"}.issubset(r["options"].split(",")), "Ext4MountReadbackChanged")
    artifact.require(sum(line.split()[4] == str(target) for line in Path("/proc/self/mountinfo").read_text().splitlines()) == 1, "StackedFixtureMount")
    return r


def run(workspace):
    environment(workspace)
    artifact.require(os.readlink("/proc/self/ns/mnt") != os.readlink("/proc/1/ns/mnt"), "FixtureMountNamespaceNotPrivate")
    verified = json.loads((workspace / "artifact-independent-verification.json").read_bytes())
    artifact.require(verified["Result"] == "ArtifactValid" and not verified["ImportedRoot"], "SourceNotIndependentlyVerified")
    pin = json.loads((workspace / "external-development-pin.json").read_bytes())
    descriptor = (workspace / "artifact/descriptor.json").read_bytes()
    value = authenticate(descriptor, pin, dt.datetime.now(dt.timezone.utc))
    image = workspace / "import.ext4"; target = workspace / "import-target"; identity = str(uuid.uuid4())
    checkpoint(workspace, "ext4-create-intent", {"Outcome": "IntentDurable", "Image": str(image), "Bytes": 12 * 1024 ** 3,
               "FilesystemUuid": identity, "DescriptorSha256": pin["DescriptorSha256"], "Ancestry": "New regular file inside isolated factory virtual disk"})
    fd = os.open(image, os.O_RDWR | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
    try:
        os.ftruncate(fd, 12 * 1024 ** 3); os.fsync(fd); s = os.fstat(fd)
        expected = (s.st_dev, s.st_ino, s.st_size)
        artifact.require(stat.S_ISREG(s.st_mode) and s.st_nlink == 1 and image.stat().st_ino == s.st_ino, "FixtureNotNewRegularFile")
        # Even a path substitution cannot turn this formatter argument into a
        # physical block device: it receives the already-verified regular-file FD.
        checkpoint(workspace, "ext4-format-intent", {"Outcome": "IntentDurable", "ImageWitness": list(expected), "FilesystemUuid": identity})
        command(["/usr/sbin/mkfs.ext4", "-F", "-m", "0", "-U", identity, "/proc/self/fd/" + str(fd)], (fd,))
        os.fsync(fd)
        observed_uuid = command(["/usr/sbin/blkid", "-p", "-s", "UUID", "-o", "value", "/proc/self/fd/" + str(fd)], (fd,)).decode().strip()
        artifact.require(observed_uuid == identity, "FormattedUuidChanged")
        before = command(["/usr/sbin/losetup", "--list", "--noheadings", "--output", "NAME"]).decode().split()
        checkpoint(workspace, "loop-acquire-intent", {"Outcome": "IntentDurable", "ImageWitness": list(expected), "BeforeLoops": before})
        loop = command(["/usr/sbin/losetup", "--find", "--show", "--nooverlap", "/proc/self/fd/" + str(fd)], (fd,)).decode().strip()
    finally: os.close(fd)
    artifact.require(loop not in before and loop.startswith("/dev/loop") and loop[9:].isdigit(), "LoopNotNew")
    backing = loop_witness(loop, image, expected)
    checkpoint(workspace, "loop-acquire-result", {"Outcome": "AppliedAndVerified", "Backing": backing})
    target.mkdir(mode=0o700)
    checkpoint(workspace, "ext4-mount-intent", {"Outcome": "IntentDurable", "Backing": backing, "Target": str(target)})
    loop_witness(loop, image, expected)
    command(["/usr/bin/mount", "-t", "ext4", "-o", "rw,nosuid,nodev,noexec", loop, str(target)])
    mounted = mount_witness(target, loop, identity); loop_witness(loop, image, expected)
    checkpoint(workspace, "ext4-mount-result", {"Outcome": "AppliedAndVerified", "Mount": mounted})
    fd = os.open(target, os.O_DIRECTORY | os.O_NOFOLLOW); info = os.fstat(fd)
    view = artifact.RootView(fd, info.st_dev, info.st_ino, artifact.mount_id(fd)); generation = str(uuid.uuid4())
    store = workspace / "import-journal"; store.mkdir(mode=0o700)
    plan_hash = artifact.digest(artifact.canonical({"Descriptor": pin["DescriptorSha256"], "Generation": generation,
                                                 "Image": list(expected), "FilesystemUuid": identity, "Mount": mounted}))
    journal = artifact.ImportJournal(str(store), generation, plan_hash, value["BuildId"])
    content = os.open(workspace / "artifact/root.content", os.O_RDONLY | os.O_NOFOLLOW)
    try:
        view.empty(); mount_witness(target, loop, identity); loop_witness(loop, image, expected)
        result = artifact.import_development_fixture(view, (workspace / "artifact/root.manifest.json").read_bytes(), content,
                 artifact.DevelopmentPin(value["BuildId"], pin["ManifestSha256"], pin["ContentSha256"], dt.datetime.fromisoformat(pin["NotAfterUtc"])),
                 journal, generation, plan_hash, dt.datetime.now(dt.timezone.utc))
    finally: os.close(content); view.close(); os.close(fd)
    command([sys.executable, "-I", "-B", str(workspace / "verifier/factory_artifact_verifier.py"), str(workspace),
             str(workspace / "external-development-pin.json"), "imported"], timeout=900)
    after = json.loads((workspace / "import-independent-verification.json").read_bytes())
    artifact.require(after["FilesystemSha256"] == verified["FilesystemSha256"] and after["PackageStateSha256"] == verified["PackageStateSha256"], "ImportedSemanticDifference")
    checkpoint(workspace, "ext4-import-result", {"Outcome": "AppliedAndVerified", "Qualification": "ImportMechanicsQualified",
               "NativeSupported": False, "GenerationId": generation, "PlanSha256": plan_hash, "FilesystemUuid": identity,
               "ExactFilesystemEquality": True, "ExactPackageEquality": True, "PackageCount": after["PackageCount"], "PrimitiveResult": result})
    artifact.require(mount_witness(target, loop, identity) == mounted, "MountChangedBeforeTeardown")
    loop_witness(loop, image, expected)
    checkpoint(workspace, "ext4-unmount-intent", {"Outcome": "IntentDurable", "Mount": mounted})
    command(["/usr/bin/umount", str(target)])  # no lazy/force unmount
    artifact.require(not any(line.split()[4] == str(target) for line in Path("/proc/self/mountinfo").read_text().splitlines()), "MountStillPresent")
    loop_witness(loop, image, expected)
    command(["/usr/sbin/losetup", "--detach", loop])
    artifact.require(not Path("/sys/class/block/" + Path(loop).name + "/loop/backing_file").exists(), "LoopStillAttached")
    checkpoint(workspace, "ext4-teardown-result", {"Outcome": "AppliedAndVerified", "MountId": mounted["id"], "MountAbsent": True,
               "ExactLoopDetached": True, "LazyOrForceUnmount": False, "ImageRetained": True})
    print("REAL_EXT4_IMPORT_AND_INDEPENDENT_EQUIVALENCE_VERIFIED", flush=True)


if __name__ == "__main__":
    workspace = Path(sys.argv[1])
    try: run(workspace)
    except (OSError, ValueError, subprocess.SubprocessError):
        checkpoint(workspace, "ext4-rehearsal-incomplete", {"Outcome": "OutcomeUnknown", "AutomaticRetry": False, "CleanupClaimed": False})
        raise
