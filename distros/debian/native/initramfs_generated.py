"""Narrow generated-index checks against the authenticated target metadata.

This reads the kmod 2.1 on-disk trie, not a module loader or alias resolver.
Required runtime lookups are a separate retained-tool observation.
"""
import re
import struct
import shlex
import hashlib
import stat

from isolation_policy import require


def index_entries(data):
    require(12 <= len(data) <= 16 * 1024 * 1024 and
            struct.unpack_from('>II', data) == (0xB007F457, 0x00020001), 'ModuleIndexHeader')
    spans, seen, result = [(0, 12)], set(), {}
    stack = [(struct.unpack_from('>I', data, 8)[0], '')]
    while stack:
        pointer, key = stack.pop()
        offset = pointer & 0x0fffffff
        if not offset:
            require(pointer == 0, 'ModuleIndexNullFlags')
            continue
        require(not pointer & 0x10000000 and offset >= 12 and offset not in seen and
                len(seen) < 131072 and len(key) <= 4096, 'ModuleIndexGraph')
        seen.add(offset); start = offset
        def number():
            nonlocal offset
            require(offset + 4 <= len(data), 'ModuleIndexTruncated')
            value = struct.unpack_from('>I', data, offset)[0]; offset += 4
            return value
        def string():
            nonlocal offset
            end = data.find(b'\0', offset, min(len(data), offset + 16384))
            require(end >= offset, 'ModuleIndexString')
            raw = data[offset:end]; offset = end + 1
            require(all(32 <= c < 127 for c in raw), 'ModuleIndexEncoding')
            return raw.decode('ascii')
        if pointer & 0x80000000: key += string()
        if pointer & 0x20000000:
            require(offset + 2 <= len(data), 'ModuleIndexChildren')
            first, last = data[offset:offset + 2]; offset += 2
            require(first <= last < 128, 'ModuleIndexChildRange')
            stack.extend((number(), key + chr(c)) for c in range(first, last + 1))
        if pointer & 0x40000000:
            count = number(); require(count <= 65536 and key not in result, 'ModuleIndexValues')
            values = [(number(), string()) for _ in range(count)]
            result[key] = [v for _, v in sorted(values)]
        spans.append((start, offset))
    end = 0
    for start, stop in sorted(spans):
        require(start == end and stop > start, 'ModuleIndexUnaccountedBytes')
        end = stop
    require(end == len(data), 'ModuleIndexTrailingBytes')
    return result


def module_name(path):
    return re.sub(r'\.ko(?:\.(?:xz|zst))?$', '', path.rsplit('/', 1)[-1]).replace('-', '_')


def verify_indices(members, prefix, read_source):
    selected = {p[len(prefix):] for p in members if p.startswith(prefix) and re.search(r'\.ko(?:\.(?:xz|zst))?$', p)}
    names = {module_name(p) for p in selected}
    def source(name): return read_source('/' + prefix + name)
    def actual(name):
        require(prefix + name in members, 'ModuleIndexMissing')
        return members[prefix + name].data
    checked = []
    dependency_lines = {}
    for name in ('modules.dep.bin', 'modules.alias.bin', 'modules.symbols.bin', 'modules.builtin.bin', 'modules.builtin.alias.bin'):
        expected = index_entries(source(name))
        if name == 'modules.dep.bin':
            expected = {k: v for k, v in expected.items() if k in names}
        elif name in ('modules.alias.bin', 'modules.symbols.bin'):
            expected = {k: [v for v in values if v in names] for k, values in expected.items()}
            expected = {k: v for k, v in expected.items() if v}
        observed = index_entries(actual(name))
        if name == 'modules.dep.bin':
            require(set(observed) == set(expected), 'ModuleDependencySet')
            for key, values in observed.items():
                require(len(values) == len(expected[key]) == 1, 'ModuleDependencyValue')
                path, dependencies = values[0].split(':', 1)
                source_path, source_dependencies = expected[key][0].split(':', 1)
                deps = dependencies.split()
                require(path == source_path and path in selected and len(deps) == len(set(deps)) and
                        set(deps) == set(source_dependencies.split()) <= selected, 'ModuleDependencySource')
                dependency_lines[path] = values[0]
            for line in dependency_lines.values():
                deps = line.split(':', 1)[1].split()
                positions = {p: i for i, p in enumerate(deps)}
                for dep in deps:
                    required = dependency_lines[dep].split(':', 1)[1].split()
                    require(all(d in positions and positions[d] > positions[dep] for d in required), 'ModuleDependencyOrder')
        else:
            require(observed == expected, 'ModuleIndexSourceMismatch:' + name)
        checked.append(prefix + name)
    def lines(data): return [l for l in data.decode('ascii').splitlines() if l and not l.startswith('#')]
    for name in ('modules.dep', 'modules.alias', 'modules.symbols', 'modules.softdep', 'modules.weakdep', 'modules.devname'):
        original_bytes = source(name)
        require([l for l in actual(name).splitlines() if l.startswith(b'#')] ==
                [l for l in original_bytes.splitlines() if l.startswith(b'#')], 'ModuleIndexCommentChanged')
        original = lines(original_bytes)
        if name == 'modules.dep': expected = list(dependency_lines.values())
        elif name in ('modules.alias', 'modules.symbols'): expected = [l for l in original if l.split()[-1] in names]
        elif name in ('modules.softdep', 'modules.weakdep'): expected = [l for l in original if l.split()[1] in names]
        else: expected = [l for l in original if l.split()[0] in names]
        require(sorted(lines(actual(name))) == sorted(expected), 'ModuleTextIndexSourceMismatch:' + name)
        checked.append(prefix + name)
    return set(checked)


def font_cache_header(data):
    require(64 <= len(data) <= 4 * 1024 * 1024, 'FontCacheBudget')
    magic, version, length, directory = struct.unpack_from('<IIqq', data)
    require(magic == 0xfc02fc04 and version == 9 and length == len(data) and 64 <= directory < len(data), 'FontCacheHeader')
    end = data.find(b'\0', directory, directory + 4096)
    require(end > directory, 'FontCacheDirectory')
    path = data[directory:end].decode('ascii')
    require(path in ('/usr/share/fonts', '/usr/share/fonts/truetype',
        '/usr/share/fonts/truetype/dejavu', '/usr/local/share/fonts'), 'FontCacheDirectoryScope')
    seconds, nanos = struct.unpack_from('<qq', data, 48)
    require(0 <= seconds < 2**32 and 0 <= nanos < 10**9, 'FontCacheTimestamp')
    return path, seconds, nanos


def verify_fonts(members, cache_queries, source_queries):
    """Compare retained fc-cat patterns to independent retained fc-query output."""
    checked, directories = set(), set()
    from initramfs_archive import resolve
    for path, member in members.items():
        if not path.startswith('var/cache/fontconfig/') or not path.endswith('.cache-9'): continue
        directory, seconds, _ = font_cache_header(member.data)
        require(directory not in directories and directory[1:] in members and members[directory[1:]].mtime == seconds,
                'FontCacheArchiveDirectory')
        directories.add(directory)
        require(path.rsplit('/', 1)[-1] == hashlib.md5(directory.encode(), usedforsecurity=False).hexdigest() + '-le64.cache-9',
                'FontCacheNameBinding')
        lines = cache_queries[path].decode('utf-8').splitlines()
        require(len(lines) >= 4 and lines[:3] == ['Directory: ' + directory,
            'Cache: /run/igloo-source/' + path.rsplit('/', 1)[-1], '--------'], 'FontCacheReadbackHeader')
        observed = {}
        for line in lines[3:]:
            if line in ('', '<empty>'): continue
            parts = shlex.split(line)
            require(len(parts) == 3 and parts[1] == '0' and '/' not in parts[0] and parts[0] not in ('', '.', '..') and
                    parts[0] not in observed, 'FontCachePatternShape')
            observed[parts[0]] = parts[2]
        expected = {}
        for name, entry in members.items():
            if name.rsplit('/', 1)[0] != directory[1:]: continue
            leaf = name.rsplit('/', 1)[-1]
            if stat.S_ISDIR(entry.mode): expected[leaf] = '.dir'
            else:
                resolved = resolve(members, name)
                require(resolved in source_queries, 'FontCacheUnboundFont')
                # fc-cat omits FC_FILE; every other unparsed pattern field must
                # equal the independent source-font query, including charset/lang.
                expected[leaf] = re.sub(r'(?<!\\):file=[^:]*', '', source_queries[resolved].decode('utf-8'))
        require(observed == expected, 'FontCachePatternMismatch')
        checked.add(path)
    require(directories == {'/usr/share/fonts', '/usr/share/fonts/truetype',
        '/usr/share/fonts/truetype/dejavu', '/usr/local/share/fonts'}, 'FontCacheClosureMissing')
    return checked


def intel_blocks(data):
    """Frame authenticated Intel records; strict validation is done by iucode_tool."""
    offset = 0; blocks = []
    while offset < len(data):
        require(offset + 48 <= len(data) and len(blocks) < 4096, 'MicrocodeRecordHeader')
        length = struct.unpack_from('<I', data, offset + 32)[0] or 2048
        require(48 <= length <= 4 * 1024 * 1024 and offset + length <= len(data), 'MicrocodeRecordLength')
        blocks.append(hashlib.sha256(data[offset:offset + length]).digest()); offset += length
    return blocks


def verify_microcode(members, source, read_source, vendor, candidate_listing, source_listing):
    amd = 'kernel/x86/microcode/AuthenticAMD.bin'; intel = 'kernel/x86/microcode/GenuineIntel.bin'
    require(vendor in ('AuthenticAMD', 'GenuineIntel'), 'MicrocodeCpuScopeMissing')
    checked = set()
    if vendor == 'AuthenticAMD':
        paths = sorted(p for p, e in source.items() if p.startswith('usr/lib/firmware/amd-ucode/') and
                       '/' not in p[len('usr/lib/firmware/amd-ucode/'):] and e['Type'] == 'File')
        expected = b''.join(read_source('/' + p) for p in paths)
        require(expected and amd in members and members[amd].data == expected, 'AmdMicrocodeComposition')
        checked.add(amd)
    else: require(amd not in members, 'UnexpectedAmdMicrocode')
    require(intel in members and not any(p.startswith('usr/share/misc/intel-microcode') for p in source), 'IntelMicrocodeInputScope')
    paths = [p for p, e in source.items() if p.startswith('usr/lib/firmware/intel-ucode/') and
             '/' not in p[len('usr/lib/firmware/intel-ucode/'):] and e['Type'] == 'File']
    available = {block for p in paths for block in intel_blocks(read_source('/' + p))}
    blocks = intel_blocks(members[intel].data)
    require(blocks and len(blocks) == len(set(blocks)) and set(blocks) <= available, 'IntelMicrocodeSourceSubstitution')
    def selected(data):
        # --list-all includes extended signatures on indented continuation
        # lines. They refer to the preceding complete record's size; --list
        # expands and deduplicates those signatures during source selection.
        values = set(); size = None
        for line in data.splitlines():
            match = re.fullmatch(rb'\s*(?:\d+/\d+: )?(sig 0x[0-9a-f]+, pf_mask 0x[0-9a-f]+, [0-9-]+, rev 0x[0-9a-f]+)(?:, size ([0-9]+))?', line)
            if match is None: continue
            if match[2] is not None: size = match[2]
            require(size is not None, 'MicrocodeExtendedSignatureContext')
            values.add(match[1] + b', size ' + size)
        require(values, 'MicrocodeIndependentSelectionMissing')
        return sorted(values)
    require(selected(candidate_listing) == selected(source_listing), 'IntelMicrocodeSelectionMismatch')
    checked.add(intel)
    padding = 'kernel/x86/microcode/.enuineIntel.align.0123456789abc'
    require(padding in members and stat.S_ISDIR(members[padding].mode) and members[padding].data == b'' and stat.S_IMODE(members[padding].mode) == 0o755,
            'IntelEarlyArchiveAlignment')
    checked.add(padding)
    return checked


def verify_library_cache(members, listing, libc_version):
    """Bounded glibc 1.1 cache plus independent ldconfig -p observation.

    Only the retained amd64 format and generator extension are supported. All
    strings are accounted for and every SONAME resolves to source-bound ELF data.
    """
    from initramfs_archive import resolve
    from initramfs_candidate import elf_dependencies
    data = members['etc/ld.so.cache'].data
    require(48 <= len(data) <= 1024 * 1024 and data[:20] == b'glibc-ld.so.cache1.1', 'LibraryCacheHeader')
    count, length, flags, extension, *unused = struct.unpack_from('<7I', data, 20)
    start = 48 + 24 * count; end = start + length
    require(0 < count <= 4096 and flags == 2 and unused == [0, 0, 0] and
            end <= extension == (end + 3) & ~3 and extension + 24 <= len(data) and
            data[end:extension] == b'\0' * (extension-end), 'LibraryCacheBounds')
    covered = set(); observed = {}
    def string(offset):
        require(start <= offset < end, 'LibraryCacheStringOffset')
        stop = data.find(b'\0', offset, end)
        require(stop >= offset, 'LibraryCacheStringEnd')
        covered.update(range(offset, stop + 1))
        return data[offset:stop].decode('ascii')
    expected = {}
    for path, entry in members.items():
        if not stat.S_ISREG(entry.mode) or path.rsplit('/', 1)[0] not in ('usr/lib', 'usr/lib64', 'usr/lib/x86_64-linux-gnu'): continue
        _, names = elf_dependencies(entry.data, sonames=True)
        for name in names:
            require(name not in expected or expected[name] == entry.sha256, 'LibrarySonameAmbiguous')
            expected[name] = entry.sha256
    for i in range(count):
        kind, key, value, osversion, hwcap = struct.unpack_from('<IIIIQ', data, 48 + i * 24)
        name, path = string(key), string(value)
        require(kind == 0x303 and osversion == hwcap == 0 and name not in observed and
                path.startswith(('/lib/', '/lib64/', '/usr/lib/', '/usr/lib64/')), 'LibraryCacheEntry')
        resolved = resolve(members, path.lstrip('/'))
        require(resolved in members and expected.get(name) == members[resolved].sha256, 'LibraryCacheSourceMismatch')
        observed[name] = path
    require(covered == set(range(start, end)) and set(observed) == set(expected), 'LibraryCacheClosure')
    magic, sections, tag, section_flags, offset, size = struct.unpack_from('<6I', data, extension)
    generator = ('ldconfig (Debian GLIBC ' + libc_version + ') stable release version 2.41').encode()
    require((magic, sections, tag, section_flags, offset, size) ==
            (0xeaa42174, 1, 0, 0, extension + 24, len(generator)) and
            data[offset:] == generator, 'LibraryCacheExtension')
    lines = listing.decode('ascii').splitlines()
    queried = {}
    for line in lines[1:]:
        if line.startswith('Cache generated by: '):
            require(line == 'Cache generated by: ' + generator.decode(), 'LibraryCacheQueryGenerator')
            continue
        match = re.fullmatch(r'\s+(\S+) \(libc6,x86-64\) => (\S+)', line)
        require(match is not None and match[1] not in queried, 'LibraryCacheQueryShape')
        queried[match[1]] = match[2]
    require(queried == observed and lines[0].startswith(str(count) + ' libs found in cache '), 'LibraryCacheQueryMismatch')
    return {'etc/ld.so.cache'}
