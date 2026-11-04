#!/usr/bin/env bash
# Disposable, file-backed probe for the reviewed Linux ESP loader/config path.
# It never writes a host block device or changes firmware variables on the host.
if [[ ${BASH_SOURCE[0]} != "$0" ]]; then
  echo "Execute this probe explicitly; do not source it." >&2
  return 2
fi
set -euo pipefail

usage() {
  echo "usage: $0 prepare|run|run-prefix|run-handoff|run-decoy|run-duplicate|run-secureboot PINNED_ISO ISO_SHA256 OVMF_CODE OVMF_VARS UEFI_SHELL NEW_OUTPUT_DIR [SOURCE_TRAMPOLINE]" >&2
  exit 2
}

(( $# >= 7 )) || usage
mode=$1
case $mode in
  prepare|run|run-prefix) (( $# == 7 )) || usage ;;
  run-handoff|run-decoy|run-duplicate|run-secureboot) (( $# == 8 )) || usage ;;
  *) usage ;;
esac
iso=$2
expected_iso_sha=${3,,}
ovmf_code=$4
ovmf_vars=$5
uefi_shell=$6
output=$7
source_trampoline=${8:-}
[[ $expected_iso_sha =~ ^[0-9a-f]{64}$ ]] || usage
if [[ $mode == run-secureboot && -z $source_trampoline ]]; then
  echo "Secure Boot mode requires the source-generated installer trampoline." >&2
  exit 2
fi
for input in "$iso" "$ovmf_code" "$ovmf_vars" "$uefi_shell"; do
  [[ $input == /* && -f $input && ! -L $input ]] || {
    echo "Inputs must be absolute regular files, not symlinks: $input" >&2
    exit 2
  }
done
if [[ -n $source_trampoline ]]; then
  [[ $source_trampoline == /* && -f $source_trampoline && ! -L $source_trampoline ]] || {
    echo "The source-generated trampoline must be an absolute regular file, not a symlink." >&2
    exit 2
  }
fi
[[ $output == /* && ! -e $output && ! -L $output ]] || {
  echo "Choose a new absolute output directory." >&2
  exit 2
}

for tool in sha256sum xorriso sgdisk mkfs.vfat mmd mcopy mdir qemu-system-x86_64 \
  timeout df stat awk grep realpath truncate cmp cp rm python3; do
  command -v "$tool" >/dev/null || {
    echo "Missing required lab tool: $tool" >&2
    exit 2
  }
done
for path in "$ovmf_code" "$ovmf_vars" "$output"; do
  [[ $path != *,* ]] || {
    echo "QEMU lab paths cannot contain a comma." >&2
    exit 2
  }
done

# This is an Ubuntu WSL lab. Its virtual ext4 free-space report alone does not
# protect the Windows host backing the VHD. Keep the existing host floor.
[[ -d /mnt/c ]] || {
  echo "The Windows C: capacity mount is unavailable." >&2
  exit 2
}
host_free_kib=$(df -Pk /mnt/c | awk 'END {print $4}')
output_parent=$(realpath -e -- "$(dirname -- "$output")")
output_free_kib=$(df -Pk "$output_parent" | awk 'END {print $4}')
if (( host_free_kib < 21 * 1024 * 1024 + 512 * 1024 ||
      output_free_kib < 512 * 1024 )); then
  echo "The lab needs at least 512 MiB above the 21 GiB Windows host floor and 512 MiB in the output filesystem." >&2
  exit 3
fi

actual_iso_sha=$(sha256sum -- "$iso" | awk '{print $1}')
[[ $actual_iso_sha == "$expected_iso_sha" ]] || {
  echo "The installer ISO differs from the exact SHA-256 pin." >&2
  exit 3
}

mkdir -- "$output"
disk="$output/esp.raw"
serial="$output/serial.log"
startup="$output/startup.nsh"
config="$output/grub.cfg"
prefix_config="$output/compiled-prefix-grub.cfg"
shim="$output/shimx64.efi"
grub="$output/grubx64.efi"

# Match ReviewedSeedFileWriter.FindInstallerEfiPair's ordered source choices.
# Its ISO9660 reader ignores case; xorriso extraction needs the recorded case.
pair_source=''
if [[ $mode == run-secureboot ]]; then
  pair_candidates=(
    'EFI/BOOT/BOOTX64.EFI|EFI/BOOT/grubx64.efi'
    'EFI/boot/bootx64.efi|EFI/boot/grubx64.efi'
  )
else
  pair_candidates=(
    'EFI/fedora/shimx64.efi|EFI/fedora/grubx64.efi'
    'EFI/debian/shimx64.efi|EFI/debian/grubx64.efi'
    'EFI/ubuntu/shimx64.efi|EFI/ubuntu/grubx64.efi'
    'EFI/BOOT/shimx64.efi|EFI/BOOT/grubx64.efi'
    'EFI/BOOT/BOOTX64.EFI|EFI/BOOT/grubx64.efi'
    'EFI/boot/bootx64.efi|EFI/boot/grubx64.efi'
  )
fi
for pair in "${pair_candidates[@]}"; do
  shim_member=${pair%%|*}
  grub_member=${pair#*|}
  if xorriso -osirrox on -indev "$iso" \
      -extract "/$shim_member" "$shim" \
      -extract "/$grub_member" "$grub" >"$output/extract.log" 2>&1; then
    pair_source=$pair
    break
  fi
  rm -f -- "$shim" "$grub"
done
[[ -n $pair_source && -s $shim && -s $grub ]] || {
  echo "No reviewed shim/GRUB pair could be extracted from the pinned ISO." >&2
  exit 3
}
prefix_config_path=/boot/grub/grub.cfg
if [[ ${pair_source,,} == efi/boot/* ]]; then
  prefix_config_path=/EFI/BOOT/grub.cfg
fi
if [[ $mode == run-secureboot && $prefix_config_path != /EFI/BOOT/grub.cfg ]]; then
  echo "Secure Boot mode requires a fallback-path shim and GRUB pair." >&2
  exit 3
fi
[[ $(sha256sum -- "$iso" | awk '{print $1}') == "$expected_iso_sha" ]] || {
  echo "The installer ISO changed while its EFI files were extracted." >&2
  exit 3
}

# An externally generated trampoline must come from the built reviewed source.
# The simple modes use distinct markers to identify which ESP config GRUB reads.
probe_esp_guid=63be74c0-9b45-4d55-8c51-88a400ec82d0
probe_decoy_guid=6da1112a-1381-4655-984d-f7d54ca3c952
probe_seed_guid=970e806a-56b2-4a36-9a33-39b7068fc680
if [[ -n $source_trampoline ]]; then
  grep -Fq "$probe_esp_guid" "$source_trampoline"
  grep -Fq "$probe_seed_guid" "$source_trampoline"
  grep -Fq 'configfile ($root)/EFI/BOOT/grub.cfg' "$source_trampoline"
  cp -- "$source_trampoline" "$config"
else
  primary_marker=IGLOO_REVIEWED_PREFIX_CONFIG_LOADED
  if [[ $mode == run-prefix ]]; then
    primary_marker=IGLOO_PRIMARY_CONFIG_LOADED
  fi
  cat >"$config" <<GRUB
serial --unit=0 --speed=115200
terminal_output serial
echo $primary_marker
sleep --verbose --interruptible 5
GRUB
  if [[ $mode == run-prefix ]]; then
    cat >"$prefix_config" <<'GRUB'
serial --unit=0 --speed=115200
terminal_output serial
echo IGLOO_COMPILED_PREFIX_CONFIG_LOADED
sleep --verbose --interruptible 5
GRUB
  fi
fi
cat >"$startup" <<'SHELL'
echo -off
fs0:\EFI\iGloo\shimx64.efi
SHELL

if [[ $mode == run-secureboot ]]; then
  secureboot_state=$(python3 - "$ovmf_vars" <<'PY'
from pathlib import Path
import sys

data = Path(sys.argv[1]).read_bytes()
name = "SecureBootEnable".encode("utf-16le")
offset = data.find(name)
if offset < 0 or offset + 34 >= len(data):
    raise SystemExit("The OVMF variable template does not expose SecureBootEnable.")
print(data[offset + 34])
PY
  )
  if [[ $secureboot_state != 1 ]]; then
    echo "Secure Boot mode requires an OVMF variable template with SecureBootEnable=1." >&2
    exit 3
  fi
fi

# The shell is a fallback launcher; startup.nsh invokes the reviewed exact
# shim path. All disks here are new sparse files, never host block devices.
if [[ -n $source_trampoline ]]; then
  # ESP, earlier decoy, then exact seed. The decoy-only mode forces the wrong
  # candidate; the duplicate mode records either safe search outcome.
  truncate -s 160M -- "$disk"
  sgdisk --clear --new=1:2048:+48M --typecode=1:EF00 \
    --partition-guid=1:"$probe_esp_guid" --change-name=1:IGLOO_PROBE_ESP \
    --new=2:100352:+48M --typecode=2:0700 \
    --partition-guid=2:"$probe_decoy_guid" --change-name=2:IGLOO_DECOY \
    --new=3:198656:+48M --typecode=3:0700 \
    --partition-guid=3:"$probe_seed_guid" --change-name=3:IGLOO_SEED \
    "$disk" >"$output/gpt.log"
  mkfs.vfat -F 32 -n IGLOOPROBE --offset=2048 "$disk" 98304 \
    >"$output/fat.log" 2>&1
  mkfs.vfat -F 32 -n IGLOODECOY --offset=100352 "$disk" 98304 \
    >"$output/decoy-fat.log" 2>&1
  mkfs.vfat -F 32 -n OEMDRV --offset=198656 "$disk" 98304 \
    >"$output/seed-fat.log" 2>&1
else
  truncate -s 128M -- "$disk"
  sgdisk --clear --new=1:2048:+96M --typecode=1:EF00 \
    --change-name=1:IGLOO_PROBE_ESP "$disk" >"$output/gpt.log"
  mkfs.vfat -F 32 -n IGLOOPROBE --offset=2048 "$disk" 98304 \
    >"$output/fat.log" 2>&1
fi
fat_image="$disk@@1048576"
mmd -i "$fat_image" ::/EFI ::/EFI/iGloo ::/EFI/BOOT
mcopy -i "$fat_image" "$shim" ::/EFI/iGloo/shimx64.efi
mcopy -i "$fat_image" "$grub" ::/EFI/iGloo/grubx64.efi
# For run-prefix, omit the sibling config so the compiled-prefix candidate is
# actually isolated. The normal product path writes both files.
if [[ $mode != run-prefix ]]; then
  mcopy -i "$fat_image" "$config" ::/EFI/iGloo/grub.cfg
fi
if [[ $mode != prepare && $mode != run ]]; then
  if [[ $prefix_config_path == /boot/grub/grub.cfg ]]; then
    mmd -i "$fat_image" ::/boot ::/boot/grub
  fi
  prefix_source=$config
  if [[ $mode == run-prefix ]]; then prefix_source=$prefix_config; fi
  mcopy -i "$fat_image" "$prefix_source" "::$prefix_config_path"
fi
if [[ $mode == run-secureboot ]]; then
  # Debian's signed EFI GRUB keeps its build-time /boot/grub prefix even when
  # shim starts it from the removable-media fallback directory. Mirror the
  # reviewed trampoline there so both GRUB config lookup paths are covered.
  mmd -i "$fat_image" ::/boot ::/boot/grub
  mcopy -i "$fat_image" "$config" ::/boot/grub/grub.cfg
  mcopy -i "$fat_image" ::/boot/grub/grub.cfg "$output/readback-compiled-prefix-grub.cfg"
  cmp -- "$config" "$output/readback-compiled-prefix-grub.cfg"
  # Secure Boot must enter through the signed shim. An unsigned UEFI Shell
  # fallback cannot launch startup.nsh while firmware signature enforcement is on.
  mcopy -i "$fat_image" "$shim" ::/EFI/BOOT/BOOTX64.EFI
  mcopy -i "$fat_image" "$grub" ::/EFI/BOOT/grubx64.efi
else
  mcopy -i "$fat_image" "$uefi_shell" ::/EFI/BOOT/BOOTX64.EFI
  mcopy -i "$fat_image" "$startup" ::/startup.nsh
fi
mcopy -i "$fat_image" ::/EFI/iGloo/shimx64.efi "$output/readback-shimx64.efi"
mcopy -i "$fat_image" ::/EFI/iGloo/grubx64.efi "$output/readback-grubx64.efi"
if [[ $mode != run-prefix ]]; then
  mcopy -i "$fat_image" ::/EFI/iGloo/grub.cfg "$output/readback-grub.cfg"
fi
fallback="$output/readback-UEFI-shell.efi"
fallback_source=$uefi_shell
if [[ $mode == run-secureboot ]]; then
  fallback="$output/readback-UEFI-shim.efi"
  fallback_source=$shim
fi
mcopy -i "$fat_image" ::/EFI/BOOT/BOOTX64.EFI "$fallback"
if [[ $mode != run-secureboot ]]; then
  mcopy -i "$fat_image" ::/startup.nsh "$output/readback-startup.nsh"
fi
cmp -- "$shim" "$output/readback-shimx64.efi"
cmp -- "$grub" "$output/readback-grubx64.efi"
if [[ $mode != run-prefix ]]; then
  cmp -- "$config" "$output/readback-grub.cfg"
fi
cmp -- "$fallback_source" "$fallback"
if [[ $mode != run-secureboot ]]; then
  cmp -- "$startup" "$output/readback-startup.nsh"
fi
if [[ $mode != prepare && $mode != run ]]; then
  mcopy -i "$fat_image" "::$prefix_config_path" "$output/readback-prefix-grub.cfg"
  cmp -- "$prefix_source" "$output/readback-prefix-grub.cfg"
fi
if [[ -n $source_trampoline ]]; then
  decoy_image="$disk@@51380224"
  seed_image="$disk@@101711872"
  identity="$output/igloo-seed-identity.json"
  seed_config="$output/seed-grub.cfg"
  printf '{"probeSeedPartuuid":"%s"}\n' "$probe_seed_guid" >"$identity"
  cat >"$seed_config" <<'GRUB'
serial --unit=0 --speed=115200
terminal_output serial
probe --set=igloo_loaded_partuuid --part-uuid ($root)
echo IGLOO_SEED_CONFIG_LOADED
echo IGLOO_SEED_PARTUUID=$igloo_loaded_partuuid
sleep --verbose --interruptible 5
GRUB
  mmd -i "$seed_image" ::/EFI ::/EFI/BOOT
  if [[ $mode != run-decoy ]]; then
    mcopy -i "$seed_image" "$identity" ::/igloo-seed-identity.json
    mcopy -i "$seed_image" ::/igloo-seed-identity.json "$output/readback-seed-identity.json"
    cmp -- "$identity" "$output/readback-seed-identity.json"
  fi
  mcopy -i "$seed_image" "$seed_config" ::/EFI/BOOT/grub.cfg
  mcopy -i "$seed_image" ::/EFI/BOOT/grub.cfg "$output/readback-seed-grub.cfg"
  cmp -- "$seed_config" "$output/readback-seed-grub.cfg"
  if [[ $mode == run-decoy || $mode == run-duplicate ]]; then
    mcopy -i "$decoy_image" "$identity" ::/igloo-seed-identity.json
    mcopy -i "$decoy_image" ::/igloo-seed-identity.json "$output/readback-decoy-identity.json"
    cmp -- "$identity" "$output/readback-decoy-identity.json"
  fi
fi
sgdisk --verify "$disk" >"$output/gpt-verify.log"
mdir -i "$fat_image" ::/EFI/iGloo >"$output/fat-readback.log"

{
  printf 'iso_sha256=%s\n' "$actual_iso_sha"
  printf 'selected_iso_pair=%s\n' "$pair_source"
  printf 'compiled_prefix_config=%s\n' "$prefix_config_path"
  for input in "$shim" "$grub" "$config" "$ovmf_code" "$ovmf_vars"; do
    sha256sum -- "$input"
  done
  if [[ $mode != run-secureboot ]]; then
    sha256sum -- "$uefi_shell"
  fi
  if [[ -n $source_trampoline ]]; then
    sha256sum -- "$source_trampoline" "$seed_config" "$identity"
    printf 'esp_partuuid=%s\nseed_partuuid=%s\ndecoy_partuuid=%s\n' \
      "$probe_esp_guid" "$probe_seed_guid" "$probe_decoy_guid"
  fi
  printf 'disk_bytes=%s\n' "$(stat -c %s -- "$disk")"
  printf 'mode=%s\n' "$mode"
} >"$output/inputs.txt"

if [[ $mode == prepare ]]; then
  echo "Prepared exact-ISO, file-backed ESP; QEMU was not started: $output"
  exit 0
fi

cp -- "$ovmf_vars" "$output/OVMF_VARS.fd"
disk_sha_before=$(sha256sum -- "$disk" | awk '{print $1}')
set +e
snapshot_arg=()
if [[ $mode == run-secureboot ]]; then snapshot_arg=(-snapshot); fi
timeout --signal=TERM --kill-after=5s 45s qemu-system-x86_64 "${snapshot_arg[@]}" \
  -machine q35 -accel tcg -m 512M -smp 1 -net none \
  -drive "if=pflash,format=raw,readonly=on,file=$ovmf_code" \
  -drive "if=pflash,format=raw,file=$output/OVMF_VARS.fd" \
  -drive "if=ide,format=raw,file=$disk" \
  -serial "file:$serial" -display none -monitor none \
  >"$output/qemu.log" 2>&1
qemu_status=$?
set -e
printf 'qemu_exit=%s\n' "$qemu_status" >>"$output/inputs.txt"
if [[ $mode == run-secureboot ]]; then
  disk_sha_after=$(sha256sum -- "$disk" | awk '{print $1}')
  printf 'secureboot_enable=%s\nqemu_snapshot=true\nesp_sha256_before=%s\nesp_sha256_after=%s\n' \
    "$secureboot_state" "$disk_sha_before" "$disk_sha_after" >>"$output/inputs.txt"
  [[ $disk_sha_before == "$disk_sha_after" ]] || {
    echo "The read-only Secure Boot probe changed its ESP base image." >&2
    exit 4
  }
fi
if [[ -f $serial ]]; then
  case $mode in
    run)
      if grep -Fq 'IGLOO_REVIEWED_PREFIX_CONFIG_LOADED' "$serial"; then
        echo "OVMF loaded the exact shim path and GRUB read the reviewed config path: $output"
        exit 0
      fi
      ;;
    run-prefix)
      if grep -Fq 'IGLOO_COMPILED_PREFIX_CONFIG_LOADED' "$serial" &&
          ! grep -Fq 'IGLOO_PRIMARY_CONFIG_LOADED' "$serial"; then
        echo "OVMF loaded the exact shim path and GRUB read its compiled prefix path: $output"
        exit 0
      fi
      ;;
    run-handoff)
      if grep -Fq 'IGLOO_SEED_CONFIG_LOADED' "$serial" &&
          grep -Fiq "IGLOO_SEED_PARTUUID=$probe_seed_guid" "$serial"; then
        echo "The source trampoline selected the exact seed PARTUUID and loaded its config: $output"
        exit 0
      fi
      ;;
    run-secureboot)
      if grep -Fq 'IGLOO_SEED_CONFIG_LOADED' "$serial" &&
          grep -Fiq "IGLOO_SEED_PARTUUID=$probe_seed_guid" "$serial"; then
        echo "Secure Boot accepted the ISO shim and GRUB, then loaded the exact seed PARTUUID: $output"
        exit 0
      fi
      ;;
    run-decoy)
      if grep -Fq 'The installer seed partition identity changed.' "$serial" &&
          ! grep -Fq 'IGLOO_SEED_CONFIG_LOADED' "$serial"; then
        echo "The source trampoline rejected the earlier decoy seed: $output"
        exit 0
      fi
      ;;
    run-duplicate)
      if { grep -Fq 'The installer seed partition identity changed.' "$serial" &&
          ! grep -Fq 'IGLOO_SEED_CONFIG_LOADED' "$serial"; } ||
          { grep -Fq 'IGLOO_SEED_CONFIG_LOADED' "$serial" &&
            grep -Fiq "IGLOO_SEED_PARTUUID=$probe_seed_guid" "$serial" &&
            ! grep -Fq 'The installer seed partition identity changed.' "$serial"; }; then
        echo "The source trampoline accepted only the exact seed or rejected the duplicate decoy: $output"
        exit 0
      fi
      ;;
  esac
fi
echo "The expected exact-path, seed handoff, or decoy guard was not observed; inspect serial.log and qemu.log." >&2
exit 1
