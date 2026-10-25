"""Exercise candidate selection locally without authorizing or performing an upload."""

import copy
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

import test_review_materials as fixtures


SPECIFICATION = importlib.util.spec_from_file_location("candidate_retention", Path(__file__).parents[1] / "prepare-candidate-retention.py")
RETENTION = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(RETENTION)


class CandidateRetentionTests(unittest.TestCase):
    """Bind both actual archive formats to their checked source and matching material sets."""

    def setUp(self):
        """Reuse the existing tiny committed source/package fixture and material generator."""
        self.fixture = fixtures.ReviewMaterialTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.summary = self.fixture.prepare()
        self.summary_path = self.fixture.output / "material-summary.json"
        self.material_archive = self.fixture.output / self.summary["archive"]["name"]
        self.output = self.fixture.root / "candidate staging Кириллица"

    def prepare(self, **changes):
        """Invoke exactly the production preparation path with controlled run identities."""
        arguments = {
            "repository": self.fixture.repository, "archive": self.fixture.archive,
            "inventory_path": self.fixture.inventory_path, "materials": self.fixture.output,
            "runtime": self.fixture.runtime, "source_revision": self.fixture.revision,
            "run_id": "12345", "run_attempt": "2", "output": self.output,
        }
        arguments.update(changes)
        return RETENTION.prepare(**arguments)

    def save_summary(self, summary):
        """Change one compact report independently from the actual generated archives."""
        self.summary_path.write_text(json.dumps(summary), encoding="utf-8")

    def test_both_runtime_sets_retain_exact_bytes_and_only_five_allowlisted_files(self):
        """Keep original ZIP/tar bytes, matching source bundles and independently checked hashes."""
        for runtime in ("win-x64", "linux-x64"):
            with self.subTest(runtime=runtime):
                if runtime == "linux-x64":
                    self.fixture.runtime = runtime
                    self.fixture.output = self.fixture.root / "linux materials"
                    self.fixture.pack(tar=True)
                    self.summary = self.fixture.prepare()
                    self.summary_path = self.fixture.output / "material-summary.json"
                    self.material_archive = self.fixture.output / self.summary["archive"]["name"]
                output = self.fixture.root / runtime
                selection = self.prepare(output=output)
                expected = {
                    self.fixture.archive.name: self.fixture.archive,
                    self.material_archive.name: self.material_archive,
                    "distribution-" + runtime + ".json": self.fixture.inventory_path,
                    "material-summary.json": self.summary_path,
                }
                self.assertEqual(set(expected) | {"SHA256SUMS"}, {path.name for path in output.iterdir()})
                for name, path in expected.items():
                    self.assertEqual(path.read_bytes(), (output / name).read_bytes())
                sums = (output / "SHA256SUMS").read_text(encoding="utf-8").splitlines()
                self.assertEqual(len(expected), len(sums))
                for line in sums:
                    checksum, name = line.split("  ", 1)
                    self.assertIn(name, expected)
                    self.assertEqual(checksum, hashlib.sha256((output / name).read_bytes()).hexdigest())
                self.assertEqual("candidate-" + runtime + "-" + self.fixture.revision + "-run-12345-attempt-2", selection["artifactName"])
                self.assertEqual(self.material_archive.name, selection["materialsName"])
                self.assertNotIn(str(self.fixture.root), json.dumps(selection))

    def test_each_missing_required_input_rejects_the_whole_set(self):
        """Never stage a binary without the material archive or either identity report."""
        for path in (self.fixture.archive, self.material_archive, self.fixture.inventory_path, self.summary_path):
            with self.subTest(name=path.name):
                contents = path.read_bytes()
                path.unlink()
                with self.assertRaises(FileNotFoundError):
                    self.prepare()
                self.assertFalse(self.output.exists())
                path.write_bytes(contents)

    def test_cli_returns_the_machine_readable_selection_used_by_the_workflow(self):
        """Exercise argument parsing and the exact output boundary without any upload."""
        result = subprocess.check_output([
            sys.executable, str(Path(RETENTION.__file__)), "--repository", str(self.fixture.repository),
            "--archive", str(self.fixture.archive), "--inventory", str(self.fixture.inventory_path),
            "--materials", str(self.fixture.output), "--runtime", self.fixture.runtime,
            "--source-revision", self.fixture.revision, "--run-id", "12345", "--run-attempt", "2",
            "--output", str(self.output),
        ], text=True)
        selection = json.loads(result)
        self.assertEqual(self.material_archive.name, selection["materialsName"])
        self.assertIn(self.fixture.revision, selection["artifactName"])
        self.assertEqual(5, len(list(self.output.iterdir())))

    def test_changed_binary_or_material_bytes_fail_before_staging(self):
        """Hash actual archive bytes instead of trusting a successful historical build."""
        for path in (self.fixture.archive, self.material_archive):
            with self.subTest(name=path.name):
                original = path.read_bytes()
                path.write_bytes(original + b"controlled change")
                with self.assertRaisesRegex(ValueError, "archive name, size or SHA256"):
                    self.prepare()
                self.assertFalse(self.output.exists())
                path.write_bytes(original)

    def test_summary_identity_disagreements_reject_the_whole_set(self):
        """Reject another source, tree, runtime, binary identity or material identity."""
        variants = [
            {"schemaVersion": 2}, {"sourceRevision": "0" * 40}, {"sourceTree": "0" * 40},
            {"runtimeIdentifier": "linux-x64"}, {"binaryArchive": {**self.summary["binaryArchive"], "sha256": "0" * 64}},
            {"archive": {**self.summary["archive"], "sha256": "0" * 64}},
            {"archive": {**self.summary["archive"], "name": "../unrelated-source.tar.gz"}},
        ]
        for changed in variants:
            with self.subTest(changed=changed):
                self.save_summary({**self.summary, **changed})
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())

    def test_inventory_must_identify_the_actual_clean_checkout(self):
        """Preserve the existing source/tree/dirty checks and add exact checkout/runtime binding."""
        original = copy.deepcopy(self.fixture.inventory)
        for changed in ({"sourceRevision": "0" * 40}, {"sourceTree": "0" * 40},
                        {"worktreeDirty": True}, {"runtimeIdentifier": "linux-x64"}):
            with self.subTest(changed=changed):
                self.fixture.inventory = copy.deepcopy(original)
                self.fixture.inventory["build"].update(changed)
                self.fixture.save_inventory()
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())
        self.fixture.inventory = original
        self.fixture.save_inventory()
        self.fixture.git("-c", "user.name=Controlled fixture", "-c", "user.email=fixture@example.invalid",
                         "commit", "--quiet", "--allow-empty", "-m", "Advance checked revision")
        with self.assertRaisesRegex(ValueError, "checked revision"):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_invalid_runtime_revision_run_or_archive_name_is_rejected(self):
        """Keep artifact/output names free of arbitrary inputs or ambiguous source identities."""
        for changed in ({"runtime": "other"}, {"source_revision": "not-a-sha"},
                        {"run_id": "../other"}, {"run_attempt": "0"}):
            with self.subTest(changed=changed):
                with self.assertRaises(ValueError):
                    self.prepare(**changed)
                self.assertFalse(self.output.exists())
        renamed = self.fixture.archive.with_name("unrelated.zip")
        self.fixture.archive.rename(renamed)
        self.fixture.inventory["archive"]["name"] = renamed.name
        self.fixture.save_inventory()
        with self.assertRaisesRegex(ValueError, "checked revision"):
            self.prepare(archive=renamed)

    def test_existing_staging_is_not_reused_or_uploaded_with_unrelated_files(self):
        """Refuse a populated destination rather than widening the candidate allowlist."""
        self.output.mkdir()
        sentinel = self.output / "unrelated.txt"
        sentinel.write_bytes(b"preserve")
        with self.assertRaises(FileExistsError):
            self.prepare()
        self.assertEqual([sentinel], list(self.output.iterdir()))
        self.assertEqual(b"preserve", sentinel.read_bytes())

    def test_nonregular_input_is_rejected_before_reading(self):
        """A directory cannot substitute for a required JSON identity file."""
        self.summary_path.unlink()
        self.summary_path.mkdir()
        with self.assertRaisesRegex(ValueError, "regular files"):
            self.prepare()
        self.assertFalse(self.output.exists())

    def test_failed_or_changed_copy_has_no_completed_checksum_manifest(self):
        """A failed opted-in preparation cannot leave a successful candidate selection."""
        with mock.patch.object(RETENTION.shutil, "copyfile", side_effect=OSError("controlled copy failure")):
            with self.assertRaisesRegex(OSError, "controlled copy failure"):
                self.prepare()
        self.assertFalse((self.output / "SHA256SUMS").exists())
        self.output.rmdir()

        def changed_copy(source, destination):
            """Model a write that fails to preserve the validated source bytes."""
            destination.write_bytes(b"different archive bytes")

        with mock.patch.object(RETENTION.shutil, "copyfile", side_effect=changed_copy):
            with self.assertRaisesRegex(ValueError, "bytes changed"):
                self.prepare()
        self.assertFalse((self.output / "SHA256SUMS").exists())


if __name__ == "__main__":
    unittest.main()
