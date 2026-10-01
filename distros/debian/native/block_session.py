"""Persistent trusted mount session; ownership decisions come from the shared .NET resolver.

Private inherited pipes only. No CLI device/path selection, labels, partition writers,
package commands, credentials or firmware operations. Not yet a qualified stage host.
"""
import fcntl
import json
import os
from pathlib import Path
import stat
import subprocess
import sys
import uuid

from isolation_policy import ENVIRONMENT, canonical, digest, require
from isolation_observer import mount_records, namespace_ids
from mount_supervisor import BlockLease, ExactMountSupervisor
from package_broker import DirectoryLease, Runtime
import session_import
import session_configuration

LIMIT = 8 * 1024 * 1024


def read_message(stream):
    raw = stream.readline(LIMIT + 1)
    require(raw.endswith(b"\n") and len(raw) <= LIMIT, "SessionMessageMissingOrOversized")
    def pairs(items):
        result = {}
        for key, value in items:
            require(key not in result, "DuplicateSessionProperty")
            result[key] = value
        return result
    return json.loads(raw, object_pairs_hook=pairs)


class AuthorityChannel:
    def __init__(self, declaration, source, sink):
        self.declaration, self.source, self.sink = declaration, source, sink
        self.action = None
        self.observation_sequence = 0

    def ask(self, kind, **value):
        challenge = str(uuid.uuid4())
        request = {"SessionId": self.declaration["SessionId"], "GenerationId": self.declaration["GenerationId"],
                   "PlanSha256": self.declaration["PlanSha256"], "Action": self.action,
                   "Challenge": challenge, "Kind": kind, **value}
        self.sink.write(canonical(request) + b"\n"); self.sink.flush()
        response = read_message(self.source)
        require(response.get("Challenge") == challenge and response.get("Accepted") is True, "CanonicalAuthorityRejected")
        return response

    def checkpoint(self, record):
        self.ask("Checkpoint", Record=record)


def numbers_for(inventory):
    """Read stat for the WHOLE inventory. No open for reading/writing any preserved block."""
    paths = sorted(row["devicePath"] for key in ("disks", "partitions", "externalFileSystems") for row in inventory[key])
    require(len(set(paths)) == len(paths), "DuplicateInventoryLocator")
    result = []
    for path in paths:
        require(path.startswith("/dev/") and os.path.realpath(path, strict=True) == path, "BlockLocatorSymlink")
        fd = os.open(path, os.O_PATH | os.O_CLOEXEC | os.O_NOFOLLOW)
        try:
            info = os.fstat(fd)
            require(stat.S_ISBLK(info.st_mode) and info.st_rdev == os.stat(path, follow_symlinks=False).st_rdev, "BlockStatChanged")
            result.append({"DevicePath": path, "Major": os.major(info.st_rdev), "Minor": os.minor(info.st_rdev)})
        finally:
            os.close(fd)
    require(len({(n["Major"], n["Minor"]) for n in result}) == len(result), "AliasedBlockNumbers")
    return result


def collect_stable_inventory(declaration, runtime):
    path = declaration["CollectorPath"]
    require(digest(Path(path).read_bytes()) == declaration["CollectorSha256"], "CollectorChanged")
    def collect():
        value = subprocess.run([runtime.python, "-I", path], stdin=subprocess.DEVNULL, capture_output=True,
                               timeout=180, env=ENVIRONMENT, check=False)
        require(len(value.stdout) <= LIMIT, "InventoryOversized")
        observed = json.loads(value.stdout)
        # Send failed observations to the canonical layer; never replace them with empty lists.
        if observed.get("availability") != "Available":
            return observed, []
        require(value.returncode == 0, "InventoryProcessFailed")
        return observed, numbers_for(observed)
    first, numbers = collect()
    second, after = collect()
    require(first == second and numbers == after, "InventoryChangedAcrossBlockStat")
    return second, after


def lab_guest_disks(inventory):
    require(inventory.get("availability") == "Available", "LabWholeInventoryUnavailable")
    result = []
    for disk in inventory["disks"]:
        name = Path(disk["devicePath"]).name
        result.append({"DevicePath": disk["devicePath"],
            "Serial": Path("/sys/class/block", name, "serial").read_text().strip(),
            "PhysicalSectorSize": int(Path("/sys/class/block", name, "queue/physical_block_size").read_text())})
    return result


def lab_provenance(declaration):
    userdata = declaration.get('LabUserData')
    if userdata is not None:
        require(all(declaration.get(k) is None for k in ('StorageSmoke', 'LabImport', 'ImportPlan', 'LabConfiguration', 'ConfigurationPlan', 'LabInitramfs', 'InitramfsPlan')),
                'MixedLabScopes')
        require(userdata.get('Provider') == 'IsolatedFileBackedLab' and type(userdata.get('Version')) is int and userdata['Version'] == 5 and
                userdata.get('Scope') == 'SelectedDocumentTrees' and userdata.get('GenerationId') == declaration['GenerationId'] and
                declaration['UserDataPlan']['Provenance'] == userdata, 'UserDataProvenanceInvalid')
        return userdata
    initramfs = declaration.get('LabInitramfs')
    if initramfs is not None:
        require(all(declaration.get(k) is None for k in ('StorageSmoke', 'LabImport', 'ImportPlan', 'LabConfiguration', 'ConfigurationPlan')),
                'MixedLabScopes')
        require(initramfs.get('Provider') == 'IsolatedFileBackedLab' and initramfs.get('Version') == 4 and
                initramfs.get('Scope') == 'InitramfsImage' and initramfs.get('GenerationId') == declaration['GenerationId'] and
                declaration['InitramfsPlan']['Provenance'] == initramfs, 'InitramfsProvenanceInvalid')
        return initramfs
    configuration = declaration.get("LabConfiguration")
    if configuration is not None:
        require(declaration.get("StorageSmoke") is None and declaration.get("LabImport") is None and declaration.get("ImportPlan") is None, "MixedLabScopes")
        require(configuration.get("Provider") == "IsolatedFileBackedLab" and configuration.get("Version") == 3 and
                configuration.get("Scope") == "CoreConfiguration" and configuration.get("GenerationId") == declaration["GenerationId"] and
                declaration["ConfigurationPlan"]["Provenance"] == configuration, "ConfigurationProvenanceInvalid")
        return configuration
    smoke, imported = declaration.get("StorageSmoke"), declaration.get("LabImport")
    require(not (smoke is not None and imported is not None), "MixedLabScopes")
    value = smoke if smoke is not None else imported
    if value is not None:
        scope, version = ("StorageSmoke", 1) if smoke is not None else ("ConfiguredRootImport", 2)
        require(value.get("Provider") == "IsolatedFileBackedLab" and value.get("Version") == version and
                value.get("Scope") == scope and value.get("GenerationId") == declaration["GenerationId"], "LabProvenanceInvalid")
        require((declaration.get("ImportPlan") is None) == (smoke is not None), "LabImportScopeChanged")
        if imported is not None:
            require(declaration["ImportPlan"].get("Provenance") == imported and
                    declaration["ImportPlan"].get("SchemaVersion") == 1, "LabImportPlanChanged")
    return value


def root_only(declaration):
    return lab_provenance(declaration) is not None or declaration.get("ImportPlan") is not None


class CanonicalBlocks:
    def __init__(self, declaration, runtime, channel):
        self.declaration, self.runtime, self.channel = declaration, runtime, channel
        self.leases = {}
        self.bindings = None
        self.failed = False

    def inventory(self):
        second, after = collect_stable_inventory(self.declaration, self.runtime)
        self.channel.observation_sequence += 1
        extra = {"GuestDisks": lab_guest_disks(second)} if lab_provenance(self.declaration) is not None else {}
        response = self.channel.ask("Inventory", Inventory=second, DeviceNumbers=after, **extra,
                                    ObservationSequence=self.channel.observation_sequence)
        leases = response["Leases"]
        require(leases["SessionId"] == self.declaration["SessionId"] and leases["GenerationId"] == self.declaration["GenerationId"],
                "LeaseSessionChanged")
        if lab_provenance(self.declaration) is not None:
            require(leases.get("Provenance") == lab_provenance(self.declaration), "LeaseProvenanceChanged")
        bindings = leases["Bindings"]
        require(len(bindings) == 3 and [b["Role"] for b in bindings] == [0, 1, 2], "LeaseRoleSetChanged")
        if self.bindings is not None:
            require(bindings == self.bindings, "ActiveBlockLeaseChanged")
        return bindings

    def acquire(self):
        require(not self.leases and not self.failed, "BlockSessionSingleUse")
        self.failed = True  # Failed acquisition cannot be silently retried.
        self.bindings = self.inventory()
        self.channel.checkpoint({"State": "IntentDurable", "Bindings": self.bindings})
        try:
            self.inventory()  # Durable I/O is a race boundary; never open from a cached observation.
            for binding, role in zip(self.bindings, ("Root", "LinuxEsp", "Payload")):
                locator = binding["Locator"]
                require(binding["Access"] == (1 if role == "Root" else 0), "BlockAccessDeclarationChanged")
                path = locator["DevicePath"]
                require(os.path.realpath(path, strict=True) == path, "BlockPathSubstituted")
                fd = os.open(path, (os.O_RDWR if role == "Root" else os.O_RDONLY) | os.O_CLOEXEC | os.O_NOFOLLOW)
                lease = BlockLease(self.declaration["GenerationId"], self.declaration["PlanSha256"], role,
                                   fd, locator["Major"], locator["Minor"], binding["CanonicalSha256"])
                self.leases[role] = lease
                self._descriptor(lease, binding)
                fcntl.flock(fd, fcntl.LOCK_EX | fcntl.LOCK_NB)  # Cooperative, NOT a kernel exclusive ownership claim.
            self.inventory()
            self.channel.checkpoint({"State": "AppliedAndVerified", "Bindings": self.bindings})
            self.failed = False
        except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
            self.close_descriptors()
            raise

    def _descriptor(self, lease, binding):
        info = os.fstat(lease.descriptor)
        require(stat.S_ISBLK(info.st_mode) and (os.major(info.st_rdev), os.minor(info.st_rdev)) == (lease.major, lease.minor), "BlockDescriptorSubstituted")
        require(fcntl.fcntl(lease.descriptor, fcntl.F_GETFD) & fcntl.FD_CLOEXEC, "BlockDescriptorInheritable")
        size = int.from_bytes(fcntl.ioctl(lease.descriptor, 0x80081272, b"\0" * 8), sys.byteorder)  # BLKGETSIZE64, read-only
        partition = binding.get("StoragePartition") if lab_provenance(self.declaration) is not None else binding.get("Partition")
        require(partition is not None and (binding.get("Partition") is None if lab_provenance(self.declaration) is not None
                else binding.get("StoragePartition") is None), "MixedBlockProvider")
        require(size == partition["SizeBytes"], "BlockDescriptorSizeChanged")

    def revalidate(self, lease):
        require(not self.failed and self.leases.get(lease.role) is lease, "BlockLeaseNotActive")
        try:
            bindings = self.inventory()
            binding = bindings[("Root", "LinuxEsp", "Payload").index(lease.role)]
            self._descriptor(lease, binding)
            return binding["CanonicalSha256"]
        except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
            self.failed = True
            raise

    def close_descriptors(self):
        for lease in self.leases.values():
            os.close(lease.descriptor)
        self.leases.clear()


class MountSession:
    """One persistent private mount namespace. Package execution is deliberately not an action."""
    def __init__(self, declaration, runtime, channel):
        self.declaration, self.channel = declaration, channel
        self.runtime = runtime
        self.blocks = CanonicalBlocks(declaration, runtime, channel)
        self.supervisor = ExactMountSupervisor(declaration["GenerationId"], declaration["PlanSha256"],
            declaration["ParentMountNamespace"], runtime, self.blocks.revalidate, channel.checkpoint)
        self.failed = False
        self.closed = False
        self.acquired = False
        self.teardown = False
        self.import_attempted = False
        self.scaffold = False
        self.import_result = None
        self.configuration_attempted = False
        self.configuration_result = None
        self.userdata_attempted = False
        self.userdata_result = None
        self.initramfs_attempted = False
        self.initramfs_result = None

    def perform(self, action):
        require(not self.failed and not self.closed, "SessionClosedOrPoisoned")
        self.channel.action = action
        try:
            if action == "AcquireLeases":
                require(not self.acquired, "SessionCannotReacquire")
                self.acquired = True
                self.blocks.acquire()
            elif action == "PrepareImportMountpoints":
                require(self.acquired and not self.scaffold and not self.supervisor.receipts and
                        root_only(self.declaration), "ImportScaffoldingOutOfOrder")
                self.blocks.inventory()
                self.channel.checkpoint({"State": "IntentDurable", "Operation": "CreateImportMountpoints"})
                self.blocks.inventory()
                self._import_mountpoints()
                self.blocks.inventory()
                self.channel.checkpoint({"State": "AppliedAndVerified", "Observation": self.supervisor._observe("/run/igloo")})
                self.scaffold = True
            elif action in ("MountRoot", "MountLinuxEsp", "MountPayload"):
                require(not self.teardown, "SessionCannotRemountAfterTeardown")
                if root_only(self.declaration):
                    require(self.scaffold and action != "MountLinuxEsp", "ImportPhaseCannotMountEspUnderRoot")
                role = action[5:]
                lease = self.blocks.leases[role]
                generation = self.declaration["GenerationId"]
                root = "/run/igloo/target/" + generation
                path = {"Root": root, "LinuxEsp": root + "/boot/efi", "Payload": "/run/igloo/source/" + generation}[role]
                # Runtime preparation must supply existing protected empty mountpoints. Creating
                # parent directories on an unverified filesystem is not smuggled into mounting.
                destination = DirectoryLease(path)
                try:
                    require(not os.listdir(destination.fd), "MountpointNotEmpty")
                    self.supervisor.mount_block(lease, destination)
                finally:
                    destination.close()
            elif action == "ConfigureCore":
                require(self.acquired and not self.teardown and self.declaration.get("LabConfiguration") is not None, "ConfigurationScopeRequired")
                self.configuration_result = session_configuration.perform(self)
            elif action == 'TransferUserData':
                import session_userdata
                require(self.acquired and not self.teardown and self.declaration.get('LabUserData') is not None, 'UserDataScopeRequired')
                self.userdata_result = session_userdata.perform(self)
            elif action == 'GenerateInitramfs':
                import session_initramfs
                require(self.acquired and not self.teardown and self.declaration.get('LabInitramfs') is not None,
                        'InitramfsScopeRequired')
                self.initramfs_result = session_initramfs.perform(self)
            elif action == "ImportConfiguredRoot":
                require(self.acquired and not self.teardown and self.declaration.get("StorageSmoke") is None, "ImportSessionNotActive")
                self.import_result = session_import.perform_import(self)
            elif action in ("UnmountLinuxEsp", "UnmountPayload", "UnmountRoot"):
                self.teardown = True
                role = action[7:]
                rank = {"Scratch": 0, "LinuxEsp": 1, "Payload": 2, "Root": 3}
                require(self.supervisor.receipts and min(self.supervisor.receipts, key=lambda r: rank[r["Role"]])["Role"] == role,
                        "UnmountOrderChanged")
                self.blocks.revalidate(self.blocks.leases[role])
                self.supervisor.unmount_next()
            elif action == "Inspect":
                require(self.acquired, "SessionNotAcquired")
                self.blocks.inventory()
                for lease in self.blocks.leases.values():
                    self.blocks.revalidate(lease)
                for receipt in self.supervisor.receipts:
                    observed = self.supervisor._observe(receipt["Path"])
                    require(observed["Paths"][0]["Identity"] == receipt["Identity"] and
                            observed["Paths"][0]["MountId"] == receipt["MountId"], "SessionMountSubstituted")
                if self.declaration.get("StorageSmoke") is not None:
                    view = session_import.ConnectedImportView(self)
                    try:
                        view.verify()
                        self.channel.ask("StorageInspection", Observation=[self.supervisor._observe(r["Path"]) for r in self.supervisor.receipts])
                    finally:
                        view.close()
            elif action == "Close":
                require(not self.supervisor.receipts, "SessionStillMounted")
                self.channel.checkpoint({"State": "IntentDurable"})
                self.blocks.close_descriptors()
                self.channel.checkpoint({"State": "AppliedAndVerified"})
                self.closed = True
            else:
                raise ValueError("UnsupportedSessionAction")
            result = {"Kind": "Result", "SessionId": self.declaration["SessionId"], "Action": action, "State": "AppliedAndVerified"}
            if action == "ConfigureCore":
                result["ConfigurationResult"] = self.configuration_result
            if action == 'TransferUserData':
                result['UserDataResult'] = self.userdata_result
            if action == 'GenerateInitramfs':
                result['InitramfsResult'] = self.initramfs_result
            if action == "ImportConfiguredRoot":
                result["ImportResult"] = self.import_result
            return result
        except (OSError, ValueError, KeyError, subprocess.TimeoutExpired):
            self.failed = True
            raise

    def _import_mountpoints(self):
        # Runtime scaffolding only, outside the import root. Every component is pinned
        # and checked; generation directories must be create-new, never reused.
        generation = self.declaration["GenerationId"]
        run = DirectoryLease("/run")
        try:
            require(os.fstat(run.fd).st_uid == 0 and not os.fstat(run.fd).st_mode & 0o022, "UnprotectedRuntimeParent")
            for kind in ("target", "source"):
                current = os.dup(run.fd)
                try:
                    for name in ("igloo", kind, generation):
                        try:
                            os.mkdir(name, 0o700, dir_fd=current)
                            os.fsync(current)
                        except FileExistsError:
                            require(name != generation, "ImportMountpointGenerationAlreadyExists")
                        child = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=current)
                        info = os.fstat(child)
                        require(info.st_uid == 0 and stat.S_IMODE(info.st_mode) == 0o700 and
                                session_import.mount_id(child) == run.mount, "ImportScaffoldingSubstituted")
                        os.close(current); current = child
                    require(not os.listdir(current), "ImportMountpointNotEmpty")
                finally:
                    os.close(current)
            run.verify()
        finally:
            run.close()


def main(declaration):
    session = None
    try:
        require(os.geteuid() == 0, "TrustedRootSupervisorRequired")
        runtime = Runtime(**declaration["Runtime"])
        runtime.verify()
        require(str(uuid.UUID(declaration["SessionId"])) == declaration["SessionId"], "InvalidSessionId")
        channel = AuthorityChannel(declaration, sys.stdin.buffer, sys.stdout.buffer)
        session = MountSession(declaration, runtime, channel)
        observed = session.supervisor._observe("/run")  # Fresh external process, no target command.
        sys.stdout.buffer.write(canonical({"Kind": "SessionReady", "SessionId": declaration["SessionId"], "Observation": observed}) + b"\n")
        sys.stdout.buffer.flush()
        accepted = read_message(sys.stdin.buffer)
        require(accepted.get("SessionId") == declaration["SessionId"] and accepted.get("Accepted") is True, "SessionStartupNotDurable")
        while not session.closed:
            command = read_message(sys.stdin.buffer)
            require(command["SessionId"] == declaration["SessionId"] and set(command) == {"SessionId", "Action"}, "UnexpectedSessionCommand")
            result = session.perform(command["Action"])
            sys.stdout.buffer.write(canonical(result) + b"\n"); sys.stdout.buffer.flush()
        return 0
    except (OSError, ValueError, KeyError, TypeError, subprocess.TimeoutExpired):
        # Prior durable intent remains the authority. No implicit unmount or success receipt.
        return 1
    finally:
        if session is not None:
            session.blocks.close_descriptors()


if __name__ == "__main__":
    raise SystemExit("Use the authenticated session entrypoint")
