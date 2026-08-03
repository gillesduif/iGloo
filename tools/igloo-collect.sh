#!/usr/bin/env bash
# =============================================================================
# igloo-collect - gather every Igloo/install diagnostic into one folder on a USB
# -----------------------------------------------------------------------------
# Run it on the freshly installed Linux system:
#
#     sudo bash /run/media/$USER/<STICK>/igloo-collect.sh
#
# After the first run it installs itself to /usr/local/bin/igloo-logs, so from
# then on the whole thing is just:
#
#     sudo igloo-logs
#
# It writes <stick>/igloo-logs-<host>-<timestamp>/ plus a .tar.gz of the same,
# then prints where it put them. Everything is best-effort: a missing file or a
# command that does not exist is recorded in the output instead of aborting the
# run, because a partial bundle collected once beats a perfect one that needs a
# second reboot to obtain.
#
# SECRETS: the migration manifest holds the Linux password and Wi-Fi PSKs in
# plaintext until the agent redacts them. This script NEVER copies it verbatim -
# those fields are stripped before writing. Check redacted-manifest.json yourself
# before sharing a bundle.
# =============================================================================
set -u

TS="$(date -u '+%Y%m%d-%H%M%SZ')"
HOST="$(hostname 2>/dev/null || echo unknown)"

#   Locate the destination
# Priority: explicit argument > the stick this script is running from > any
# mounted removable medium. Falls back to /tmp so the bundle always gets made.
find_dest() {
    if [ $# -ge 1 ] && [ -n "${1:-}" ]; then
        printf '%s' "$1"; return
    fi
    local self_dir
    self_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" 2>/dev/null && pwd)"
    # Running from the stick itself, and it is writable → use it.
    if [ -n "$self_dir" ] && [ -w "$self_dir" ] && \
       printf '%s' "$self_dir" | grep -qE '^/(run/media|media|mnt)/'; then
        printf '%s' "$self_dir"; return
    fi
    # Otherwise: first writable removable mount.
    local m
    for m in /run/media/*/* /media/*/* /mnt/*; do
        [ -d "$m" ] && [ -w "$m" ] && { printf '%s' "$m"; return; }
    done
    printf '%s' "/tmp"
}

DEST_ROOT="$(find_dest "${1:-}")"
OUT="$DEST_ROOT/igloo-logs-$HOST-$TS"
mkdir -p "$OUT" 2>/dev/null || { echo "ERROR: cannot write to $DEST_ROOT"; exit 1; }

say() { echo "  $*"; }
# run <file> <command...>  - capture stdout+stderr and the exit code
run() {
    local f="$OUT/$1"; shift
    { echo "\$ $*"; "$@" 2>&1; echo "[exit $?]"; } > "$f" 2>&1 || true
}
# grab <src> [name] - copy a file/dir if it exists, else note that it did not
grab() {
    local src="$1" name="${2:-$(basename "$1")}"
    if [ -e "$src" ]; then
        cp -a "$src" "$OUT/$name" 2>/dev/null && say "collected $src" && return
        say "FAILED to copy $src"
    else
        echo "$src : NOT PRESENT" >> "$OUT/_missing.txt"
        say "missing   $src (noted)"
    fi
}

echo "igloo-collect → $OUT"
echo

#   1. Igloo's own logs
echo "[1/6] Igloo logs"
grab /var/log/igloo                igloo-log-dir
grab /var/log/igloo-post.log       igloo-post.log          # Fedora kickstart %post
grab /var/log/igloo-install.log    igloo-install.log       # Debian/Mint install-time
grab /var/lib/igloo/.done          marker-done
grab /var/lib/igloo/.reboot-required marker-reboot-required

# Manifest, with secrets stripped. python3 when available (exact), sed otherwise.
if [ -f /var/lib/igloo/manifest.json ]; then
    if command -v python3 >/dev/null 2>&1; then
        python3 - "$OUT/redacted-manifest.json" <<'PY' 2>/dev/null || say "manifest redaction failed - NOT copied"
import json, sys
d = json.load(open("/var/lib/igloo/manifest.json", encoding="utf-8"))
d.get("user", {}).pop("linuxPassword", None)
for n in d.get("wifiNetworks", []):
    n.pop("psk", None)
json.dump(d, open(sys.argv[1], "w", encoding="utf-8"), indent=2)
PY
        [ -f "$OUT/redacted-manifest.json" ] && say "collected manifest (password + Wi-Fi keys removed)"
    else
        sed -E 's/("linuxPassword"|"psk")[[:space:]]*:[[:space:]]*"[^"]*"/\1: "<redacted>"/g' \
            /var/lib/igloo/manifest.json > "$OUT/redacted-manifest.json" 2>/dev/null \
            && say "collected manifest (redacted with sed)"
    fi
else
    echo "/var/lib/igloo/manifest.json : NOT PRESENT" >> "$OUT/_missing.txt"
    say "missing   manifest  <-- the first-boot agent is gated on this file"
fi

#   2. Installer logs
echo "[2/6] Installer logs"
grab /var/log/anaconda              anaconda            # Fedora
grab /var/log/installer             debian-installer    # Debian/Mint
grab /var/log/casper.log            casper.log

#   3. Boot + kernel + GPU  (the resolution/driver questions)
echo "[3/6] Kernel & GPU"
run  kernel-running.txt      uname -a
run  kernel-cmdline.txt      cat /proc/cmdline
run  kernel-packages.txt     rpm -qa kernel-core kernel-modules kernel-devel
run  kernel-entries.txt      grubby --info=ALL
run  lsmod-nvidia.txt        bash -c 'lsmod | grep -Ei "nvidia|nouveau" || echo "(no nvidia/nouveau module loaded)"'
run  nvidia-smi.txt          nvidia-smi
run  lspci-vga.txt           bash -c 'lspci -nnk | grep -A3 -Ei "vga|3d|display"'
run  nvidia-modules-built.txt bash -c 'for d in /lib/modules/*/; do echo "== $d"; ls "$d"extra/nvidia* 2>/dev/null || echo "  (no nvidia module for this kernel)"; done'
run  nvidia-macro.txt        cat /etc/rpm/macros.nvidia-kmod
run  akmods-log.txt          bash -c 'tail -n 200 /var/cache/akmods/*.log 2>/dev/null || echo "(no akmods build logs)"'
run  glxinfo.txt             bash -c 'glxinfo -B 2>/dev/null || echo "(glxinfo not installed)"'
run  resolution.txt          bash -c 'xrandr 2>/dev/null || kscreen-doctor -o 2>/dev/null || echo "(no X/kscreen tool available)"'

#   3b. Display layout migration (rotation / refresh rate / position)
# The cross-OS match is by EDID: Windows records a PnP id, Linux must find the same
# one on a connector. When rotation "did not apply", the answer is almost always
# here - either the manifest carried no displays, or the ids did not line up.
echo "[3b/6] Display layout"
run  display-agent-log.txt   bash -c 'grep -iE "display|monitor|output|pnp" /var/log/igloo/agent.log 2>/dev/null || echo "(no display lines in agent.log)"'
# NOTE: test the connector's "status", never [ -s edid ]. sysfs reports size 0 for these
# files whatever they contain, so a -s test silently skips every monitor on the machine.
run  display-connectors.txt  bash -c 'for c in /sys/class/drm/card*-*; do [ -r "$c/status" ] || continue; s=$(cat "$c/status" 2>/dev/null); echo "== $(basename "$c")  status=$s"; [ "$s" = connected ] || continue; (edid-decode "$c/edid" 2>/dev/null | grep -iE "manufacturer|model year|serial|Display Product Name") || xxd -p -l 32 "$c/edid" 2>/dev/null; done 2>&1 | head -80'
run  display-modes.txt       bash -c 'for m in /sys/class/drm/card*-*/modes; do [ -s "$m" ] || continue; echo "== $(basename "$(dirname "$m")")"; head -8 "$m"; done 2>&1'
run  display-monitors-xml.txt bash -c 'for h in /home/*/; do echo "== $h"; cat "$h.config/monitors.xml" 2>/dev/null || echo "(no monitors.xml for this user)"; done'
run  display-manifest.txt    bash -c 'python3 -c "import json;d=json.load(open(\"/var/lib/igloo/manifest.json\"));print(json.dumps(d.get(\"displays\",\"NO displays KEY\"),indent=2))" 2>/dev/null || echo "(manifest unreadable)"'

#   3c. Login-time display hook (the Cinnamon/X11 applier chain)
# The first-boot agent only stages this hook; it fires inside the user session,
# so its evidence lives in the user's home and /opt/igloo - NOT in the agent
# log. A missing display-apply.py here once cost a full debug round.
echo "[3c/6] Display login hook"
run  opt-igloo.txt           bash -c 'ls -la /opt/igloo/ 2>/dev/null || echo "(no /opt/igloo)"'
grab /opt/igloo/display-layout.json display-layout-staged.json
grab /etc/xdg/autostart/igloo-display-layout.desktop autostart-display-layout.desktop
run  display-hook-user.txt   bash -c 'for h in /home/*/; do echo "== $h"; cat "$h.local/state/igloo-display.log" 2>/dev/null || echo "(no igloo-display.log - the hook never ran)"; [ -f "$h.config/.igloo-display-done" ] && echo "done-marker: PRESENT" || echo "done-marker: absent"; done'
run  session-desktop.txt     bash -c 'echo "XDG_CURRENT_DESKTOP=${XDG_CURRENT_DESKTOP:-unset} XDG_SESSION_TYPE=${XDG_SESSION_TYPE:-unset}"; loginctl list-sessions --no-legend 2>/dev/null'
run  xrandr-verbose.txt      bash -c 'xrandr --verbose 2>/dev/null | head -150'

#   3e. Wallpaper migration
# The image lands in ~/Pictures and is set via dconf default (GNOME/Cinnamon)
# or a KDE login hook (plasma-apply-wallpaperimage, done-marker convention).
echo "[3e/6] Wallpaper"
run  wallpaper-user.txt      bash -c 'for h in /home/*/; do echo "== $h"; ls -la "$hPictures"/wallpaper.* 2>/dev/null || echo "(no wallpaper in Pictures)"; [ -f "$h.config/.igloo-wallpaper-done" ] && echo "wallpaper-done: PRESENT" || echo "wallpaper-done: absent"; done'
grab /etc/dconf/db/local.d/00-igloo-wallpaper dconf-igloo-wallpaper.txt
grab /etc/xdg/autostart/igloo-wallpaper.desktop autostart-wallpaper.desktop
run  wallpaper-live.txt      bash -c 'ls -la /opt/igloo/igloo-wallpaper.* 2>/dev/null || echo "(no wallpaper in /opt/igloo)"; gsettings get org.gnome.desktop.background picture-uri 2>/dev/null'

#   3d. Keyboard (GNOME dconf) + display second pass
# GNOME session layout comes from dconf input-sources, seeded via a system db;
# that db only counts when the profile names system-db:local. The display
# second pass is a one-shot service whose journal says whether EDID appeared.
echo "[3d/6] Keyboard & second pass"
grab /etc/dconf/profile/user dconf-profile-user.txt
grab /etc/dconf/db/local.d/00-igloo-keyboard dconf-igloo-keyboard.txt
run  keyboard-live.txt       bash -c 'cat /etc/default/keyboard 2>/dev/null; gsettings get org.gnome.desktop.input-sources sources 2>/dev/null'
run  second-pass.txt         bash -c 'systemctl status igloo-display-layout.service --no-pager 2>/dev/null; journalctl -b -u igloo-display-layout.service --no-pager 2>/dev/null; ls /var/lib/igloo/.display-done 2>/dev/null && echo "display-done: PRESENT" || echo "display-done: absent"'

#   4. Desktop session (the black-screen question)
echo "[4/6] Desktop session"
run  display-manager.txt     systemctl status display-manager --no-pager
run  default-target.txt      systemctl get-default
run  failed-units.txt        systemctl --failed --no-pager
run  igloo-service.txt       systemctl status igloo-first-boot.service igloo-bootstrap.service --no-pager
run  journal-igloo.txt       journalctl -b -u igloo-first-boot.service -u igloo-bootstrap.service --no-pager
run  journal-errors.txt      journalctl -b -p err --no-pager
run  selinux-state.txt       bash -c 'getenforce 2>/dev/null; ls -Zd /home/* 2>/dev/null'
run  selinux-denials.txt     bash -c 'ausearch -m avc -ts boot 2>/dev/null | tail -n 100 || journalctl -b --no-pager | grep -i avc | tail -n 100 || echo "(no denials found)"'
run  home-ownership.txt      bash -c 'ls -la /home/ 2>/dev/null; for h in /home/*/; do echo "== $h"; ls -la "$h" | head -20; done'

#   5. Storage + network (partitioning and Wi-Fi migration)
echo "[5/6] Storage & network"
run  lsblk.txt               lsblk -f
run  findmnt-root.txt        findmnt -rno SOURCE,FSTYPE,OPTIONS /
run  blkid.txt               blkid
run  lvm.txt                 bash -c 'vgs 2>/dev/null; lvs 2>/dev/null; pvs 2>/dev/null'
run  efibootmgr.txt          efibootmgr -v
run  os-release.txt          cat /etc/os-release
run  nm-connections.txt      bash -c 'ls -lZ /etc/NetworkManager/system-connections/ 2>/dev/null; nmcli -t -f NAME,TYPE,AUTOCONNECT connection show 2>/dev/null'
run  nm-status.txt           nmcli device status

#   6. Package/install summary
echo "[6/6] Package state"
run  dnf-history.txt         bash -c 'dnf history 2>/dev/null | head -40 || echo "(dnf not present)"'
run  nvidia-packages.txt     bash -c 'rpm -qa | grep -Ei "nvidia|akmod|kmod" || dpkg -l | grep -Ei "nvidia" || echo "(none)"'
run  repos.txt               bash -c 'dnf repolist 2>/dev/null || apt-cache policy 2>/dev/null'

#   Wrap up
{
    echo "igloo-collect bundle"
    echo "  host      : $HOST"
    echo "  collected : $TS (UTC)"
    echo "  kernel    : $(uname -r 2>/dev/null)"
    echo "  os        : $(. /etc/os-release 2>/dev/null && echo "$PRETTY_NAME")"
    echo
    echo "Secrets: the Linux password and Wi-Fi PSKs are stripped from"
    echo "redacted-manifest.json. Review before sharing."
} > "$OUT/_README.txt"

chmod -R a+rX "$OUT" 2>/dev/null
tar -czf "$OUT.tar.gz" -C "$DEST_ROOT" "$(basename "$OUT")" 2>/dev/null \
    && echo && echo "Archive : $OUT.tar.gz"

# Install the short command for next time.
if [ -w /usr/local/bin ] || [ "$(id -u)" = "0" ]; then
    cp -f "${BASH_SOURCE[0]}" /usr/local/bin/igloo-logs 2>/dev/null && \
        chmod 0755 /usr/local/bin/igloo-logs 2>/dev/null && \
        echo "Shortcut: installed - next time just run  sudo igloo-logs"
fi

sync
echo
echo "Folder  : $OUT"
echo "Done. Safe to unplug the stick once this line is on screen."
