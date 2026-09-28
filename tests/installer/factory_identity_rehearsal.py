#!/usr/bin/env python3
"""Two independent machine-identity helper rehearsals inside the derived VM.

This qualifies Debian helper semantics, NOT the production target stage host.
The neutral candidate is read-only; only newly copied instance configuration is
writable. No NIC, EFI, host mounts, package installation or firmware calls.
"""
import json
import os
from pathlib import Path
import stat
import subprocess
import sys

from factory_neutralization_rehearsal import environment, checkpoint
from factory_root_observer import digest


def run(workspace, attempt):
    assert attempt in ("1", "2", "3")
    environment(workspace); root = workspace / "root"; results = []
    for name in ("igloo-instance-" + attempt + "-a", "igloo-instance-" + attempt + "-b"):
        instance = workspace / name; instance.mkdir(mode=0o700)
        for source, destination in (("etc", "etc"), ("var/cache/debconf", "debconf"), ("var/lib/exim4", "exim")):
            subprocess.run(["cp", "--archive", "--reflink=auto", str(root / source), str(instance / destination)], check=True)
        etc = instance / "etc"
        assert not (etc / "ssl/private/ssl-cert-snakeoil.key").exists()
        (etc / "hostname").write_text(name + "\n"); (etc / "mailname").write_text(name + "\n")
        (etc / "hosts").write_text("127.0.0.1 localhost\n127.0.1.1 " + name + "\n::1 localhost\n")
        config = etc / "exim4/update-exim4.conf.conf"
        config.write_text(config.read_text().replace("dc_other_hostnames=''", "dc_other_hostnames='" + name + "'")
                          .replace("dc_mailname_in_oh='false'", "dc_mailname_in_oh='true'"))
        # As in the target package profile, do not use a one-UID user namespace:
        # Debian-exim/ssl-cert numeric ownership must remain representable. This
        # factory fixture still has no raw devices, firmware or external network.
        argv = [str(root / "usr/bin/bwrap"), "--unshare-pid", "--unshare-net", "--unshare-ipc", "--unshare-uts",
                "--cap-drop", "ALL", "--cap-add", "CAP_CHOWN", "--cap-add", "CAP_DAC_OVERRIDE", "--cap-add", "CAP_FOWNER",
                "--cap-add", "CAP_SETUID", "--cap-add", "CAP_SETGID", "--die-with-parent", "--ro-bind", str(root), "/",
                "--dev", "/dev", "--proc", "/proc", "--tmpfs", "/tmp", "--tmpfs", "/run", "--hostname", name,
                "--bind", str(etc), "/etc", "--bind", str(instance / "debconf"), "/var/cache/debconf",
                "--bind", str(instance / "exim"), "/var/lib/exim4", "--chdir", "/", "--clearenv",
                "--setenv", "PATH", "/usr/sbin:/usr/bin:/sbin:/bin", "--setenv", "DEBIAN_FRONTEND", "noninteractive"]
        checkpoint(instance, "intent", {"Outcome": "IntentDurable", "Namespace": "factory-only no-network no-efi",
                   "BwrapSha256": digest(root / "usr/bin/bwrap"), "Tools": {n: digest(root / n) for n in
                   ("usr/sbin/make-ssl-cert", "usr/sbin/update-exim4.conf", "usr/bin/debconf-communicate")}})
        def tool(command, data=None):
            value = subprocess.run(argv + command, input=data, stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=60, check=False)
            if value.returncode != 0:
                checkpoint(instance, "helper-failure", {"Outcome": "OutcomeUnknown", "Argv": command,
                           "ExitCode": value.returncode, "Diagnostic": value.stderr.decode(errors="replace")[:4096]})
                raise ValueError("Identity helper failed; retain instance, no retry")
            return value.stdout
        tool(["/usr/bin/debconf-communicate", "exim4-config"],
             ("SET exim4/mailname " + name + "\nSET exim4/dc_other_hostnames " + name + "\nFSET exim4/dc_other_hostnames mailname true\n").encode())
        tool(["/usr/sbin/update-exim4.conf"])
        hostname = tool(["/usr/sbin/exim4", "-bP", "primary_hostname"]).decode().strip()
        assert hostname == "primary_hostname = " + name
        tool(["/usr/sbin/make-ssl-cert", "generate-default-snakeoil"])
        key = etc / "ssl/private/ssl-cert-snakeoil.key"; cert = etc / "ssl/certs/ssl-cert-snakeoil.pem"
        public_key = subprocess.check_output(["openssl", "pkey", "-in", str(key), "-pubout"])
        certificate_key = subprocess.check_output(["openssl", "x509", "-in", str(cert), "-pubkey", "-noout"])
        subject = subprocess.check_output(["openssl", "x509", "-in", str(cert), "-subject", "-ext", "subjectAltName", "-noout"]).decode()
        assert public_key == certificate_key and "DNS:" + name in subject
        assert (key.stat().st_uid, key.stat().st_gid, stat.S_IMODE(key.stat().st_mode)) == (0, 105, 0o640)
        assert (cert.stat().st_uid, cert.stat().st_gid, stat.S_IMODE(cert.stat().st_mode)) == (0, 0, 0o644)
        result = {"Outcome": "AppliedAndVerified", "Hostname": name, "EximPrimaryHostname": hostname,
                  "KeySha256": digest(key), "CertificateSha256": digest(cert), "PublicKeyMatches": True,
                  "KeyOwnerGroupMode": [0, 105, 0o640], "CertificateOwnerGroupMode": [0, 0, 0o644],
                  "ProductionTargetHostQualified": False}
        checkpoint(instance, "result", result); results.append(result)
    assert results[0]["KeySha256"] != results[1]["KeySha256"] and results[0]["CertificateSha256"] != results[1]["CertificateSha256"]
    assert not (root / "etc/ssl/private/ssl-cert-snakeoil.key").exists()
    checkpoint(workspace, "identity-regeneration-rehearsal", {"IndependentInstances": results, "DifferentKeys": True,
               "NeutralSourceUnchanged": True, "TargetBrokerQualification": "Outstanding"})
    print("Two independent TLS/Exim identities verified; production target host remains unqualified", flush=True)


if __name__ == "__main__": run(Path(sys.argv[1]), sys.argv[2])
