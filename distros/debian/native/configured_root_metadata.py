"""Reviewed metadata profile from the first real Trixie factory (2026-09-28).

Manifest schema 2 is deliberately narrower than arbitrary Linux metadata. The
content stream stays v1. Unknown paths, ACLs and capabilities still block release.
This does not authenticate an artifact or qualify machine neutralization.
"""
import base64
import struct

CERT_NAME = "NetLock_Arany_=Class_Gold=_Főtanúsítvány"
UNICODE_PATHS = frozenset((
    "/etc/ssl/certs/" + CERT_NAME + ".pem",
    "/usr/share/ca-certificates/mozilla/" + CERT_NAME + ".crt"))
ESCAPED_UNIT_PATHS = frozenset((
    r"/usr/lib/systemd/system/system-systemd\x2dcryptsetup.slice",
    r"/usr/lib/systemd/system/system-systemd\x2dveritysetup.slice"))
GST_PATH = "/usr/lib/x86_64-linux-gnu/gstreamer1.0/gstreamer-1.0/gst-ptp-helper"
GST_SHA256 = "46436E050473C9EFD1802F581A63C274BA0C3EFBF69E4DE176802C59280FF7B5"
# libgstreamer1.0-0 1.26.2-2 postinst: NET_BIND_SERVICE, NET_ADMIN, SYS_NICE, +ep.
GST_CAPABILITY = struct.pack("<IIIII", 0x02000001, (1 << 10) | (1 << 12) | (1 << 23), 0, 0, 0)
JOURNAL_ACL = base64.b64decode("AgAAAAEABwD/////BAAFAP////8IAAUABAAAABAABQD/////IAAFAP////8=")
ACL_NAMES = frozenset(("system.posix_acl_access", "system.posix_acl_default"))
MASKED_UNITS = frozenset("/usr/lib/systemd/system/" + name + ".service" for name in (
    "alsa-utils", "cryptdisks-early", "cryptdisks", "hwclock", "saned", "sudo", "x11-common"))


def capability_allowed(entry, data):
    return (entry["Type"] == "File" and entry["Path"] == GST_PATH and entry["Uid"] == entry["Gid"] == 0 and
            entry["Mode"] == 0o755 and entry["Length"] == 534992 and entry["Sha256"] == GST_SHA256 and
            data == GST_CAPABILITY)


def acl_allowed(entry, name, data):
    # Actual systemd tmpfiles policy: adm (gid 4) read/search; systemd-journal gid 997.
    # The group database is verified independently; no blanket system.* passthrough.
    return (entry["Path"] == "/var/log/journal" and entry["Type"] == "Directory" and
            entry["Uid"] == 0 and entry["Gid"] == 997 and entry["Mode"] == 0o2755 and
            name in ACL_NAMES and data == JOURNAL_ACL and set(entry["Xattrs"]) == ACL_NAMES)


def runtime_link_allowed(path, target, destination):
    return ((path in MASKED_UNITS and target == destination == "/dev/null") or
            (path == "/etc/mtab" and target == "../proc/self/mounts" and destination == "/proc/self/mounts"))


def package_bindings(entries):
    """Each new metadata class requires the exact independently inspected package."""
    required = set()
    for entry in entries:
        if entry["Path"] in UNICODE_PATHS:
            required.add(("ca-certificates", "20250419", "all"))
        if entry["Path"] in ESCAPED_UNIT_PATHS:
            required.add(("systemd-cryptsetup", "257.13-1~deb13u1", "amd64"))
        if entry["Path"] == GST_PATH and "security.capability" in entry["Xattrs"]:
            required.add(("libgstreamer1.0-0", "1.26.2-2", "amd64"))
        if ACL_NAMES.intersection(entry["Xattrs"]):
            required.add(("systemd", "257.13-1~deb13u1", "amd64"))
    return required
