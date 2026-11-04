#!/usr/bin/env python3
"""Check the installer-selected OEMDRV before importing any first-boot payload."""

import hashlib
import json
from pathlib import Path
import re
import stat
import subprocess
import sys
import unicodedata
import uuid


GUID = re.compile(r"[0-9a-fA-F]{8}(?:-[0-9a-fA-F]{4}){3}-[0-9a-fA-F]{12}\Z")
HASH = re.compile(r"[0-9a-fA-F]{64}\Z")
SECRET_FILES = {"migration-manifest.json", "preseed.cfg", "igloo-boot/initrd"}
COMMON_AGENT_FILES = {
    "agent.py", "first-boot.sh", "display-apply.py", "display-apply-gnome.py",
    "igloo_boot.py", "igloo-first-boot.service",
    "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz",
}
MINT_AGENT_FILES = COMMON_AGENT_FILES | {
    "igloo-bootstrap.sh", "igloo-bootstrap.service", "igloo-verify-seed.py",
}
# Inventory bounds cap memory while reading an untrusted seed; they do not decide how
# much a user may migrate. They must match SeedIdentityWriter on the Windows side
# (1,000,000 files / 2,000,000 entries / 256,000,000 path characters).
MAX_USER_FILES = 1_000_000
MAX_USER_ENTRIES = 2_000_000
MAX_USER_ROOTS = 1_024
MAX_USER_PATH_CHARACTERS = 256_000_000


def _selected_media_paths(manifest: dict) -> set[str]:
    selected = set()
    wallpaper = manifest.get("wallpaper") or {}
    picture = manifest.get("accountPicture") or {}
    if not isinstance(wallpaper, dict) or not isinstance(picture, dict):
        raise ValueError("Selected media manifest is invalid")
    wallpaper_name = wallpaper.get("fileName") or ""
    picture_name = picture.get("fileName") or ""
    if wallpaper_name:
        if not isinstance(wallpaper_name, str) or not re.fullmatch(
                r"igloo-wallpaper\.[a-z0-9]{2,8}", wallpaper_name, re.IGNORECASE):
            raise ValueError("Selected wallpaper name is unsafe")
        selected.add(wallpaper_name)
    if picture_name:
        if picture_name != "igloo-avatar.jpg":
            raise ValueError("Selected account picture name is unsafe")
        selected.add(picture_name)
    return selected


def _agent_paths(seed_mount: Path, distro_id: str) -> set[str]:
    expected = (COMMON_AGENT_FILES if distro_id == "debian" else
                MINT_AGENT_FILES if distro_id == "linuxmint-cinnamon" else None)
    if expected is None:
        raise ValueError("Unsupported Debian-family seed distribution")
    directory = seed_mount / "igloo-agent"
    if directory.is_symlink() or not directory.is_dir():
        raise ValueError("Seed agent directory is missing or redirected")
    observed = set()
    for entry in directory.iterdir():
        if entry.is_symlink() or not entry.is_file():
            raise ValueError("Seed agent inventory contains a redirected or nested entry")
        observed.add(entry.name)
    if observed != expected:
        raise ValueError("Seed agent inventory differs from provider payload")
    return {"igloo-agent/" + name for name in expected}


def _safe_user_segment(segment: str) -> bool:
    base = segment.split(".", 1)[0].upper()
    reserved = base in {"CON", "PRN", "AUX", "NUL"} or re.fullmatch(
        r"(?:COM|LPT)[1-9]", base) is not None
    return (bool(segment) and not segment.isspace() and segment not in (".", "..")
            and segment == unicodedata.normalize("NFC", segment) and not reserved
            and not segment.endswith((" ", "."))
            and all(unicodedata.category(char) != "Cc" and
                    char not in '<>":|?*/\\' for char in segment))


def _safe_user_path(path: str) -> bool:
    return (isinstance(path, str) and 0 < len(path) <= 1024 and
            "\\" not in path and all(_safe_user_segment(part)
                                      for part in path.split("/")))


def _selected_user_paths(seed_mount: Path, manifest: dict) -> set[str]:
    roots = set()
    root_aliases = set()

    def add_root(path: str) -> None:
        if not _safe_user_path(path):
            raise ValueError("Selected user-data path is unsafe")
        alias = unicodedata.normalize("NFC", path).casefold()
        if alias in root_aliases:
            raise ValueError("Selected user-data roots are ambiguous")
        root_aliases.add(alias)
        roots.add(path)
        if len(roots) > MAX_USER_ROOTS:
            raise ValueError("Selected user-data roots are excessive")

    files = manifest.get("files")
    if files is None:
        files = {}
    if not isinstance(files, dict):
        raise ValueError("Selected folder metadata is invalid")
    folders = files.get("folders")
    if folders is None:
        folders = []
    if not isinstance(folders, list):
        raise ValueError("Selected folder metadata is invalid")
    if folders:
        if any(not isinstance(folder, dict) or not isinstance(folder.get("name"), str)
               for folder in folders):
            raise ValueError("Selected folder metadata is invalid")
        folder_names = [folder["name"] for folder in folders]
    else:
        folder_names = files.get("includedFolders")
        if folder_names is None:
            folder_names = []
        if not isinstance(folder_names, list) or any(
                not isinstance(name, str) for name in folder_names):
            raise ValueError("Selected folder metadata is invalid")
    staging_path = files.get("stagingPath")
    if staging_path is not None and not isinstance(staging_path, str):
        raise ValueError("Selected folder staging metadata is invalid")
    # A staging path in the manifest no longer implies a copy on the seed: the
    # Debian-family first boot reads selected folders from the Windows volume
    # (alpha flow). Bind folders only when the seed actually carries them.
    if (seed_mount / "files").exists():
        for name in folder_names:
            if not _safe_user_segment(name):
                raise ValueError("Selected folder name is unsafe")
            add_root("files/" + name)

    browsers = manifest.get("browsers")
    if browsers is None:
        browsers = []
    if not isinstance(browsers, list):
        raise ValueError("Selected browser metadata is invalid")
    for browser in browsers:
        if not isinstance(browser, dict):
            raise ValueError("Selected browser metadata is invalid")
        staged = browser.get("profileStagingPath")
        if staged is None or staged == "":
            continue  # Historical NTFS-only selection.
        source = browser.get("sourceRelativePath")
        if not isinstance(staged, str) or not isinstance(source, str):
            raise ValueError("Staged browser selection has no source binding")
        source = source.replace("\\", "/")
        if not _safe_user_path(source) or staged != "browser-profiles/" + source:
            raise ValueError("Staged browser selection differs from its selected source")
        # Bound only when the snapshot is on the seed; otherwise first boot reads
        # it from the staging directory on the Windows volume.
        if (seed_mount / staged).is_dir():
            add_root(staged)

    observed = set()
    observed_directories = set()
    aliases = set()
    entries = 0
    path_characters = 0
    for namespace in ("files", "browser-profiles"):
        namespace_root = seed_mount / namespace
        chosen = {root for root in roots if root.startswith(namespace + "/")}
        if namespace_root.is_symlink() or (namespace_root.exists() and
                                           (not namespace_root.is_dir() or not chosen)):
            raise ValueError("Seed contains an unselected or redirected user-data root")
        if not namespace_root.is_dir():
            if chosen:
                raise ValueError("Selected user-data root is missing")
            continue
        pending = [namespace_root]
        while pending:
            for entry in pending.pop().iterdir():
                if entry.is_symlink():
                    raise ValueError("Selected user data contains a redirected entry")
                relative = entry.relative_to(seed_mount).as_posix()
                if not _safe_user_path(relative):
                    raise ValueError("Selected user-data path is unsafe")
                alias = unicodedata.normalize("NFC", relative).casefold()
                entries += 1
                if alias in aliases or entries > MAX_USER_ENTRIES:
                    raise ValueError("Selected user-data inventory is ambiguous or excessive")
                aliases.add(alias)
                if entry.is_dir():
                    if not any(relative == root or relative.startswith(root + "/") or
                               root.startswith(relative + "/") for root in chosen):
                        raise ValueError("Seed contains an unselected user-data directory")
                    observed_directories.add(relative)
                    pending.append(entry)
                elif entry.is_file():
                    if not any(relative.startswith(root + "/") for root in chosen):
                        raise ValueError("Seed contains an unselected user-data file")
                    path_characters += len(relative)
                    if len(observed) >= MAX_USER_FILES or path_characters > MAX_USER_PATH_CHARACTERS:
                        raise ValueError("Selected user-data inventory is too large")
                    observed.add(relative)
                else:
                    raise ValueError("Selected user data contains a special entry")
    for root in roots:
        selected = seed_mount / root
        if selected.is_symlink() or not selected.is_dir():
            raise ValueError("Selected user-data directory is missing or redirected")
    bound_directories = set()
    for path in roots:
        parts = path.split("/")
        bound_directories.update("/".join(parts[:index])
                                 for index in range(1, len(parts) + 1))
    for path in observed:
        parts = path.split("/")
        bound_directories.update("/".join(parts[:index])
                                 for index in range(1, len(parts)))
    for directory in observed_directories:
        if directory not in bound_directories:
            raise ValueError("Selected user data contains an unbound empty directory")
    return observed


def _guid(value: object) -> str:
    if not isinstance(value, str) or not GUID.fullmatch(value):
        raise ValueError("Invalid seed GPT identifier")
    return str(uuid.UUID(value))


def _command(*args: str) -> str:
    result = subprocess.run(args, check=True, capture_output=True, text=True)
    return result.stdout.strip()


def _block_number(path: Path) -> int:
    info = path.stat()
    if not stat.S_ISBLK(info.st_mode):
        raise ValueError("Bound seed path is not a block device")
    return info.st_rdev


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as payload:
        for block in iter(lambda: payload.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def verify_seed(selected_path: Path, selected_disk_path: Path, seed_mount: Path) -> list[str]:
    selected = _guid(selected_path.read_text(encoding="ascii").strip())
    selected_disk = _guid(selected_disk_path.read_text(encoding="ascii").strip())
    exact = Path("/dev/disk/by-partuuid") / selected
    selected_number = _block_number(exact)
    matches = [value for value in _command("lsblk", "-rno", "PARTUUID").splitlines()
               if GUID.fullmatch(value.strip()) and _guid(value.strip()) == selected]
    if len(matches) != 1:
        raise ValueError("Seed PARTUUID is missing or ambiguous")
    if not seed_mount.is_mount():
        raise ValueError("Selected seed is not mounted")
    source_name = _command("findmnt", "-rn", "-o", "SOURCE", "--mountpoint", str(seed_mount))
    if "\n" in source_name or not source_name.startswith("/dev/"):
        raise ValueError("Seed mount has no unique block source")
    source = Path(source_name)
    if _block_number(source) != selected_number or seed_mount.stat().st_dev != selected_number:
        raise ValueError("Mounted seed differs from selected PARTUUID")
    if _guid(_command("lsblk", "-dnro", "PARTUUID", str(source))) != selected:
        raise ValueError("Mounted partition GUID differs from selected seed")
    parent_name = _command("lsblk", "-dnro", "PKNAME", str(source))
    if not parent_name or "/" in parent_name or "\n" in parent_name:
        raise ValueError("Seed has no unique parent disk")
    disk_guid = _guid(_command("lsblk", "-dnro", "PTUUID", "/dev/" + parent_name))
    if disk_guid != selected_disk:
        raise ValueError("Seed parent disk differs from installer selection")

    identity_file = seed_mount / "igloo-seed-identity.json"
    if identity_file.is_symlink() or not identity_file.is_file():
        raise ValueError("Seed identity file is missing")
    identity_bytes = identity_file.read_bytes()
    identity = json.loads(identity_bytes)
    if (identity.get("version") != 1 or _guid(identity.get("partitionGuid")) != selected
            or _guid(identity.get("diskGuid")) != disk_guid):
        raise ValueError("Seed identity differs from selected GPT partition")
    records = identity.get("files")
    if not isinstance(records, list):
        raise ValueError("Seed has no bound file list")
    manifest_file = seed_mount / "migration-manifest.json"
    if manifest_file.is_symlink() or not manifest_file.is_file():
        raise ValueError("Seed manifest is missing or redirected")
    manifest = json.loads(manifest_file.read_bytes())
    if not isinstance(manifest, dict):
        raise ValueError("Seed manifest is invalid")
    agent_paths = _agent_paths(seed_mount, manifest.get("distroId"))
    media_paths = _selected_media_paths(manifest)
    user_paths = _selected_user_paths(seed_mount, manifest)
    for entry in seed_mount.iterdir():
        if entry.name.lower().startswith(("igloo-wallpaper.", "igloo-avatar.")) and entry.name not in media_paths:
            raise ValueError("Seed contains unselected migration media")
    allowed_paths = SECRET_FILES | agent_paths | media_paths | user_paths
    retry_hashes = {}
    journal = selected_path.parent / "seed-retry/journal.json"
    if journal.exists():
        if journal.is_symlink() or stat.S_IMODE(journal.stat().st_mode) & 0o077:
            raise ValueError("Seed scrub retry journal is not private")
        retry = json.loads(journal.read_text(encoding="utf-8"))
        retry_hashes = retry.get("redactedHashes")
        if (retry.get("identitySha256") != hashlib.sha256(identity_bytes).hexdigest()
                or not isinstance(retry_hashes, dict)):
            raise ValueError("Seed scrub retry journal differs from selected identity")
    seen = set()
    for record in records:
        if not isinstance(record, dict):
            raise ValueError("Invalid bound seed file")
        name, digest = record.get("path"), record.get("sha256")
        if name not in allowed_paths or name in seen or not isinstance(digest, str) or not HASH.fullmatch(digest):
            raise ValueError("Unexpected or duplicate bound seed file")
        seen.add(name)
        source_file = seed_mount / name
        if source_file.is_symlink() or not source_file.is_file():
            raise ValueError("Bound seed file is missing")
        actual = _sha256_file(source_file)
        allowed = {digest.lower()}
        if retry_hashes and name in SECRET_FILES:
            retry_hash = retry_hashes.get(name)
            if not isinstance(retry_hash, str) or not HASH.fullmatch(retry_hash):
                raise ValueError("Seed scrub retry journal has an invalid file hash")
            allowed.add(retry_hash.lower())
        if actual.lower() not in allowed:
            raise ValueError("Bound seed file changed after staging")
    if not {"migration-manifest.json", "preseed.cfg"}.issubset(seen):
        raise ValueError("Seed omits its manifest or installer config binding")
    if (seed_mount / "igloo-boot/initrd").is_file() and "igloo-boot/initrd" not in seen:
        raise ValueError("Seed omits its injected initrd binding")
    if not agent_paths.issubset(seen):
        raise ValueError("Seed omits exact agent file bindings")
    if not media_paths.issubset(seen):
        raise ValueError("Seed omits selected media bindings")
    if not user_paths.issubset(seen):
        raise ValueError("Seed omits selected user-data bindings")
    if retry_hashes and set(retry_hashes) != seen & SECRET_FILES:
        raise ValueError("Seed scrub retry journal has a different file set")
    return sorted(media_paths)


if __name__ == "__main__":
    try:
        for name in verify_seed(Path(sys.argv[1]), Path(sys.argv[2]), Path(sys.argv[3])):
            print(name)
    except (IndexError, OSError, ValueError, subprocess.CalledProcessError) as error:
        print(f"iGloo seed validation failed: {error}", file=sys.stderr)
        raise SystemExit(1)
