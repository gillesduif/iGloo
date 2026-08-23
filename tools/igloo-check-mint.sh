#!/usr/bin/env bash
# =============================================================================
# igloo-check-mint - verdict sheet for the Linux Mint bare-metal test
# -----------------------------------------------------------------------------
#     sudo bash igloo-check-mint.sh
#
# Answers the questions this particular test exists to answer, and says OK or
# FOUT per item instead of printing output to be interpreted. Everything is
# read-only: nothing here changes the machine.
#
# For the full forensic bundle use igloo-collect.sh - this is the quick verdict.
# =============================================================================
set -u

pass=0; fail=0; unknown=0

ok()   { printf '  \033[32mOK\033[0m    %s\n' "$1"; pass=$((pass+1)); }
bad()  { printf '  \033[31mFOUT\033[0m  %s\n' "$1"; fail=$((fail+1)); }
huh()  { printf '  \033[33m?\033[0m     %s\n' "$1"; unknown=$((unknown+1)); }
note() { printf '        %s\n' "$1"; }
head2() { printf '\n\033[1m%s\033[0m\n' "$1"; }

# Running under sudo, $HOME is root's. The migrated files live in the real
# user's home, so resolve that once and use it everywhere below.
USER_NAME="${SUDO_USER:-${USER:-$(id -un)}}"
USER_HOME="$(getent passwd "$USER_NAME" 2>/dev/null | cut -d: -f6)"
[ -n "$USER_HOME" ] || USER_HOME="/home/$USER_NAME"

echo "==============================================================="
echo " iGloo - Mint testcontrole"
echo " gebruiker : $USER_NAME    home: $USER_HOME"
echo " datum     : $(date '+%F %T')"
echo "==============================================================="

#   1. Welke build zit erin
head2 "1. Zit de nieuwe agent erin?"
AGENT=/opt/igloo/agent.py
if [ ! -f "$AGENT" ]; then
    bad "$AGENT ontbreekt - de agent is nooit gestaged"
else
    note "$(ls -la "$AGENT" | awk '{print $5" bytes, "$6" "$7" "$8}')"
    for marker in normalise_gecko_profiles:"Firefox-profiel" \
                  _clear_windows_install_paths:"compatibility.ini blijft staan" \
                  ensure_matching_firefox:"Firefox-versiecontrole" \
                  _chromium_config_homes:"Flatpak-pad voor Chromium" \
                  _copy_chromium_profile:"Chromium bookmarks/history/cookies" \
                  fix-boot-order:"--fix-boot-order"; do
        key="${marker%%:*}"; label="${marker#*:}"
        if grep -q -- "$key" "$AGENT" 2>/dev/null; then
            ok "$label"
        else
            bad "$label ontbreekt - oude build"
        fi
    done
fi

HOOK=/opt/igloo/display-apply.sh
if [ ! -f "$HOOK" ]; then
    huh "$HOOK ontbreekt - display-hook niet gestaged"
elif grep -q 'DONE_MARKER" \] && exit 0' "$HOOK"; then
    bad "display-hook draait maar EEN keer - oude build"
else
    ok "display-hook draait bij elke login"
fi

#   2. GRUB standaardkeuze - hier zat de bug
head2 "2. Blijft Windows plakken als standaardkeuze?"
GRUBCFG="$(cat /etc/default/grub /etc/default/grub.d/*.cfg 2>/dev/null)"
if echo "$GRUBCFG" | grep -q "GRUB_SAVEDEFAULT=true"; then
    bad "GRUB_SAVEDEFAULT=true staat er nog - oude build, Windows blijft plakken"
elif echo "$GRUBCFG" | grep -q "GRUB_DEFAULT=0"; then
    ok "GRUB_DEFAULT=0, geen SAVEDEFAULT - dit systeem blijft de standaard"
else
    huh "geen GRUB_DEFAULT=0 gevonden"
    note "$(echo "$GRUBCFG" | grep -E 'GRUB_DEFAULT|SAVEDEFAULT' | tr '\n' ' ')"
fi

SAVED="$( { grub-editenv list || grub2-editenv list; } 2>/dev/null | grep '^saved_entry=' )"
if [ -n "$SAVED" ]; then
    bad "grubenv bevat nog $SAVED"
else
    ok "grubenv heeft geen saved_entry"
fi

#   2b. Kernelnamen in het menu - hier hing Fedora op
head2 "2b. Booten de andere systemen op UUID?"
GCFG=/boot/grub/grub.cfg
if [ ! -f "$GCFG" ]; then
    huh "$GCFG ontbreekt"
else
    STALE="$(grep -o 'root=/dev/[^ ]*' "$GCFG" 2>/dev/null | sort -u | tr '\n' ' ')"
    if [ -n "$STALE" ]; then
        bad "entries booten een kernelnaam: $STALE"
        note "die naam volgt de sondeervolgorde - hierop bleef Fedora hangen"
        grep -n 'root=/dev/' "$GCFG" | sed 's/^/        /'
    else
        ok "elke entry noemt zijn root met UUID"
    fi
fi
FIXHOOK=/etc/kernel/postinst.d/zzz-igloo-grub-fixups
[ -x "$FIXHOOK" ] && ok "fixup-hook staat er - geldt ook na kernel-updates" \
                  || bad "$FIXHOOK ontbreekt - oude build"

#   3. UEFI-bootvolgorde
head2 "3. Staat dit systeem vooraan in de UEFI-volgorde?"
if [ ! -d /sys/firmware/efi ]; then
    huh "geen UEFI - deze machine boot legacy, hele vraag is niet van toepassing"
else
    EFI="$(efibootmgr 2>/dev/null)"
    CUR="$(echo "$EFI" | sed -n 's/^BootCurrent: *//p')"
    ORD="$(echo "$EFI" | sed -n 's/^BootOrder: *//p')"
    FIRST="${ORD%%,*}"
    note "BootCurrent=$CUR  BootOrder=$ORD"
    if [ -n "$CUR" ] && [ "$CUR" = "$FIRST" ]; then
        ok "we booten uit de entry die vooraan staat"
    else
        bad "we booten uit Boot$CUR maar Boot$FIRST staat vooraan"
    fi

    case "$(systemctl is-enabled igloo-boot-order.service 2>/dev/null)" in
        enabled) ok "igloo-boot-order.service is ingeschakeld" ;;
        "")      bad "igloo-boot-order.service bestaat niet - oude build" ;;
        *)       bad "igloo-boot-order.service is niet ingeschakeld" ;;
    esac
    if systemctl is-failed igloo-boot-order.service >/dev/null 2>&1; then
        bad "igloo-boot-order.service is gefaald"
        note "$(systemctl status igloo-boot-order.service --no-pager 2>&1 | tail -4)"
    fi
fi

#   4. Restanten van de installer
head2 "4. Is de installer opgeruimd?"
if [ -f /var/lib/igloo/.had-failures ]; then
    bad "/var/lib/igloo/.had-failures bestaat - opruiming is BEWUST overgeslagen"
    note "een eerdere stap faalde; zie /var/log/igloo/agent.log"
else
    ok "geen .had-failures - de opruiming mocht doorgaan"
fi
LEFT="$(lsblk -no LABEL 2>/dev/null | grep -iE 'oemdrv|igloo' | tr '\n' ' ')"
[ -n "$LEFT" ] && bad "installer-partitie staat er nog: $LEFT" || ok "geen installer-partitie meer"
if [ -d /sys/firmware/efi ]; then
    STALE="$(efibootmgr 2>/dev/null | grep -i igloo)"
    [ -n "$STALE" ] && { bad "dangling boot-entries:"; note "$STALE"; } \
                    || ok "geen iGloo boot-entries meer"
fi

#   5. Beeldscherm - Cinnamon gebruikt xrandr, niet D-Bus
head2 "5. Beeldschermlayout (Cinnamon)"
note "desktop = ${XDG_CURRENT_DESKTOP:-onbekend via sudo, kijk in de log hieronder}"
for f in monitors.xml cinnamon-monitors.xml; do
    if [ -f "$USER_HOME/.config/$f" ]; then
        ok "$f aanwezig ($(grep -c '<configuration>' "$USER_HOME/.config/$f") configuratie(s))"
    else
        huh "$f ontbreekt"
    fi
done
DLOG="$USER_HOME/.local/state/igloo-display.log"
if [ -f "$DLOG" ]; then
    RUNS="$(grep -c 'start:' "$DLOG")"
    if [ "$RUNS" -gt 1 ]; then
        ok "hook is $RUNS keer gedraaid - dus bij elke login"
    else
        huh "hook is $RUNS keer gedraaid - log uit en weer in om dit te bevestigen"
    fi
    note "laatste regels:"
    tail -5 "$DLOG" | sed 's/^/        /'
else
    bad "geen igloo-display.log - de hook heeft nooit gedraaid"
fi

#   6. Browsers
head2 "6. Browsermigratie"
FF_NATIVE="$USER_HOME/.mozilla/firefox"
FF_FLAT="$USER_HOME/.var/app/org.mozilla.firefox/.mozilla/firefox"
for d in "$FF_NATIVE" "$FF_FLAT"; do
    [ -d "$d" ] && note "profiel: $d  ($(du -sh "$d" 2>/dev/null | cut -f1))"
done
if [ -d "$FF_FLAT" ] && [ -d "$FF_NATIVE" ]; then
    bad "twee Firefoxen - op Mint hoort dat NIET, die levert al het release-kanaal"
elif [ -d "$FF_FLAT" ]; then
    huh "alleen de Flatpak-Firefox - onverwacht op Mint"
elif [ -d "$FF_NATIVE" ]; then
    ok "een Firefox, de meegeleverde - correct voor Mint"
else
    huh "geen Firefox-profiel gevonden - is Firefox aangevinkt bij de migratie?"
fi
for d in "$FF_NATIVE" "$FF_FLAT"; do
    [ -f "$d/profiles.ini" ] || continue
    grep -q '^\[Install' "$d/profiles.ini" \
        && bad "$d/profiles.ini heeft nog een [Install]-sectie" \
        || ok "profiles.ini genormaliseerd ($(basename "$(dirname "$d")"))"
done

for rel in .config/BraveSoftware/Brave-Browser .var/app/com.brave.Browser/config/BraveSoftware/Brave-Browser; do
    D="$USER_HOME/$rel/Default"
    [ -d "$D" ] || continue
    FOUND=""
    for f in Bookmarks History Favicons "Network/Cookies" "Login Data"; do
        [ -e "$D/$f" ] && FOUND="$FOUND $f"
    done
    [ -n "$FOUND" ] && ok "Brave:$FOUND" || huh "Brave-map leeg: $D"
done

CLOG="$USER_HOME/.local/state/igloo-credentials.log"
[ -f "$CLOG" ] && { note "wachtwoord-import:"; tail -3 "$CLOG" | sed 's/^/        /'; } \
               || huh "geen igloo-credentials.log - login-hook nog niet gedraaid"

flatpak list --app --columns=application 2>/dev/null | sed 's/^/        /' | head -12

#   Samenvatting
echo
echo "==============================================================="
printf " OK: %d   FOUT: %d   onbekend: %d\n" "$pass" "$fail" "$unknown"
if [ "$fail" -gt 0 ]; then
    echo " Er staan fouten hierboven. Draai igloo-collect.sh voor de bundel."
fi
echo "==============================================================="
