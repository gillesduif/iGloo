"""Qualified retained-tool generated-content queries shared by fixture and session."""
import os
from pathlib import Path
import subprocess
import sys
import uuid

def observe_generated(members, manifest, view, request, directory):
    """Closed retained-tool queries on opaque data, never archive extraction.

    Only fixed index/font/microcode inputs are materialized in this independently
    owned test workspace. No archive executable, symlink or device is created.
    """
    import re
    import stat
    import initramfs_generated as generated
    import initramfs_candidate as candidate
    import configured_root as root
    from isolation_policy import canonical, digest, require, Launch, Profile
    from package_broker import DirectoryLease, PackageBroker, Runtime
    source = {e['Path'].lstrip('/'): e for e in manifest['Entries']}
    directory = Path(directory); directory.mkdir(mode=0o700)
    def write(path, data):
        with path.open('xb') as output:
            output.write(data); output.flush(); os.fsync(output.fileno())
        fd = os.open(path.parent, os.O_DIRECTORY | os.O_NOFOLLOW)
        try: os.fsync(fd)
        finally: os.close(fd)
        require(path.read_bytes() == data, 'ImageObserverRecordReopen')
    def read(path):
        entry = source[path.lstrip('/')]
        require(entry['Type'] == 'File', 'ImageObserverSourceType')
        data = root.read_small(view, path)
        require(digest(data) == entry['Sha256'], 'ImageObserverSourceSubstitution')
        return data
    def query(label, executable, args):
        tool = Path(executable).resolve(strict=True)
        require(digest(tool.read_bytes()) == source[str(tool).lstrip('/')]['Sha256'], 'ImageObserverToolChanged')
        for p in (tool, *tool.parents):
            st = p.stat()
            require(st.st_uid == 0 and not st.st_mode & 0o022, 'ImageObserverToolPlacement')
        write(directory/(label+'.intent.json'), canonical({'Executable': str(tool),
            'Sha256': digest(tool.read_bytes()), 'Arguments': args}))
        result = subprocess.run([executable, *args], stdin=subprocess.DEVNULL, capture_output=True,
            timeout=120, env={'PATH': '/usr/sbin:/usr/bin:/sbin:/bin', 'LC_ALL': 'C'}, check=False)
        require(len(result.stdout) <= 1024*1024 and len(result.stderr) <= 65536, 'ImageObserverOutputBudget')
        write(directory/(label+'.result.json'), canonical({'ExitCode': result.returncode,
            'StdoutSha256': digest(result.stdout), 'StderrSha256': digest(result.stderr)}))
        require(result.returncode == 0 and result.stderr == b'', 'ImageObserverToolFailure:' + label)
        return result.stdout
    prefix = 'usr/lib/modules/' + candidate.RELEASE + '/'
    checked = generated.verify_indices(members, prefix, read)
    module_root = directory/'module-query'; d = module_root/'lib/modules'/candidate.RELEASE
    d.mkdir(parents=True, mode=0o700)
    for name in ('modules.dep.bin', 'modules.alias.bin', 'modules.symbols.bin', 'modules.builtin.bin',
                 'modules.builtin.alias.bin', 'modules.builtin', 'modules.builtin.modinfo', 'modules.softdep', 'modules.weakdep'):
        write(d/name, members[prefix+name].data)
    write(module_root/'empty.conf', b'')
    deps = {line.split(':', 1)[0]: line.split(':', 1)[1].split()
            for line in members[prefix+'modules.dep'].data.decode().splitlines()}
    builtins = {generated.module_name(p) for p in read('/'+prefix+'modules.builtin').decode().splitlines()}
    required = ('virtio', 'virtio_pci', 'virtio_blk', 'ext4')
    lookup = {}
    for name in required:
        paths = [p for p in deps if generated.module_name(p) == name]
        require(name in builtins or len(paths) == 1, 'RequiredModuleLookupAmbiguous')
        source_output = query('source-module-'+name, '/usr/sbin/modprobe', ['--show-depends', '--ignore-install',
            '--config', str(module_root/'empty.conf'), '--dirname', request['Target'], '--set-version', candidate.RELEASE, name])
        needed = []
        expected = []
        source_prefix = str(Path(request['Target'])/'lib/modules'/candidate.RELEASE) + '/'
        for line in source_output.decode().splitlines():
            if line.startswith('builtin '):
                require(line[8:] in builtins, 'SourceBuiltinLookupChanged')
                expected.append(line); continue
            require(line.startswith('insmod '+source_prefix) and line.endswith(' '), 'SourceModuleQueryShape')
            path = line[len('insmod '+source_prefix):].strip()
            require(path in deps and prefix+path in members, 'RequiredSoftDependencyMissing')
            needed.append(path); expected.append('insmod '+str(d/path))
        for path in needed:
            dest = d/path
            if dest.exists(): continue
            dest.parent.mkdir(parents=True, exist_ok=True)
            data = read('/'+prefix+path)
            require(members[prefix+path].data == data, 'ModuleLookupSourceChanged')
            write(dest, data)
        output = query('module-'+name, '/usr/sbin/modprobe', ['--show-depends', '--ignore-install',
            '--config', str(module_root/'empty.conf'), '--dirname', str(module_root), '--set-version', candidate.RELEASE, name])
        require([line.rstrip() for line in output.decode().splitlines()] == [line.rstrip() for line in expected],
                'RequiredModuleLookupMismatch:'+name)
        lookup[name] = 'BuiltIn' if name in builtins else 'DependencyClosureVerified'
    for name in ('GenuineIntel.bin',):
        write(directory/name, members['kernel/x86/microcode/'+name].data)
    intel = query('intel-candidate', '/usr/sbin/iucode_tool', ['--strict-checks', '--list-all', str(directory/'GenuineIntel.bin')])
    intel_source = query('intel-source', '/usr/sbin/iucode_tool', ['--strict-checks', '--list',
        str(Path(request['Target'])/'usr/lib/firmware/intel-ucode')])
    vendors = set(re.findall(r'^vendor_id\s*:\s*(\S+)', Path('/proc/cpuinfo').read_text(), re.M))
    require(vendors == {'AuthenticAMD'}, 'DeclaredLabCpuChanged')
    checked |= generated.verify_microcode(members, source, read, 'AuthenticAMD', intel, intel_source)
    write(directory/'ld.so.cache', members['etc/ld.so.cache'].data)
    cache = query('library-cache', '/usr/sbin/ldconfig', ['-p', '-C', str(directory/'ld.so.cache')])
    version = next(p['Version'] for p in manifest['Packages'] if p['Name'] == 'libc-bin')
    checked |= generated.verify_library_cache(members, cache, version)
    # Font queries use the retained target fontconfig under the same stopped
    # gate and independent effective-isolation observer as other read-only work.
    stage = Path(request['Stage']); python = str(Path(sys.executable).resolve())
    runtime = Runtime(str(stage/'bwrap'), digest((stage/'bwrap').read_bytes()), str(stage/'command-gate'),
        digest((stage/'command-gate').read_bytes()), python, digest(Path(python).read_bytes()),
        str(stage/'native/isolation_observer.py'), digest((stage/'native/isolation_observer.py').read_bytes()))
    projection = directory/'font-root'; projection.mkdir(mode=0o700)
    fonts = ('usr/share/fonts/truetype/dejavu/DejaVuSans.ttf', 'usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf')
    for path in fonts:
        require(path in members and members[path].sha256 == source[path]['Sha256'], 'SelectedFontSourceChanged')
        dest = projection/path; dest.parent.mkdir(parents=True, exist_ok=True); write(dest, read('/'+path))
    config = projection/'etc/fonts'; config.mkdir(parents=True)
    write(config/'query.conf', b'<?xml version="1.0"?><!DOCTYPE fontconfig SYSTEM "urn:fontconfig:fonts.dtd"><fontconfig></fontconfig>')
    caches = {}
    for path, member in members.items():
        if not path.startswith('var/cache/fontconfig/') or not path.endswith('.cache-9'): continue
        name, seconds, nanos = generated.font_cache_header(member.data)
        require(members[name[1:]].mtime == seconds, 'FontCacheDirectoryTimeChanged')
        dest = projection/name[1:]; dest.mkdir(parents=True, exist_ok=True)
        caches[path] = (dest, seconds*10**9+nanos)
        write(directory/Path(path).name, member.data)
    for dest, timestamp in caches.values(): os.utime(dest, ns=(timestamp, timestamp))
    resources = {'root': DirectoryLease(request['Target']),
        'esp': DirectoryLease(str(Path(request['Target'])/'mnt')), 'payload': DirectoryLease(str(directory))}
    try:
        def font_query(label, tool, arguments):
            command = Launch(str(uuid.uuid4()), digest(canonical(request['Plan'])), 'InspectArtifacts', Profile.OBSERVER,
                tool, source[tool[1:]]['Sha256'], arguments, 120, view='CoreConfiguration')
            result = PackageBroker(runtime).query_font(command, resources, lambda op, iso:
                write(directory/(label+'.intent.json'), canonical({'OperationSha256': op, 'IsolationSha256': iso})))
            require(isinstance(result, tuple), 'FontQueryUnavailable')
            receipt, output = result
            receipt['OutputSha256'] = digest(output)
            write(directory/(label+'.result.json'), canonical(receipt))
            require(receipt['State'] == 'Exited' and receipt['ExitCode'] == 0 and output, 'FontQueryFailed')
            if tool == '/usr/bin/fc-match':
                # Selection scans the authenticated fonts without persisting a
                # source cache in this read-only view. Only this exact cache-
                # persistence diagnostic is permitted, and its bytes are bound.
                warning = b'Fontconfig error: No writable cache directories\n'
                while output.startswith(warning): output = output[len(warning):]
            return output
        for i, args in enumerate((('--format', '%{file}'), ('--format', '%{file}', 'monospace'))):
            require(font_query('font-selection-'+str(i), '/usr/bin/fc-match', args) == ('/'+fonts[i]).encode(),
                    'PlymouthFontSelectionChanged')
        patterns = {path: font_query('font-source-'+str(i), '/usr/bin/fc-query', ('--format', '%{=unparse}', '/'+path))
                    for i, path in enumerate(fonts)}
        queries = {path: font_query('font-cache-'+str(i), '/usr/bin/fc-cat', ('--verbose', '/run/igloo-source/'+Path(path).name))
                   for i, path in enumerate(caches)}
        checked |= generated.verify_fonts(members, queries, patterns)
    finally:
        for resource in resources.values(): resource.close()
    for path in checked:
        mode = 0o644 if path in ('kernel/x86/microcode/GenuineIntel.bin', 'etc/ld.so.cache') else \
            0o755 if path.startswith('kernel/x86/microcode/.') else 0o600
        entry = members[path]
        correct_type = stat.S_ISDIR(entry.mode) if path.startswith('kernel/x86/microcode/.') else stat.S_ISREG(entry.mode)
        require(correct_type and stat.S_IMODE(entry.mode) == mode and entry.uid == entry.gid == 0,
                'GeneratedOutputMetadata')
    result = {'DependencyIndices': 'Verified', 'RequiredModuleLookups': lookup, 'Microcode': 'VerifiedAuthenticAMDMost',
        'FontCaches': 'VerifiedRetainedQueries', 'LibraryCache': 'Verified', 'GeneratedPaths': sorted(checked)}
    write(directory/'result.json', canonical(result))
    return checked, result


