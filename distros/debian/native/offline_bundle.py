#!/usr/bin/env python3
"""Read/download-only Debian bundle builder and independent verifier.

Never installs packages, executes maintainer scripts, or uses the host dpkg state.
Requires Debian python3-apt, gpgv and a separately trusted archive keyring. The
request (including keyring/policy hashes and allowed signing fingerprints) must
come from the trusted build/deployment plan, NOT from the untrusted bundle.
"""
import argparse
import concurrent.futures
import datetime as dt
import email.utils
import hashlib
import importlib.util
import json
import lzma
import os
from pathlib import Path, PurePosixPath
import re
import subprocess
import sys
import tempfile
import urllib.parse
import urllib.request
import uuid

COMPONENTS = ("main", "contrib", "non-free", "non-free-firmware")
SUITES = ("trixie", "trixie-security", "trixie-updates")
MAX_FILE = 512 * 1024 * 1024
WSDD_EXCEPTION_ID = "trixie-gvfs-wsdd-2026-09-28-v1"
WSDD_SELECTOR = {"RepositoryId": "debian", "Suite": "trixie", "Package": "gvfs-backends",
                 "Version": "1.57.2-2+deb13u1", "Architecture": "amd64",
                 "Kind": "Recommends", "Relation": "wsdd"}


class Rejected(ValueError):
    pass


class ClosureRejected(Rejected):
    def __init__(self, selected_count, unresolved, report=None):
        super().__init__("UnresolvedRecommendation:" + ",".join(sorted(unresolved)))
        self.selected_count = selected_count
        self.unresolved = sorted(unresolved)
        self.report = report


def require(condition, code):
    if not condition:
        raise Rejected(code)


def digest(data):
    return hashlib.sha256(data).hexdigest().upper()


def read_json(path):
    def unique(pairs):
        out = {}
        for key, value in pairs:
            require(key not in out, "DuplicateJsonKey")
            out[key] = value
        return out
    return json.loads(Path(path).read_bytes(), object_pairs_hook=unique)


def relative(value):
    require(isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_+~:./-]+", value) is not None,
            "UnsafeRepositoryPath")
    path = PurePosixPath(value)
    require(not path.is_absolute() and all(p not in ("", ".", "..") for p in value.split("/")),
            "UnsafeRepositoryPath")
    return value


def read_file(root, name):
    path = root / relative(name)
    require(not root.is_symlink(), "SymlinkBundle")
    for parent in (path, *path.parents):
        require(not parent.is_symlink(), "SymlinkBundle")
        if parent == root:
            break
    require(path.is_file() and path.stat().st_size <= MAX_FILE, "MissingOrOversizedBundleFile")
    return path.read_bytes()


def descriptor(name, data):
    return {"Path": relative(name), "Length": len(data), "Sha256": digest(data)}


def verify_file(root, item):
    data = read_file(root, item["Path"])
    require(len(data) == item["Length"] and digest(data) == item["Sha256"], "BundleFileHashMismatch")
    return data


def fields(data):
    """Strict RFC822 subset used by Debian Release/Packages. Duplicates fail."""
    result = []
    paragraph = {}
    last = None
    for line in data.decode("utf-8").splitlines() + [""]:
        if not line:
            if paragraph:
                result.append(paragraph)
            paragraph, last = {}, None
        elif line[0] in " \t":
            require(last is not None, "InvalidDebianContinuation")
            paragraph[last] += "\n" + line[1:]
        else:
            require(":" in line, "InvalidDebianField")
            last, value = line.split(":", 1)
            require(last not in paragraph, "DuplicateDebianField")
            paragraph[last] = value.lstrip()
    return result


def run(argv, *, env=None):
    result = subprocess.run(argv, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, env=env, timeout=300, check=False)
    # Raw diagnostics are never part of an installation receipt.
    require(result.returncode == 0, "ToolFailed:" + Path(argv[0]).name)
    return result.stdout


def authenticate(inrelease, keyring, fingerprints):
    with tempfile.TemporaryDirectory(prefix="igloo-gpgv-") as name:
        root = Path(name)
        (root / "InRelease").write_bytes(inrelease)
        status = run(["/usr/bin/gpgv", "--homedir", name, "--keyring", str(keyring),
                      "--status-fd", "1", "--output", str(root / "Release"), str(root / "InRelease")])
        signatures = [line.split() for line in status.decode("ascii").splitlines()
                      if line.startswith("[GNUPG:] VALIDSIG ")]
        allowed = [s[2] for s in signatures if s[2] in fingerprints or s[-1] in fingerprints]
        require(bool(allowed), "UnapprovedDebianSigningKey")
        return (root / "Release").read_bytes(), sorted(allowed)[0]


def release_index(release, suite, now, max_age_days):
    parsed = fields(release)
    require(len(parsed) == 1, "InvalidRelease")
    values = parsed[0]
    expected_suite = {"trixie": "stable", "trixie-updates": "stable-updates", "trixie-security": "stable-security"}
    require(values.get("Codename") == suite and values.get("Suite") == expected_suite.get(suite) and values.get("Origin") == "Debian" and
            "amd64" in values.get("Architectures", "").split(), "ReleaseIdentityMismatch")
    date = email.utils.parsedate_to_datetime(values["Date"])
    expiry = email.utils.parsedate_to_datetime(values["Valid-Until"]) if "Valid-Until" in values else date + dt.timedelta(days=max_age_days)
    require(date.tzinfo is not None and date <= now < expiry and now - date <= dt.timedelta(days=max_age_days), "ReleaseExpiredOrFuture")
    indexes = {}
    for line in values.get("SHA256", "").splitlines():
        if not line.strip():
            continue
        sha, length, path = line.split()
        require(re.fullmatch(r"[0-9a-fA-F]{64}", sha) and path not in indexes, "InvalidReleaseDigest")
        indexes[relative(path)] = (int(length), sha.upper())
    require(bool(indexes), "MissingReleaseSha256")
    return indexes


def package_records(indexes):
    result = []
    for repo, data in indexes:
        for item in fields(data):
            arch = item.get("Architecture")
            require(arch in ("amd64", "all"), "UnexpectedPackageArchitecture")
            require(re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", item["Package"]) is not None, "InvalidPackageName")
            path = relative(item["Filename"])
            require(path.startswith("pool/"), "PackageOutsidePool")
            result.append((repo, item))
    return result


def validated_request(request):
    require(request["Release"] == "trixie" and request["Architecture"] == "amd64", "RequestReleaseOrArchitecture")
    require(uuid.UUID(request["GenerationId"]).int != 0 and uuid.UUID(request["BuildId"]).int != 0, "RequestGeneration")
    policy_bytes = Path(request["PolicyPath"]).read_bytes()
    require(digest(policy_bytes) == request["PolicySha256"], "PolicyHashMismatch")
    policy = read_json(request["PolicyPath"])
    require(policy["Release"] == "trixie" and policy["Architecture"] == "amd64" and
            policy["InstallRecommends"] is True and policy["InstallSuggests"] is False and
            policy["Tasks"] == ["standard"], "UnsupportedPackagePolicy")
    require(all(re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", p) for p in policy["Packages"]), "InvalidPolicyPackage")
    keyring = Path(request["KeyringPath"]).resolve(strict=True)
    require(digest(keyring.read_bytes()) == request["KeyringSha256"], "KeyringHashMismatch")
    require(request["SigningFingerprints"] and all(re.fullmatch(r"[0-9A-F]{40}", f) for f in request["SigningFingerprints"]), "MissingTrustedFingerprint")
    require(sorted(r["Suite"] for r in request["Repositories"]) == list(SUITES), "MissingRepository")
    require(len({r["Id"] for r in request["Repositories"]}) == 3, "DuplicateRepository")
    for repo in request["Repositories"]:
        relative(repo["Id"])
        require(repo["Id"] == {"trixie": "debian", "trixie-updates": "debian-updates", "trixie-security": "debian-security"}[repo["Suite"]], "RepositoryLayoutMismatch")
        require("/" not in repo["Id"], "InvalidRepositoryId")
        uri = urllib.parse.urlsplit(repo["OriginUri"])
        require(uri.scheme == "https" and uri.hostname and not uri.username and not uri.password and not uri.query and not uri.fragment, "InvalidOrigin")
    require(1 <= request["MaximumMetadataAgeDays"] <= 90, "InvalidFreshnessPolicy")
    require(request["BootstrapPackages"] and all(re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", p) for p in request["BootstrapPackages"]), "MissingBootstrapSelection")
    require(digest(Path(request["DebootstrapPath"]).read_bytes()) == request["DebootstrapSha256"], "DebootstrapChanged")
    if "RuntimeProfilePath" in request:
        profile_bytes = Path(request["RuntimeProfilePath"]).read_bytes()
        require(digest(profile_bytes) == request["RuntimeProfileSha256"], "RuntimeProfileHashMismatch")
        runtime_tool(request).verify(read_json(request["RuntimeProfilePath"]))
    return policy, keyring


def runtime_tool(request):
    path = Path(__file__).with_name("runtime_profile.py")
    require(digest(path.read_bytes()) == request["RuntimeProfileToolSha256"], "RuntimeObserverToolChanged")
    spec = importlib.util.spec_from_file_location("igloo_runtime_profile", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def recommendation_policy_report(policy, unresolved, now):
    """Allow only the reviewed exact absent-candidate relation, never an arbitrary waiver.

    The caller obtains relations (including operators/versions and providers) from
    APT over the authenticated indexes, not from an artifact's claimed report.
    """
    rules = policy.get("RecommendationExceptions", [])
    require(isinstance(rules, list) and len(rules) <= 1, "UnknownRecommendationPolicy")
    if rules:
        rule = rules[0]
        require(rule.get("Id") == WSDD_EXCEPTION_ID and rule.get("Release") == "trixie" and
                all(rule.get(k) == v for k, v in WSDD_SELECTOR.items()) and
                rule.get("EvidenceUri") == "https://bugs.debian.org/1110689" and
                rule.get("ReviewedUtc") == "2026-09-28T00:00:00+00:00" and
                rule.get("ReviewBeforeUtc") == "2026-10-28T00:00:00+00:00" and
                all(isinstance(rule.get(k), str) and rule[k] for k in ("Reason", "ProductImpact", "ReviewCondition")),
                "UnknownRecommendationPolicy")
        require(dt.datetime.fromisoformat(rule["ReviewedUtc"]) <= now < dt.datetime.fromisoformat(rule["ReviewBeforeUtc"]),
                "RecommendationPolicyReviewRequired")
    report = {"SchemaVersion": 1, "OriginalUnresolved": unresolved, "AppliedExceptions": [], "RemainingUnresolved": []}
    for relation in unresolved:
        if (rules and relation == {**WSDD_SELECTOR, "CandidateAvailable": False} and
                not report["AppliedExceptions"]):
            report["AppliedExceptions"].append({"ExceptionId": WSDD_EXCEPTION_ID, "Relation": relation})
        else:
            report["RemainingUnresolved"].append(relation)
    return report


def repository_choice(matches):
    require(matches, "SolverPackageMissingFromSignedIndexes")
    # The security archive uses pool/updates while the main archive uses pool.
    # Byte identity must agree; different authenticated filenames are provenance,
    # not different package content. Keep their aliases in the final manifest.
    require(len({(i["SHA256"].upper(), int(i["Size"])) for _, i in matches}) == 1, "ConflictingRepositoryPackage")
    return sorted(matches, key=lambda m: (m[0], m[1]["Filename"]))[0]


def repository_aliases(selected, metadata):
    by_identity = {(p["Name"], p["Version"], p["Architecture"]): p for p in selected}
    aliases = []
    for repo, item in metadata:
        source = by_identity.get((item["Package"], item["Version"], item["Architecture"]))
        if source is None or (repo, item["Filename"]) == (source["RepositoryId"], source["File"]["Path"]):
            continue
        require((int(item["Size"]), item["SHA256"].upper()) == (source["File"]["Length"], source["File"]["Sha256"]), "ConflictingRepositoryPackage")
        aliases.append({"RepositoryId": repo, "Name": source["Name"], "Version": source["Version"], "Architecture": source["Architecture"],
                        "File": {"Path": item["Filename"], "Length": int(item["Size"]), "Sha256": item["SHA256"].upper()}})
    require(len({(p["RepositoryId"], p["File"]["Path"]) for p in aliases}) == len(aliases), "DuplicateRepositoryAlias")
    return sorted(aliases, key=lambda p: (p["Name"], p["RepositoryId"], p["File"]["Path"]))


def apt_solve(root, request, policy, keyring, package_metadata):
    """Fresh APT state, empty dpkg status, file-only signed repositories. NO commit."""
    # apt_pkg is process-global; use a fresh process for every build or verification.
    import apt_pkg
    with tempfile.TemporaryDirectory(prefix="igloo-apt-") as name:
        work = Path(name)
        for path in ("lists/partial", "archives/partial", "empty"):
            (work / path).mkdir(parents=True)
        (work / "status").write_text("", encoding="ascii")
        local_keyring = work / "keyring.gpg"
        local_keyring.write_bytes(keyring.read_bytes())
        sources = ""
        for repo in request["Repositories"]:
            sources += f"deb [arch=amd64 signed-by={local_keyring}] {(root / repo['Id']).as_uri()} {repo['Suite']} {' '.join(COMPONENTS)}\n"
        (work / "sources.list").write_text(sources, encoding="utf-8")
        config = {
            "Dir::Etc::parts": str(work / "empty"), "Dir::Etc::main": "-",
            "Dir::Etc::sourcelist": str(work / "sources.list"), "Dir::Etc::sourceparts": str(work / "empty"),
            "Dir::Etc::preferences": str(work / "status"), "Dir::Etc::preferencesparts": str(work / "empty"),
            "Dir::Etc::trusted": str(local_keyring), "Dir::Etc::trustedparts": str(work / "empty"),
            "Dir::State::status": str(work / "status"), "Dir::State::lists": str(work / "lists"),
            "Dir::Cache::archives": str(work / "archives"), "Dir::Cache::pkgcache": "",
            "Dir::Cache::srcpkgcache": "", "Dir::Log": str(work),
            "APT::Architecture": "amd64", "APT::Install-Recommends": "true", "APT::Install-Suggests": "false",
            "Acquire::Languages": "none", "Acquire::Retries": "0", "Acquire::PDiffs": "false",
            "APT::Get::List-Cleanup": "false", "Acquire::AllowInsecureRepositories": "false",
            "APT::Update::Error-Mode": "any", "APT::Sandbox::User": str(os.getuid()),
        }
        # APT_CONFIG is read before host configuration. Parts/main are disabled there.
        # Reject quoting/control characters instead of interpolating them into apt.conf.
        require(all('"' not in v and "\n" not in v and "\\" not in v for v in config.values()), "UnsupportedAptPath")
        cfg = work / "apt.conf"
        cfg.write_text("\n".join(f'{k} "{v}";' for k, v in config.items()) + '\nAPT::Architectures { "amd64"; };\n', encoding="utf-8")
        environment = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C", "APT_CONFIG": str(cfg)}
        run(["/usr/bin/apt-get", "update", "--error-on=any"], env=environment)
        for key in list(apt_pkg.config.keys()):
            apt_pkg.config.clear(key)
        os.environ["APT_CONFIG"] = str(cfg)
        apt_pkg.init_config()
        apt_pkg.read_config_file(apt_pkg.config, str(cfg))
        apt_pkg.init_system()
        cache = apt_pkg.Cache(None)
        dep = apt_pkg.DepCache(cache)
        standard = sorted(p.name for p in cache.packages if (candidate := dep.get_candidate_ver(p)) is not None and
                          candidate.priority_str in ("required", "important", "standard"))
        seeds = sorted(set(policy["Packages"] + request["BootstrapPackages"] + standard))
        for pkg in seeds:
            require(pkg in cache and dep.get_candidate_ver(cache[pkg]) is not None, "MissingPolicyCandidate:" + pkg)
            dep.mark_install(cache[pkg], True, True)
        require(dep.broken_count == 0, "AptDependencyClosureBroken")
        versions = [dep.get_candidate_ver(p) for p in cache.packages if dep.marked_install(p)]
        selected_ids = {v.id for v in versions}
        by_identity = {}
        for repo, item in package_metadata:
            by_identity.setdefault((item["Package"], item["Version"], item["Architecture"]), []).append((repo, item))
        unresolved = []
        for version in versions:
            matches = by_identity.get((version.parent_pkg.name, version.ver_str, version.arch), [])
            repo_id, _ = repository_choice(matches)
            suite = next(r["Suite"] for r in request["Repositories"] if r["Id"] == repo_id)
            for kind in ("PreDepends", "Depends", "Recommends"):
                for alternatives in version.depends_list.get(kind, []):
                    targets = [target for dependency in alternatives for target in dependency.all_targets()]
                    if not any(target.id in selected_ids for target in targets):
                        relation = " | ".join(d.target_pkg.name + (f" ({d.comp_type} {d.target_ver})" if d.comp_type else "") for d in alternatives)
                        unresolved.append({"RepositoryId": repo_id, "Suite": suite, "Package": version.parent_pkg.name,
                                           "Version": version.ver_str, "Architecture": version.arch, "Kind": kind,
                                           "Relation": relation, "CandidateAvailable": bool(targets)})
        unresolved.sort(key=lambda r: (r["Package"], r["Kind"], r["Relation"]))
        report = recommendation_policy_report(policy, unresolved, dt.datetime.now(dt.timezone.utc))
        if report["RemainingUnresolved"]:
            raise ClosureRejected(len(versions), [r["Package"] + ":" + r["Relation"] for r in report["RemainingUnresolved"]], report)
        selected = []
        for package in cache.packages:
            if not dep.marked_install(package):
                continue
            version = dep.get_candidate_ver(package)
            repo, item = repository_choice(by_identity.get((package.name, version.ver_str, version.arch), []))
            selected.append({"RepositoryId": repo, "Name": item["Package"], "Version": item["Version"],
                             "Architecture": item["Architecture"], "File": {"Path": item["Filename"],
                             "Length": int(item["Size"]), "Sha256": item["SHA256"].upper()}})
        require(set(seeds) <= {p["Name"] for p in selected}, "SolverDroppedRequiredPackage")
        return sorted(selected, key=lambda p: p["Name"]), standard, apt_pkg.VERSION, report


def verify_bootstrap_selection(root, request, keyring):
    # Documented read/download-only operation, always with a disposable empty target.
    # No --foreign, --second-stage, chroot or install operation is allowed here.
    base = next(r for r in request["Repositories"] if r["Suite"] == "trixie")
    with tempfile.TemporaryDirectory(prefix="igloo-bootstrap-selection-") as work:
        output = run([request["DebootstrapPath"], "--print-debs", "--arch=amd64", "--variant=minbase",
                      "--force-check-gpg", "--keyring=" + str(keyring),
                      "--components=" + ",".join(COMPONENTS), "trixie", str(Path(work) / "target"),
                      (root / base["Id"]).as_uri()],
                     env=bootstrap_environment(request))
    names = output.decode("ascii").split()
    require(names and all(re.fullmatch(r"[a-z0-9][a-z0-9+.-]+", p) for p in names) and
            sorted(set(names)) == sorted(request["BootstrapPackages"]), "BootstrapPackageSelectionChanged")


def bootstrap_archives(root, request, keyring, metadata, download):
    # APT's final solution may choose updates/security versions newer than minbase. Obtain
    # debootstrap's real base-suite archives separately, rather than guessing those versions.
    base = next(r for r in request["Repositories"] if r["Suite"] == "trixie")
    selected = []
    if download:
        # Run both selection and download against the SAME captured file repository.
        # debootstrap adds ca-certificates for an HTTPS mirror, changing the minbase
        # selection. Refetching live Release metadata would also race the signed capture.
        for name in request["BootstrapPackages"]:
            candidates = [item for repo, item in metadata if repo == base["Id"] and item["Package"] == name]
            require(len(candidates) == 1, "AmbiguousBootstrapArchiveVersion")
            item = candidates[0]
            target_file = root / base["Id"] / item["Filename"]
            if not target_file.exists():
                fetch(base["OriginUri"], item["Filename"], target_file)
            verify_file(root / base["Id"], {"Path": item["Filename"], "Length": int(item["Size"]), "Sha256": item["SHA256"].upper()})
    with tempfile.TemporaryDirectory(prefix="igloo-bootstrap-download-") as work:
        target = Path(work) / "target"
        source = (root / base["Id"]).as_uri()
        run([request["DebootstrapPath"], "--download-only", "--arch=amd64", "--variant=minbase",
             "--force-check-gpg", "--keyring=" + str(keyring), "--components=" + ",".join(COMPONENTS),
             "trixie", str(target), source], env=bootstrap_environment(request))
        for archive in sorted((target / "var/cache/apt/archives").glob("*.deb")):
            data = archive.read_bytes()
            control = fields(run(["/usr/bin/dpkg-deb", "--field", str(archive), "Package", "Version", "Architecture"]))
            require(len(control) == 1, "BootstrapDebControlInvalid")
            identity = control[0]
            matches = [(repo, item) for repo, item in metadata if repo == base["Id"] and
                       all(item[k] == identity[k] for k in ("Package", "Version", "Architecture")) and
                       item["SHA256"].upper() == digest(data) and int(item["Size"]) == len(data)]
            require(len(matches) == 1, "BootstrapArchiveNotInAuthenticatedGeneration")
            repo, item = matches[0]
            file = descriptor(item["Filename"], data)
            selected.append({"RepositoryId": repo, "Name": item["Package"], "Version": item["Version"],
                             "Architecture": item["Architecture"], "File": file})
    require(sorted(p["Name"] for p in selected) == sorted(request["BootstrapPackages"]), "BootstrapArchiveClosureIncomplete")
    return sorted(selected, key=lambda p: p["Name"])


def bootstrap_environment(request):
    # Extracted, authenticated tool packages need not be installed on the build host.
    # A caller-supplied helper tree is part of the trusted runtime profile, not the bundle.
    environment = {"PATH": "/usr/sbin:/usr/bin:/sbin:/bin", "LC_ALL": "C"}
    if "DebootstrapDirectory" in request:
        root = Path(request["DebootstrapDirectory"])
        require(root.is_absolute() and root.is_dir() and not root.is_symlink(), "InvalidDebootstrapDirectory")
        require(runtime_tool(request).tree_identity(root) == request["DebootstrapTreeSha256"], "DebootstrapHelpersChanged")
        environment["DEBOOTSTRAP_DIR"] = str(root)
    return environment


def fetch(origin, relative_path, destination):
    url = origin.rstrip("/") + "/" + relative(relative_path)
    with urllib.request.urlopen(url, timeout=120) as stream:
        require(urllib.parse.urlsplit(stream.url).scheme == "https", "InsecureRedirect")
        data = stream.read(MAX_FILE + 1)
    require(len(data) <= MAX_FILE, "DownloadTooLarge")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("xb") as output:
        output.write(data)
    return data


def acquire_metadata(root, request, keyring, download, now):
    repos, indexes = [], []
    for repo in sorted(request["Repositories"], key=lambda r: r["Id"]):
        directory = root / repo["Id"]
        name = "dists/" + repo["Suite"] + "/InRelease"
        data = fetch(repo["OriginUri"], name, directory / name) if download else read_file(directory, name)
        release, signer = authenticate(data, keyring, request["SigningFingerprints"])
        hashes = release_index(release, repo["Suite"], now, request["MaximumMetadataAgeDays"])
        entries = []
        for component in COMPONENTS:
            short = component + "/binary-amd64/Packages.xz"
            require(short in hashes, "MissingSignedIndex")
            path = "dists/" + repo["Suite"] + "/" + short
            index = fetch(repo["OriginUri"], path, directory / path) if download else read_file(directory, path)
            require((len(index), digest(index)) == hashes[short], "SignedIndexHashMismatch")
            # Bound decompression too; corrupted indexes must never expand without limit.
            decoder = lzma.LZMADecompressor()
            plain = decoder.decompress(index, max_length=MAX_FILE + 1)
            require(decoder.eof and not decoder.unused_data and len(plain) <= MAX_FILE, "InvalidCompressedIndex")
            indexes.append((repo["Id"], plain))
            entries.append(descriptor(path, index))
        repos.append({**repo, "InRelease": descriptor(name, data), "ReleaseSha256": digest(release),
                      "SigningFingerprint": signer, "Indexes": entries})
    return repos, package_records(indexes)


def copy_metadata_snapshot(source, destination, request, keyring, expected_hash, now):
    """Copy only pinned, reauthenticated metadata; never refetch a newer snapshot mid-build."""
    data = read_file(source, "repositories.json")
    require(digest(data) == expected_hash, "MetadataSnapshotHashMismatch")
    repositories, _ = acquire_metadata(source, request, keyring, False, now)
    require(read_json(source / "repositories.json") == repositories, "MetadataSnapshotChanged")
    for repo in repositories:
        for item in [repo["InRelease"], *repo["Indexes"]]:
            data = verify_file(source / repo["Id"], item)
            path = destination / repo["Id"] / relative(item["Path"])
            path.parent.mkdir(parents=True, exist_ok=True)
            with path.open("xb") as output:
                output.write(data)


def execute(mode, root, request, expected_manifest_hash=None, metadata_snapshot=None, expected_metadata_hash=None):
    policy, keyring = validated_request(request)
    now = dt.datetime.now(dt.timezone.utc)
    if mode == "build":
        root.mkdir(exist_ok=False, parents=False)
        with (root / "debian-archive-keyring.gpg").open("xb") as output:
            output.write(keyring.read_bytes())
    else:
        require(root.is_dir() and not root.is_symlink(), "MissingBundle")
        manifest_data = read_file(root, "package-set.json")
        require(digest(manifest_data) == expected_manifest_hash, "PackageManifestHashMismatch")
    require(digest(read_file(root, "debian-archive-keyring.gpg")) == request["KeyringSha256"], "BundledKeyringChanged")
    if mode == "build" and metadata_snapshot is not None:
        copy_metadata_snapshot(metadata_snapshot, root, request, keyring, expected_metadata_hash, now)
    repositories, metadata = acquire_metadata(root, request, keyring, mode == "build" and metadata_snapshot is None, now)
    verify_bootstrap_selection(root, request, keyring)
    bootstrap = bootstrap_archives(root, request, keyring, metadata, mode == "build")
    selected, standard, apt_version, dependency_policy = apt_solve(root, request, policy, keyring, metadata)
    result = {"SchemaVersion": 1, "GenerationId": request["GenerationId"], "BuildId": request["BuildId"],
              "Release": "trixie", "Architecture": "amd64", "PolicySha256": request["PolicySha256"],
              "KeyringSha256": request["KeyringSha256"], "AptVersion": apt_version,
              "DebootstrapSha256": request["DebootstrapSha256"], "InstallRecommends": True, "InstallSuggests": False,
              "BootstrapPackages": sorted(set(request["BootstrapPackages"])), "StandardPackages": standard,
              "Repositories": repositories, "Packages": selected, "BootstrapArchives": bootstrap,
              "DependencyPolicy": dependency_policy, "RepositoryAliases": repository_aliases(selected, metadata)}
    result["RuntimeProfileSha256"] = request.get("RuntimeProfileSha256")
    if mode != "build":
        require(read_json(root / "package-set.json") == result, "PackageGenerationOrDependencyClosureChanged")
    def archive(package):
        repo = next(r for r in repositories if r["Id"] == package["RepositoryId"])
        if mode == "build" and not (root / repo["Id"] / package["File"]["Path"]).exists():
            fetch(repo["OriginUri"], package["File"]["Path"], root / repo["Id"] / package["File"]["Path"])
        data = verify_file(root / repo["Id"], package["File"])
        require(bool(data), "EmptyDeb")
    # Bounded acquisition only; no install commands and no mutable repository refresh.
    with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
        for _ in pool.map(archive, selected):
            pass
    for alias in result["RepositoryAliases"]:
        target = root / alias["RepositoryId"] / alias["File"]["Path"]
        if mode == "build" and not target.exists():
            source = next(p for p in selected if p["Name"] == alias["Name"])
            data = verify_file(root / source["RepositoryId"], source["File"])
            target.parent.mkdir(parents=True, exist_ok=True)
            with target.open("xb") as output:
                output.write(data)
        verify_file(root / alias["RepositoryId"], alias["File"])
    if mode == "build":
        # A failed build has no completed manifest. No overwrite or automatic resume.
        with (root / "package-set.json").open("xb") as output:
            output.write(json.dumps(result, separators=(",", ":")).encode("utf-8"))
            output.flush()
            os.fsync(output.fileno())
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("mode", choices=("build", "verify"))
    parser.add_argument("--request", required=True)
    parser.add_argument("--bundle", required=True)
    parser.add_argument("--expected-manifest-sha256")
    parser.add_argument("--metadata-snapshot")
    parser.add_argument("--expected-metadata-sha256")
    args = parser.parse_args()
    try:
        result = execute(args.mode, Path(args.bundle).absolute(), read_json(args.request), args.expected_manifest_sha256,
                         Path(args.metadata_snapshot).absolute() if args.metadata_snapshot else None, args.expected_metadata_sha256)
        print(json.dumps({"State": "Available", "GenerationId": result["GenerationId"], "Packages": len(result["Packages"])}))
    except ClosureRejected as error:
        print(json.dumps({"State": "Unsupported", "Code": "UnresolvedRecommends",
                          "SelectedButRejectedPackageCount": error.selected_count, "Dependencies": error.unresolved,
                          "DependencyPolicy": error.report}), file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, ImportError, subprocess.SubprocessError) as error:
        # No exception text/source contents/credentials in evidence. A partial bundle stays partial.
        state = ("AccessDenied" if isinstance(error, PermissionError) else "Absent" if isinstance(error, FileNotFoundError)
                 else "Unsupported" if isinstance(error, ImportError) else "Ambiguous" if isinstance(error, (ValueError, KeyError)) else "Unavailable")
        print(json.dumps({"State": state, "Code": type(error).__name__}), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
