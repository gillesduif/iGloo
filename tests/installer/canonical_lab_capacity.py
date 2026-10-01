"""Host provisioning capacity only; never ownership or execution authority."""
import json
import os
from pathlib import Path
import platform
import subprocess


def require_backing_capacity(required):
    if type(required) is not int or not 0 < required <= 2**63 - 1:
        raise ValueError('InvalidCapacityRequirement')
    if 'microsoft' not in platform.release().lower():
        return None  # Native Linux still uses the existing filesystem checks.
    distro = os.environ.get('WSL_DISTRO_NAME')
    if not distro:
        raise ValueError('WslDistributionUnavailable')
    script = Path(__file__).with_name('Test-CanonicalLabHostCapacity.ps1')
    mapped = subprocess.run(['/usr/bin/wslpath', '-w', str(script.resolve())],
        capture_output=True, check=True, timeout=30).stdout.decode().strip()
    # This bounded host-side lab supports the existing C: Windows installation.
    # An unavailable executable/mapping is failure, never permission to skip.
    result = subprocess.run(['/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe',
        '-NoProfile', '-NonInteractive', '-File', mapped, '-DistributionName', distro,
        '-RequiredAdditionalBytes', str(required)], capture_output=True, timeout=30, check=False)
    if len(result.stdout) > 8192:
        raise ValueError('HostCapacityObservationOversize')
    try:
        observed = json.loads(result.stdout.decode('utf-8-sig'))
    except (ValueError, UnicodeError):
        raise ValueError('HostCapacityObservationUnavailable') from None
    if (result.returncode != 0 or observed.get('SchemaVersion') != 1 or
            observed.get('Scope') != 'HostCapacityObservationOnly' or
            observed.get('Distribution') != distro or
            observed.get('RequiredAdditionalBytes') != required or
            type(observed.get('AvailableBytes')) is not int or observed['AvailableBytes'] < required or
            observed.get('Sufficient') is not True or observed.get('NativeMutationAuthorized') is not False):
        raise ValueError('HostBackingCapacityInsufficientOrUnavailable')
    return observed
