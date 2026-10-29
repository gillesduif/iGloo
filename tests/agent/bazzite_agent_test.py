#!/usr/bin/env python3
"""Ground truth for the Bazzite DX first-boot agent.

The agent runs the Fedora KDE agent's migration steps and replaces the ones an
image-based system does differently. These tests pin the replacements: signed
updates, the developer groups, the boot menu, Flatpak-only app installs, the
step list itself, and the /sysroot root the seed cleanup must look at.
"""
import importlib.util
import json
import sys
import tempfile
import types
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]


def _load(name: str, path: Path) -> types.ModuleType:
    spec = importlib.util.spec_from_file_location(name, path)
    mod = importlib.util.module_from_spec(spec)
    sys.modules[name] = mod
    spec.loader.exec_module(mod)
    return mod


# The plugin ships the Fedora agent next to this one as fedora_agent.py.
fedora = _load("fedora_agent", REPO / "distros/fedora-kde/agent/agent.py")
bz = _load("bazzite_agent", REPO / "distros/bazzite-dx/agent/agent.py")

failures: list[str] = []


def check(name: str, cond: bool, detail: str = "") -> None:
    print(("ok   " if cond else "FAIL ") + name + (f"  ({detail})" if detail and not cond else ""))
    if not cond:
        failures.append(name)


class FakeRun:
    """Stands in for run_cmd: records every command, answers from a table."""

    def __init__(self, answers: dict[str, tuple[int, str]] | None = None):
        self.calls: list[list[str]] = []
        self.answers = answers or {}

    def __call__(self, cmd, check=True, timeout=600, env=None):
        self.calls.append(list(cmd))
        code, out = 0, ""
        for prefix, answer in self.answers.items():
            if " ".join(cmd).startswith(prefix):
                code, out = answer
                break
        return types.SimpleNamespace(returncode=code, stdout=out, stderr="")

    def ran(self, *prefix: str) -> bool:
        return any(c[:len(prefix)] == list(prefix) for c in self.calls)


def with_run(fake: FakeRun):
    fedora.run_cmd = fake
    return fake


# ---------------------------------------------------------------------------

def test_signature_policy_detection():
    check("signed reference needs no switch",
          not bz.needs_signed_switch({"image": "x", "signature": "containerPolicy"}))
    check("unverified reference needs the switch",
          bz.needs_signed_switch({"image": "x", "transport": "registry"}))
    check("insecure reference needs the switch",
          bz.needs_signed_switch({"image": "x", "signature": "insecure"}))


def test_booted_image_prefers_spec():
    status = {
        "spec": {"image": {"image": "ghcr.io/ublue-os/bazzite-dx:stable"}},
        "status": {"booted": {"image": {"image": {"image": "other"}}}},
    }
    check("spec image wins", bz.booted_image(status)["image"] == "ghcr.io/ublue-os/bazzite-dx:stable")
    status = {"status": {"booted": {"image": {"image": {"image": "booted-ref"}}}}}
    check("booted image is the fallback", bz.booted_image(status)["image"] == "booted-ref")
    check("empty status gives empty image", bz.booted_image({}) == {})


def _status(signature):
    image = {"image": "ghcr.io/ublue-os/bazzite-dx:stable", "transport": "registry"}
    if signature:
        image["signature"] = signature
    return json.dumps({"spec": {"image": image}})


def test_failed_switch_is_not_a_failed_step(tmp: Path):
    marker = tmp / "image"
    marker.write_text("ghcr.io/ublue-os/bazzite-dx:stable\n", encoding="utf-8")
    bz.IMAGE_FILE = marker
    with_run(FakeRun({"bootc status": (0, _status(None)), "bootc switch": (1, "")}))
    try:
        bz.ensure_signed_image({"hardware": {}})
        raised = False
    except Exception:
        raised = True
    check("a failed bootc switch is logged, not raised (keeps seed cleanup going)", not raised)


def test_unsigned_deployment_is_switched(tmp: Path):
    marker = tmp / "image"
    marker.write_text("ghcr.io/ublue-os/bazzite-dx:stable\n", encoding="utf-8")
    bz.IMAGE_FILE = marker
    fake = with_run(FakeRun({"bootc status": (0, _status(None))}))
    bz.ensure_signed_image({"hardware": {"gpuVendor": "amd"}})
    check("unsigned deployment is switched to the signed reference",
          fake.ran("bootc", "switch", "--mutate-in-place", "--enforce-container-sigpolicy",
                   "--transport", "registry", "ghcr.io/ublue-os/bazzite-dx:stable"),
          str(fake.calls))


def test_signed_deployment_is_left_alone(tmp: Path):
    marker = tmp / "image"
    marker.write_text("ghcr.io/ublue-os/bazzite-dx:stable\n", encoding="utf-8")
    bz.IMAGE_FILE = marker
    fake = with_run(FakeRun({"bootc status": (0, _status("containerPolicy"))}))
    bz.ensure_signed_image({"hardware": {"gpuVendor": "amd"}})
    check("signed deployment is not switched again", not fake.ran("bootc", "switch"), str(fake.calls))


def test_unreadable_bootc_status_still_uses_the_marker(tmp: Path):
    marker = tmp / "image"
    marker.write_text("ghcr.io/ublue-os/bazzite-dx-nvidia:stable\n", encoding="utf-8")
    bz.IMAGE_FILE = marker
    fake = with_run(FakeRun({"bootc status": (1, "not json")}))
    bz.ensure_signed_image({"hardware": {"gpuVendor": "nvidia"}})
    check("the marker's image is switched to when bootc status fails",
          fake.ran("bootc", "switch", "--mutate-in-place", "--enforce-container-sigpolicy",
                   "--transport", "registry", "ghcr.io/ublue-os/bazzite-dx-nvidia:stable"),
          str(fake.calls))


def test_dx_groups_are_added_directly(tmp: Path):
    # The image lists its groups in /usr/lib/group; /etc/group starts without them.
    bz.ETC_GROUP = tmp / "group"
    bz.ETC_GROUP.write_text("wheel:x:10:gilles\nlibvirt:x:970:\n", encoding="utf-8")
    bz.USR_LIB_GROUP = tmp / "lib-group"
    bz.USR_LIB_GROUP.write_text("docker:x:980:\nincus-admin:x:975:\nlibvirt:x:970:\n",
                                encoding="utf-8")
    fake = FakeRun({"id -nG gilles": (0, "gilles wheel")})
    with_run(fake)
    bz.ensure_dx_groups({"user": {"preferredLinuxUsername": "gilles"}})
    for group in ("docker", "incus-admin", "libvirt"):
        check(f"user added to {group}", fake.ran("usermod", "-aG", group, "gilles"), str(fake.calls))
    etc = bz.ETC_GROUP.read_text(encoding="utf-8")
    check("missing groups copied from /usr/lib/group",
          "docker:x:980:" in etc and "incus-admin:x:975:" in etc, etc)
    check("existing /etc/group entry not duplicated", etc.count("libvirt:") == 1, etc)

    fake = with_run(FakeRun({"id -nG gilles": (0, "gilles wheel docker incus-admin libvirt")}))
    bz.ensure_dx_groups({"user": {"preferredLinuxUsername": "gilles"}})
    check("nothing to do once the user is in every group", not fake.ran("usermod"), str(fake.calls))


def test_default_apps_start_with_firefox():
    fake = with_run(FakeRun())
    bz.install_default_apps({})
    first = fake.calls[0] if fake.calls else []
    check("Firefox is installed first, on its own", first[-1:] == ["org.mozilla.firefox"], str(first))
    check("the rest of Bazzite's KDE apps follow",
          len(fake.calls) == 2 and "org.kde.okular" in fake.calls[1], str(fake.calls))


def test_set_grub_option():
    text = 'GRUB_TIMEOUT=5\nGRUB_DISABLE_OS_PROBER=true\nGRUB_ENABLE_BLSCFG=true\n'
    out = bz.set_grub_option(text, "GRUB_DISABLE_OS_PROBER", "false")
    check("existing value replaced", "GRUB_DISABLE_OS_PROBER=false" in out
          and "GRUB_DISABLE_OS_PROBER=true" not in out, out)
    check("other settings kept, BLS untouched", "GRUB_ENABLE_BLSCFG=true" in out, out)
    out = bz.set_grub_option("GRUB_TIMEOUT=5\n", "GRUB_DISABLE_OS_PROBER", "false")
    check("missing value appended", out.endswith("GRUB_DISABLE_OS_PROBER=false\n"), out)
    check("idempotent", bz.set_grub_option(out, "GRUB_DISABLE_OS_PROBER", "false") == out)
    check("empty file gets the setting",
          bz.set_grub_option("", "GRUB_DISABLE_OS_PROBER", "false") == "GRUB_DISABLE_OS_PROBER=false\n")


def _boot_menu(tmp: Path, mode: str, cfg_text: str):
    bz.GRUB_DEFAULT = tmp / "default-grub"
    bz.GRUB_DEFAULT.write_text("GRUB_TIMEOUT=5\n", encoding="utf-8")
    bz.GRUB_CFG = tmp / "grub.cfg"
    bz.GRUB_CFG.write_text(cfg_text, encoding="utf-8")
    calls = []
    fedora.igloo_boot = types.SimpleNamespace(
        fedora=lambda run, log: "boot",
        put_self_first_in_boot_order=lambda b: calls.append("order"),
        install_boot_order_unit=lambda b: calls.append("unit"),
    )
    fake = with_run(FakeRun())
    bz.configure_boot_menu({"hardware": {"installMode": mode}})
    return fake, calls


def test_boot_menu_dual_boot(tmp: Path):
    fake, calls = _boot_menu(tmp, "dual-boot", "menuentry 'Windows Boot Manager' {}\n")
    check("os-prober enabled", "GRUB_DISABLE_OS_PROBER=false" in bz.GRUB_DEFAULT.read_text())
    check("grub.cfg regenerated", fake.ran("grub2-mkconfig", "-o"), str(fake.calls))
    check("menu shown when dual-booting",
          fake.ran("grub2-editenv", "-", "set", "menu_auto_hide=1"), str(fake.calls))
    check("UEFI order and its unit handled", calls == ["order", "unit"], str(calls))


def test_boot_menu_replace(tmp: Path):
    fake, calls = _boot_menu(tmp, "replace", "")
    check("grub.cfg left as bootupd wrote it on a single-OS disk",
          not fake.ran("grub2-mkconfig") and not fake.ran("grub2-editenv"), str(fake.calls))
    check("/etc/default/grub untouched in replace mode",
          bz.GRUB_DEFAULT.read_text() == "GRUB_TIMEOUT=5\n")
    check("UEFI order still handled in replace mode", calls == ["order", "unit"], str(calls))


def test_suggested_packages_are_flatpak_only():
    fake = with_run(FakeRun())
    bz.install_suggested_packages({"suggestedPackages": [
        {"windowsAppName": "VLC", "flatpakId": "org.videolan.VLC", "nativePackage": "vlc",
         "autoInstall": True},
        {"windowsAppName": "Foo", "nativePackage": "foo", "autoInstall": True},
        {"windowsAppName": "Bar", "flatpakId": "com.example.Bar", "autoInstall": False},
    ]})
    check("auto-install Flatpak installed",
          fake.ran("flatpak", "install", "-y", "--noninteractive", "flathub", "org.videolan.VLC"),
          str(fake.calls))
    check("no package manager touches the image",
          not any(c[0] in ("dnf", "rpm-ostree") for c in fake.calls), str(fake.calls))
    check("non-auto-install package skipped",
          not any("com.example.Bar" in c for c in fake.calls), str(fake.calls))


def test_step_list():
    names = [n for n, _ in bz.steps({})]
    for gone in ("rpmfusion", "codecs", "gpu-drivers", "kernel-modules"):
        check(f"no {gone} step on an image-based system", gone not in names)
    check("Flatpaks before browser profiles",
          names.index("suggested-pkgs") < names.index("browser-profiles")
          and names.index("default-apps") < names.index("browser-profiles"))
    check("credentials staged before the manifest is redacted",
          names.index("stage-credentials") < names.index("redact-manifest"))
    check("logs exported before the seed is deleted",
          names.index("export-logs") < names.index("cleanup-seed"))
    check("cleanup is last", names[-1] == "cleanup-seed")


def test_cleanup_reads_the_physical_root():
    fake = with_run(FakeRun())
    fedora.ROOT_MOUNT = "/sysroot"
    try:
        fedora.cleanup_installer_partitions({})
    finally:
        fedora.ROOT_MOUNT = "/"
    check("seed cleanup resolves the disk from /sysroot, not the composefs /",
          ["findmnt", "-rno", "SOURCE", "--nofsroot", "/sysroot"] in fake.calls, str(fake.calls))


def test_main_rejects_another_distros_manifest(tmp: Path):
    manifest = tmp / "manifest.json"
    manifest.write_text(json.dumps({"schemaVersion": 1, "distroId": "fedora-kde"}), encoding="utf-8")
    argv = sys.argv
    sys.argv = ["agent.py", "--manifest", str(manifest), "--log-dir", str(tmp / "logs")]
    try:
        code = bz.main()
    finally:
        sys.argv = argv
        fedora.ROOT_MOUNT = "/"
    check("a manifest for another distro is refused", code == 3, str(code))


if __name__ == "__main__":
    test_signature_policy_detection()
    test_booted_image_prefers_spec()
    test_set_grub_option()
    test_suggested_packages_are_flatpak_only()
    test_default_apps_start_with_firefox()
    test_step_list()
    test_cleanup_reads_the_physical_root()
    for test in (test_unsigned_deployment_is_switched, test_signed_deployment_is_left_alone,
                 test_unreadable_bootc_status_still_uses_the_marker,
                 test_failed_switch_is_not_a_failed_step,
                 test_dx_groups_are_added_directly,
                 test_boot_menu_dual_boot, test_boot_menu_replace,
                 test_main_rejects_another_distros_manifest):
        with tempfile.TemporaryDirectory(prefix="bazzite-agent-test-") as d:
            test(Path(d))
    print()
    if failures:
        print(f"{len(failures)} FAILED: {', '.join(failures)}")
        sys.exit(1)
    print("all checks passed")
