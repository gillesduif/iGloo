"""Deterministic Linux fixtures: real gpgv/APT, no install, mount or firmware calls."""
import datetime as dt
import email.utils
import importlib.util
import json
import lzma
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
import uuid
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[2]


def load(name, file):
    spec = importlib.util.spec_from_file_location(name, ROOT / "distros/debian/native" / file)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


bundle = load("offline_bundle", "offline_bundle.py")
journal = load("deployment_journal", "deployment_journal.py")


class AuthenticatedBundleTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temp = tempfile.TemporaryDirectory(prefix="igloo-debian-auth-test-")
        cls.root = Path(cls.temp.name)
        cls.gpg = cls.root / "gpg"
        cls.gpg.mkdir(mode=0o700)
        cls.tool(["gpg", "--homedir", str(cls.gpg), "--batch", "--passphrase", "", "--quick-generate-key",
                  "iGloo fixture <fixture@example.invalid>", "ed25519", "sign", "0"])
        listing = cls.tool(["gpg", "--homedir", str(cls.gpg), "--with-colons", "--list-keys"])
        cls.fingerprint = next(l.split(":")[9] for l in listing.decode().splitlines() if l.startswith("fpr:"))
        cls.keyring = cls.root / "keyring.gpg"
        cls.keyring.write_bytes(cls.tool(["gpg", "--homedir", str(cls.gpg), "--export"]))

    @classmethod
    def tearDownClass(cls):
        cls.temp.cleanup()

    @staticmethod
    def tool(args):
        return subprocess.run(args, stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                              stderr=subprocess.PIPE, check=True).stdout

    @classmethod
    def sign(cls, content):
        result = subprocess.run(["gpg", "--homedir", str(cls.gpg), "--batch", "--yes", "--passphrase", "",
                                 "--armor", "--clearsign"], input=content, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        return result.stdout

    def release(self, suite="trixie", arch="amd64", expired=False):
        now = dt.datetime.now(dt.timezone.utc)
        until = now + dt.timedelta(days=-1 if expired else 5)
        alias = {"trixie": "stable", "trixie-updates": "stable-updates", "trixie-security": "stable-security"}.get(suite, "testing")
        return (f"Origin: Debian\nLabel: Debian\nSuite: {alias}\nCodename: {suite}\nArchitectures: {arch}\n"
                f"Date: {email.utils.format_datetime(now - dt.timedelta(days=2))}\n"
                f"Valid-Until: {email.utils.format_datetime(until)}\n"
                f"SHA256:\n {'A' * 64} 10 main/binary-amd64/Packages.xz\n").encode()

    def test_valid_signature_and_release(self):
        release = self.release()
        plain, signer = bundle.authenticate(self.sign(release), self.keyring, [self.fingerprint])
        self.assertEqual(plain, release)
        self.assertEqual(signer, self.fingerprint)
        self.assertTrue(bundle.release_index(plain, "trixie", dt.datetime.now(dt.timezone.utc), 30))

    def test_modified_inrelease_rejected(self):
        signed = self.sign(self.release()).replace(b"Origin: Debian", b"Origin: EvilOS")
        with self.assertRaises(bundle.Rejected):
            bundle.authenticate(signed, self.keyring, [self.fingerprint])

    def test_wrong_key_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.authenticate(self.sign(self.release()), self.keyring, ["B" * 40])

    def test_wrong_release_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.release_index(self.release(suite="forky"), "trixie", dt.datetime.now(dt.timezone.utc), 30)

    def test_wrong_architecture_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.release_index(self.release(arch="arm64"), "trixie", dt.datetime.now(dt.timezone.utc), 30)

    def test_expired_release_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.release_index(self.release(expired=True), "trixie", dt.datetime.now(dt.timezone.utc), 30)

    def test_modified_deb_bytes_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            data = b"archive fixture"
            (root / "fixture.deb").write_bytes(data + b"changed")
            with self.assertRaises(bundle.Rejected):
                bundle.verify_file(root, bundle.descriptor("fixture.deb", data))

    def test_missing_deb_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.verify_file(self.root, bundle.descriptor("absent.deb", b"archive"))

    def test_duplicate_release_field_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.fields(b"Codename: trixie\nCodename: forky\n")

    def test_package_wrong_architecture_rejected(self):
        with self.assertRaises(bundle.Rejected):
            bundle.package_records([("fixture", b"Package: fixture\nArchitecture: i386\n")])

    def test_symlinked_artifact_rejected(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            (root / "alias").symlink_to(self.keyring)
            with self.assertRaises(bundle.Rejected):
                bundle.read_file(root, "alias")

    def make_repos(self, root, broken=False, missing_recommendation=False, gvfs=False, gvfs_extra="", wsdd=False):
        repositories = [{"Id": {"trixie": "debian", "trixie-updates": "debian-updates", "trixie-security": "debian-security"}[s],
                         "Suite": s, "OriginUri": "https://deb.debian.org/debian"} for s in bundle.SUITES]
        metadata = []
        packages = []
        for name, extra in (("igloo-base", "Priority: required\n"), ("igloo-standard", "Priority: standard\n"),
                            ("igloo-desktop", "Depends: igloo-library\nRecommends: igloo-recommended\n"),
                            ("igloo-library", ""), ("igloo-recommended", "")):
            if broken and name == "igloo-library":
                continue
            if missing_recommendation and name == "igloo-recommended":
                continue
            packages.append((f"Package: {name}\nVersion: 1.0\nArchitecture: amd64\nMaintainer: Fixture <fixture@example.invalid>\n"
                             f"Filename: pool/main/i/{name}/{name}_1.0_amd64.deb\nSize: 7\nSHA256: {'A' * 64}\n"
                             f"Description: fixture\n{extra}\n").encode())
        if gvfs:
            packages.append((f"Package: gvfs-backends\nVersion: 1.57.2-2+deb13u1\nArchitecture: amd64\n"
                             f"Filename: pool/gvfs.deb\nSize: 7\nSHA256: {'A' * 64}\nDescription: fixture\n"
                             f"Recommends: wsdd{gvfs_extra}\n\n").encode())
        if wsdd:
            packages.append((f"Package: wsdd\nVersion: 1.0\nArchitecture: all\nFilename: pool/wsdd.deb\n"
                             f"Size: 7\nSHA256: {'B' * 64}\nDescription: fixture\n\n").encode())
        for repo in repositories:
            indexes = []
            for component in bundle.COMPONENTS:
                plain = b"".join(packages) if component == "main" and repo["Suite"] == "trixie" else b""
                data = lzma.compress(plain)
                path = component + "/binary-amd64/Packages.xz"
                target = root / repo["Id"] / "dists" / repo["Suite"] / path
                target.parent.mkdir(parents=True)
                target.write_bytes(data)
                indexes.append(f" {bundle.digest(data).lower()} {len(data)} {path}\n")
                indexes.append(f" {bundle.digest(plain).lower()} {len(plain)} {path[:-3]}\n")
                metadata.append((repo["Id"], plain))
            release = self.release(repo["Suite"]).split(b"SHA256:")[0] + b"Components: main contrib non-free non-free-firmware\nSHA256:\n" + "".join(indexes).encode()
            (root / repo["Id"] / "dists" / repo["Suite"] / "InRelease").write_bytes(self.sign(release))
        return {"Repositories": repositories, "BootstrapPackages": ["igloo-base"]}, bundle.package_records(metadata)

    def test_real_apt_solver_includes_dependency_and_recommends(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            request, records = self.make_repos(root)
            packages, standard, version, report = bundle.apt_solve(root, request, {"Packages": ["igloo-desktop"]}, self.keyring, records)
            self.assertEqual({p["Name"] for p in packages}, {"igloo-base", "igloo-standard", "igloo-desktop", "igloo-library", "igloo-recommended"})
            self.assertEqual(standard, ["igloo-base", "igloo-standard"])
            self.assertTrue(version)
            self.assertEqual(report["OriginalUnresolved"], [])

    def test_real_apt_solver_rejects_incomplete_dependency(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            request, records = self.make_repos(root, broken=True)
            with self.assertRaisesRegex(bundle.Rejected, "AptDependencyClosureBroken|SolverDroppedRequiredPackage"):
                bundle.apt_solve(root, request, {"Packages": ["igloo-desktop"]}, self.keyring, records)

    def test_real_apt_solver_reports_missing_recommends_without_omitting_it(self):
        with tempfile.TemporaryDirectory() as name:
            request, records = self.make_repos(Path(name), missing_recommendation=True)
            with self.assertRaises(bundle.ClosureRejected) as rejected:
                bundle.apt_solve(Path(name), request, {"Packages": ["igloo-desktop"]}, self.keyring, records)
            self.assertEqual(rejected.exception.unresolved, ["igloo-desktop:igloo-recommended"])
            self.assertEqual(rejected.exception.selected_count, 4)

    def solve_gvfs(self, root, request, records):
        policy = {**reviewed_policy(), "Packages": ["igloo-desktop", "gvfs-backends"]}
        original = bundle.recommendation_policy_report
        with patch.object(bundle, "recommendation_policy_report", side_effect=lambda p, u, n: original(p, u, REVIEW_TIME)):
            return bundle.apt_solve(root, request, policy, self.keyring, records)

    def test_signed_solver_applies_exact_exception_and_reports_original(self):
        with tempfile.TemporaryDirectory() as name:
            request, records = self.make_repos(Path(name), gvfs=True)
            packages, _, _, report = self.solve_gvfs(Path(name), request, records)
            self.assertEqual(len(packages), 6)
            self.assertEqual(report["OriginalUnresolved"], [missing_wsdd()])
            self.assertEqual(report["AppliedExceptions"][0]["ExceptionId"], bundle.WSDD_EXCEPTION_ID)
            self.assertEqual(report["RemainingUnresolved"], [])

    def test_signed_solver_new_wsdd_candidate_resolves_without_exception(self):
        with tempfile.TemporaryDirectory() as name:
            request, records = self.make_repos(Path(name), gvfs=True, wsdd=True)
            packages, _, _, report = self.solve_gvfs(Path(name), request, records)
            self.assertIn("wsdd", [p["Name"] for p in packages])
            self.assertEqual(report["AppliedExceptions"], [])

    def test_signed_solver_second_missing_recommend_blocks_complete_closure(self):
        with tempfile.TemporaryDirectory() as name:
            request, records = self.make_repos(Path(name), gvfs=True, gvfs_extra=", missing-other")
            with self.assertRaises(bundle.ClosureRejected) as rejected:
                self.solve_gvfs(Path(name), request, records)
            self.assertEqual(len(rejected.exception.report["AppliedExceptions"]), 1)
            self.assertEqual(rejected.exception.unresolved, ["gvfs-backends:missing-other"])

    def test_wrong_suite_alias_rejected_even_with_right_codename(self):
        with self.assertRaisesRegex(bundle.Rejected, "ReleaseIdentityMismatch"):
            bundle.release_index(self.release().replace(b"Suite: stable", b"Suite: testing"), "trixie", REVIEW_TIME, 90)


REVIEW_TIME = dt.datetime(2026, 9, 28, tzinfo=dt.timezone.utc)


def reviewed_policy():
    return {"RecommendationExceptions": [{**bundle.WSDD_SELECTOR, "Id": bundle.WSDD_EXCEPTION_ID,
            "Release": "trixie", "Reason": "Reviewed fixture", "ProductImpact": "No automatic WS-Discovery",
            "EvidenceUri": "https://bugs.debian.org/1110689", "ReviewedUtc": "2026-09-28T00:00:00+00:00",
            "ReviewBeforeUtc": "2026-10-28T00:00:00+00:00", "ReviewCondition": "Exact condition changes"}]}


def missing_wsdd():
    return {**bundle.WSDD_SELECTOR, "CandidateAvailable": False}


class RecommendationPolicyTests(unittest.TestCase):
    def test_only_exact_absent_relation_allowed(self):
        result = bundle.recommendation_policy_report(reviewed_policy(), [missing_wsdd()], REVIEW_TIME)
        self.assertEqual(len(result["AppliedExceptions"]), 1)
        self.assertEqual(result["RemainingUnresolved"], [])

    def test_each_relation_dimension_is_fail_closed(self):
        for key, value in (("RepositoryId", "debian-security"), ("Suite", "trixie-security"),
                           ("Package", "gnome"), ("Version", "1.57.2-3"), ("Architecture", "all"),
                           ("Kind", "Depends"), ("Kind", "PreDepends"), ("Relation", "wsdd (>= 1)"),
                           ("Relation", "wsdd | wsdd2"), ("CandidateAvailable", True)):
            with self.subTest(key=key, value=value):
                relation = {**missing_wsdd(), key: value}
                report = bundle.recommendation_policy_report(reviewed_policy(), [relation], REVIEW_TIME)
                self.assertEqual(report["AppliedExceptions"], [])
                self.assertEqual(report["RemainingUnresolved"], [relation])

    def test_no_declared_policy_means_no_exception(self):
        self.assertEqual(bundle.recommendation_policy_report({}, [missing_wsdd()], REVIEW_TIME)["RemainingUnresolved"], [missing_wsdd()])

    def test_duplicate_relation_not_consumed_twice(self):
        result = bundle.recommendation_policy_report(reviewed_policy(), [missing_wsdd()] * 2, REVIEW_TIME)
        self.assertEqual(len(result["RemainingUnresolved"]), 1)

    def test_review_expiry_and_before_review_fail(self):
        for now in (REVIEW_TIME - dt.timedelta(seconds=1), REVIEW_TIME + dt.timedelta(days=30)):
            with self.assertRaisesRegex(bundle.Rejected, "RecommendationPolicyReviewRequired"):
                bundle.recommendation_policy_report(reviewed_policy(), [], now)

    def test_unknown_rule_and_widened_rule_rejected(self):
        for key, value in (("Id", "ignore-all"), ("Version", "*"), ("Relation", "wsdd2"), ("ReviewBeforeUtc", "2099-01-01T00:00:00+00:00")):
            policy = reviewed_policy(); policy["RecommendationExceptions"][0][key] = value
            with self.assertRaisesRegex(bundle.Rejected, "UnknownRecommendationPolicy"):
                bundle.recommendation_policy_report(policy, [], REVIEW_TIME)

    def test_snapshot_hash_mismatch_prevents_copy(self):
        with tempfile.TemporaryDirectory() as name:
            source = Path(name) / "source"; source.mkdir()
            (source / "repositories.json").write_bytes(b"[]")
            destination = Path(name) / "new"
            with self.assertRaisesRegex(bundle.Rejected, "MetadataSnapshotHashMismatch"):
                bundle.copy_metadata_snapshot(source, destination, {}, None, "A" * 64, REVIEW_TIME)
            self.assertFalse(destination.exists())

    def test_identical_signed_bytes_at_different_pool_paths_preserve_provenance(self):
        item = {"Package": "fixture", "Version": "1", "Architecture": "amd64", "Size": "7",
                "SHA256": "A" * 64, "Filename": "pool/main/f/fixture.deb"}
        other = {**item, "Filename": "pool/updates/main/f/fixture.deb"}
        matches = [("debian-security", other), ("debian", item)]
        self.assertEqual(bundle.repository_choice(matches), ("debian", item))
        selected = [{"RepositoryId": "debian", "Name": "fixture", "Version": "1", "Architecture": "amd64",
                     "File": {"Path": item["Filename"], "Length": 7, "Sha256": "A" * 64}}]
        aliases = bundle.repository_aliases(selected, matches)
        self.assertEqual(aliases[0]["RepositoryId"], "debian-security")
        self.assertEqual(aliases[0]["File"]["Path"], other["Filename"])

    def test_conflicting_bytes_at_same_package_version_rejected(self):
        for field, changed in (("SHA256", "B" * 64), ("Size", "8")):
            item = {"SHA256": "A" * 64, "Size": "7", "Filename": "pool/a.deb"}
            with self.assertRaisesRegex(bundle.Rejected, "ConflictingRepositoryPackage"):
                bundle.repository_choice([("debian", item), ("debian-security", {**item, field: changed})])


class DurableJournalTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="igloo-debian-journal-test-")
        self.store = self.temp.name
        os.chmod(self.store, 0o700)
        self.generation = str(uuid.uuid4())
        self.plan = "A" * 64
        self.data = json.dumps({"PlanSha256": self.plan, "Stages": ["IntentDurable"]}).encode()
        journal.perform(self.store, self.generation, self.plan, "reserve")

    def tearDown(self):
        self.temp.cleanup()

    def test_durable_independent_process_reopen(self):
        name = journal.perform(self.store, self.generation, self.plan, "append", self.data).decode()
        result = subprocess.run(["/usr/bin/python3", "-I", str(ROOT / "distros/debian/native/deployment_journal.py"), "read",
                                 "--store", self.store, "--generation", self.generation, "--plan-hash", self.plan, "--reference", name],
                                stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True)
        self.assertEqual(result.stdout, self.data)

    def test_no_blind_reservation_retry(self):
        with self.assertRaises(FileExistsError):
            journal.perform(self.store, self.generation, self.plan, "reserve")

    def test_changed_plan_rejected(self):
        with self.assertRaises(ValueError):
            journal.perform(self.store, self.generation, "B" * 64, "append", self.data)

    def test_symlink_readback_rejected(self):
        name = "00000000-" + "A" * 64 + ".json"
        (Path(self.store) / self.generation / name).symlink_to("plan.sha256")
        with self.assertRaises(OSError):
            journal.perform(self.store, self.generation, self.plan, "read", reference=name)

    def test_missing_sequence_rejected(self):
        (Path(self.store) / self.generation / "00000001.json").write_bytes(self.data)
        with self.assertRaises(ValueError):
            journal.perform(self.store, self.generation, self.plan, "append", self.data)

    def test_unexpected_permissions_rejected(self):
        os.chmod(self.store, 0o755)
        with self.assertRaises(ValueError):
            journal.perform(self.store, self.generation, self.plan, "append", self.data)

    def test_reference_cannot_escape_generation(self):
        with self.assertRaises(ValueError):
            journal.perform(self.store, self.generation, self.plan, "read", reference="../plan.sha256")

    def test_corrupt_checkpoint_blocks_reopen_and_future_append(self):
        name = journal.perform(self.store, self.generation, self.plan, "append", self.data).decode()
        (Path(self.store) / self.generation / name).write_bytes(self.data + b" ")
        with self.assertRaisesRegex(ValueError, "integrity mismatch"):
            journal.perform(self.store, self.generation, self.plan, "read", reference=name)
        with self.assertRaisesRegex(ValueError, "Previous checkpoint corrupt"):
            journal.perform(self.store, self.generation, self.plan, "append", self.data)

    def test_fsync_failure_never_reports_success_or_removes_partial_file(self):
        with patch.object(journal.os, "fsync", side_effect=OSError("fixture fsync failure")):
            with self.assertRaises(OSError):
                journal.perform(self.store, self.generation, self.plan, "append", self.data)
        self.assertEqual(len(list((Path(self.store) / self.generation).glob("*.json"))), 1)


if __name__ == "__main__":
    unittest.main()
