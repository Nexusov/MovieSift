"""Bind review source/material bundles to tiny real packages and Git commits."""

import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tempfile
import unittest
import warnings
import zipfile


SPECIFICATION = importlib.util.spec_from_file_location("review_materials", Path(__file__).parents[1] / "prepare-review-materials.py")
MATERIALS = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(MATERIALS)
LICENSE = b"MIT License\n\nCopyright (c) 2026 Nexusov\n\nControlled license fixture.\n"


class ReviewMaterialTests(unittest.TestCase):
    """Keep source provenance, license inclusion and material bytes mechanically honest."""

    def setUp(self):
        """Create an isolated committed source and a small actual Windows package."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.repository = self.root / "source Кириллица"
        self.repository.mkdir()
        self.git("init", "--quiet")
        self.git("config", "core.autocrlf", "true")
        (self.repository / ".gitattributes").write_text("* text=auto eol=lf\n", encoding="utf-8")
        (self.repository / "LICENSE").write_bytes(LICENSE)
        (self.repository / "build").mkdir()
        (self.repository / "build/recipe.txt").write_text("controlled build recipe", encoding="utf-8")
        self.git("add", ".gitattributes", "LICENSE", "build/recipe.txt")
        self.git("-c", "user.name=Controlled fixture", "-c", "user.email=fixture@example.invalid", "commit", "--quiet", "-m", "Create controlled source")
        self.revision = self.git("rev-parse", "HEAD").strip()
        self.runtime = "win-x64"
        self.archive = self.root / "TrackTrim-win-x64.zip"
        self.inventory_path = self.root / "distribution.json"
        self.output = self.root / "materials"
        self.payloads = {name: ("controlled " + name).encode() for name in MATERIALS.REQUIRED_RECORDS}
        self.payloads.update({
            "LICENSE": LICENSE,
            "mkv-source-provenance.json": b"controlled MKV provenance",
            "tools/licenses/source.tar.xz": b"controlled upstream source archive",
            "licenses/nuget/notice.txt": b"controlled upstream notice",
            "TrackTrim.exe": b"standalone apphost must not be copied",
            "tools/mkvmerge.exe": b"standalone worker must not be copied",
        })
        self.pack()

    def git(self, *arguments):
        """Mutate only the test's temporary Git repository."""
        return subprocess.check_output(["git", "-C", str(self.repository), *arguments], text=True, stderr=subprocess.STDOUT)

    def pack(self, tar=False):
        """Build a real small archive and matching complete inventory."""
        self.archive = self.root / ("TrackTrim-" + self.runtime + (".tar.gz" if tar else ".zip"))
        prefix = "TrackTrim-" + self.runtime + "/"

        if tar:
            with tarfile.open(self.archive, "w:gz") as archive:
                for name, contents in self.payloads.items():
                    member = tarfile.TarInfo(prefix + name)
                    member.size = len(contents)
                    archive.addfile(member, io.BytesIO(contents))
        else:
            with zipfile.ZipFile(self.archive, "w") as archive:
                for name, contents in self.payloads.items():
                    archive.writestr(prefix + name, contents)
        self.inventory = {
            "schemaVersion": 1,
            "build": {"sourceRevision": self.revision, "sourceTree": self.git("rev-parse", "HEAD^{tree}").strip(),
                      "worktreeDirty": False, "runtimeIdentifier": self.runtime},
            "archive": MATERIALS.file_record(self.archive),
            "files": [{"path": name, "size": len(contents), "sha256": hashlib.sha256(contents).hexdigest()}
                      for name, contents in self.payloads.items()],
        }
        self.save_inventory()

    def save_inventory(self):
        """Persist controlled changed evidence independently of package bytes."""
        self.inventory_path.write_text(json.dumps(self.inventory), encoding="utf-8")

    def prepare(self):
        """Exercise the same function invoked by platform CI."""
        return MATERIALS.prepare(self.repository, self.archive, self.inventory_path, self.output)

    def test_exact_materials_license_source_and_summary_are_bound_without_workers(self):
        """Copy material bytes and committed source, keeping compact public-safe evidence."""
        self.assertEqual(LICENSE, MATERIALS.git_output(self.repository, "show", self.revision + ":LICENSE"))
        summary = self.prepare()
        self.assertEqual(self.inventory["archive"], summary["binaryArchive"])
        self.assertEqual(self.revision, summary["sourceRevision"])
        self.assertEqual("MIT", summary["originalApplicationLicense"]["spdxId"])
        self.assertEqual("Copyright (c) 2026 Nexusov", summary["originalApplicationLicense"]["copyright"])
        self.assertFalse(summary["publicDeliveryPerformed"])
        bundle = self.output / summary["archive"]["name"]
        self.assertEqual(MATERIALS.file_record(bundle), summary["archive"])
        with tarfile.open(bundle, "r:gz") as archive:
            prefix = "TrackTrim-review-materials/"
            names = set(archive.getnames())
            self.assertNotIn(prefix + self.runtime + "/tools/mkvmerge.exe", names)
            self.assertNotIn(prefix + self.runtime + "/TrackTrim.exe", names)
            self.assertEqual(LICENSE, archive.extractfile(prefix + self.runtime + "/LICENSE").read())
            self.assertIn(prefix + self.runtime + "/mkv-source-provenance.json", names)
            source = archive.extractfile(prefix + summary["sourceArchive"]["name"]).read()
            self.assertEqual(summary["sourceArchive"]["sha256"], hashlib.sha256(source).hexdigest())
            with tarfile.open(fileobj=io.BytesIO(source), mode="r:gz") as source_archive:
                self.assertEqual(LICENSE, source_archive.extractfile("TrackTrim-source-" + self.revision[:12] + "/LICENSE").read())
            provenance = json.load(archive.extractfile(prefix + "materials-provenance.json"))
            instructions = archive.extractfile(prefix + "READ-ME-FIRST.txt").read().decode()
            self.assertIn("public repository are not private", instructions)
            self.assertIn("preparing this bundle does not publish it", instructions)
            for row in provenance["copiedMaterials"]:
                content = archive.extractfile(prefix + self.runtime + "/" + row["path"]).read()
                self.assertEqual(row["size"], len(content))
                self.assertEqual(row["sha256"], hashlib.sha256(content).hexdigest())
            manifest = archive.extractfile(prefix + "SHA256SUMS").read().decode()
            for line in manifest.splitlines():
                expected, name = line.split("  ", 1)
                self.assertEqual(expected, hashlib.sha256(archive.extractfile(prefix + name).read()).hexdigest())
        self.assertNotIn(str(self.root), (self.output / "material-summary.json").read_text(encoding="utf-8"))

    def test_linux_tar_uses_the_same_complete_material_policy(self):
        """Use a real tar archive without inventing native Linux execution evidence."""
        self.runtime = "linux-x64"
        self.payloads.pop("mkv-source-provenance.json")
        self.pack(tar=True)
        self.assertEqual("linux-x64", self.prepare()["runtimeIdentifier"])

    def test_missing_pending_or_source_mismatched_license_is_rejected(self):
        """Reject an omitted or obsolete license state even when the package is self-consistent."""
        for mode in ("missing", "pending", "changed"):
            with self.subTest(mode=mode):
                self.payloads["LICENSE"] = LICENSE
                self.payloads.pop(MATERIALS.PENDING_MARKER, None)

                if mode == "missing":
                    self.payloads.pop("LICENSE")

                if mode == "pending":
                    self.payloads[MATERIALS.PENDING_MARKER] = b"old pending notice"

                if mode == "changed":
                    self.payloads["LICENSE"] = LICENSE + b"unreviewed amendment"
                self.pack()
                with self.assertRaisesRegex(ValueError, "required|pending|LICENSE differs"):
                    self.prepare()
                self.assertFalse(self.output.exists())

    def test_dirty_unresolved_or_different_source_tree_is_rejected(self):
        """Never attribute a package to an unavailable or different committed source."""
        for changed in ({"worktreeDirty": True}, {"sourceRevision": "0" * 40}, {"sourceTree": "0" * 40}):
            with self.subTest(changed=changed):
                self.pack()
                self.inventory["build"].update(changed)
                self.save_inventory()
                with self.assertRaises(ValueError):
                    self.prepare()
                self.assertFalse(self.output.exists())

    def test_changed_archive_or_material_bytes_are_rejected(self):
        """Check final archive identity and included material bytes, not only names."""
        self.inventory["archive"]["size"] += 1
        self.save_inventory()
        with self.assertRaisesRegex(ValueError, "archive name, size or SHA256"):
            self.prepare()
        self.pack()
        next(row for row in self.inventory["files"] if row["path"] == "licenses/nuget/notice.txt")["sha256"] = "0" * 64
        self.save_inventory()
        with self.assertRaisesRegex(ValueError, "material differs"):
            self.prepare()
        self.assertFalse((self.output / "material-summary.json").exists())

    def test_missing_duplicate_unsafe_or_nonregular_members_fail(self):
        """Never accept a renamed traversal, duplicate or link as included materials."""
        for mode in ("missing", "duplicate", "unsafe", "symlink"):
            with self.subTest(mode=mode):
                self.output = self.root / ("materials-" + mode)
                self.pack()
                prefix = "TrackTrim-" + self.runtime + "/"
                with zipfile.ZipFile(self.archive, "w") as archive:
                    for name, contents in self.payloads.items():

                        if mode == "missing" and name == "licenses/nuget/notice.txt":
                            continue
                        archive.writestr(prefix + name, contents)

                    if mode == "duplicate":
                        with warnings.catch_warnings():
                            warnings.simplefilter("ignore", UserWarning)
                            archive.writestr(prefix + "LICENSE", LICENSE)

                    if mode == "unsafe":
                        archive.writestr(prefix + "../outside.txt", b"escape")

                    if mode == "symlink":
                        member = zipfile.ZipInfo(prefix + "link")
                        member.create_system = 3
                        member.external_attr = (0o120777 << 16)
                        archive.writestr(member, b"LICENSE")
                self.inventory["archive"] = MATERIALS.file_record(self.archive)
                self.save_inventory()
                with self.assertRaisesRegex(ValueError, "absent|Duplicate|Unsafe|nonregular"):
                    self.prepare()
                self.assertFalse((self.output / "material-summary.json").exists())


if __name__ == "__main__":
    unittest.main()
