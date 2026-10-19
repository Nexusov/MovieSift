"""Exercise distribution evidence against tiny real archives, not media or disk filling."""

import base64
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import tarfile
import tempfile
import unittest
import zipfile


SPECIFICATION = importlib.util.spec_from_file_location("inventory", Path(__file__).parents[1] / "distribution-inventory.py")
INVENTORY = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(INVENTORY)


class DistributionInventoryTests(unittest.TestCase):
    """Verify complete actual payload coverage and fail closed on missing evidence."""

    def setUp(self):
        """Create an isolated package with one exact NuGet payload and notice."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.root = self.directory / "TrackTrim-win-x64"
        self.root.mkdir()
        self.archive = self.directory / "TrackTrim-win-x64.zip"
        self.assets = self.directory / "assets.json"
        self.profile = self.directory / "profile.json"
        self.output = self.directory / "evidence"
        self.write("Sample.dll", b"published sample assembly")
        self.write("TrackTrim.dll", b"original application")
        self.write("unknown.dat", b"explicitly unknown")
        self.write("tools/licenses/corresponding-source/source.tar.xz", b"small source archive stand-in")
        self.json("tools/licenses/upstream-materials.json", [])
        self.json("build-provenance.json", {"runtimeIdentifier": "win-x64", "sourceRevision": "1" * 40, "worktreeDirty": False})
        self.json("tool-provenance.json", {
            "version": "102.0", "win-x64": {"sha256": "2" * 64},
            "source": {"url": "https://example.invalid/source.tar.xz", "sha256": INVENTORY.file_hash(self.root / "tools/licenses/corresponding-source/source.tar.xz")},
        })
        self.profile.write_text(json.dumps({"pinnedTools": {"win-x64": "2" * 64}, "components": [], "unresolved": []}), encoding="utf-8")
        cache = self.directory / "cache" / "sample" / "1.0.0"
        cache.mkdir(parents=True)
        self.nupkg = cache / "sample.1.0.0.nupkg"
        with zipfile.ZipFile(self.nupkg, "w") as package:
            package.writestr("lib/net10.0/Sample.dll", b"published sample assembly")
            package.writestr("LICENSE", b"original license text")
            package.writestr("sample.nuspec", b"package specification")
        signed_hash = base64.b64encode(hashlib.sha512(self.nupkg.read_bytes()).digest()).decode("ascii")
        self.nupkg.with_suffix(".nupkg.sha512").write_text(signed_hash, encoding="utf-8")
        self.assets.write_text(json.dumps({"packageFolders": {str(self.directory / "cache"): {}}}), encoding="utf-8")
        self.write("licenses/nuget/Sample/1.0.0/LICENSE", b"original license text")
        self.write("licenses/nuget/Sample/1.0.0/sample.nuspec", b"package specification")
        self.json("licenses/nuget/inventory.json", [{
            "id": "Sample", "version": "1.0.0", "license": "MIT", "sha512": "different-signature-independent-content-hash",
            "repositoryUrl": "https://example.invalid/sample", "repositoryCommit": "3" * 40,
            "includedLicenseFiles": ["LICENSE"], "specificationFile": "sample.nuspec", "supplementalMaterials": [],
        }])
        tool_records = []
        for path in (self.root / "tools").rglob("*"):

            if path.is_file():
                tool_records.append({"path": path.relative_to(self.root / "tools").as_posix(), "size": path.stat().st_size, "sha256": INVENTORY.file_hash(path), "upstreamArchiveSha256": ""})
        self.json("tools/file-provenance.json", tool_records)
        self.pack()

    def write(self, name, value):
        """Write a fixture payload under the owned temporary directory."""
        destination = self.root / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(value)

    def json(self, name, value):
        """Write a fixture evidence input with the real production schema."""
        self.write(name, json.dumps(value).encode("utf-8"))

    def pack(self, tar=False):
        """Create the real manifest and final ZIP or tar.gz from current fixture bytes."""
        records = INVENTORY.staged_files(self.root)
        manifest = "".join(f"{record['sha256']}  {name}\n" for name, record in records.items() if name != "SHA256SUMS")
        self.write("SHA256SUMS", manifest.encode("utf-8"))

        if tar:
            self.archive = self.directory / "TrackTrim-win-x64.tar.gz"
            with tarfile.open(self.archive, "w:gz") as package:
                package.add(self.root, arcname=self.root.name)
            return

        with zipfile.ZipFile(self.archive, "w") as package:
            for path in self.root.rglob("*"):

                if path.is_file():
                    package.write(path, arcname=self.root.name + "/" + path.relative_to(self.root).as_posix())

    def generate(self):
        """Run the public evidence entry point with only explicit fixture inputs."""
        with contextlib.redirect_stdout(io.StringIO()):
            return INVENTORY.generate(self.root, self.archive, self.assets, self.output, self.profile)

    def test_zip_accounts_for_every_actual_byte_and_explicit_unknown(self):
        """Report the final hash, every file, exact origin and unresolved payload."""
        report = self.generate()
        self.assertEqual(report["archive"]["sha256"], INVENTORY.file_hash(self.archive))
        self.assertEqual(len(report["files"]), len(INVENTORY.staged_files(self.root)))
        self.assertFalse(report["redistributionReady"])
        self.assertTrue(any(row["component"] == "unknown.dat" for row in report["unresolved"]))
        component = report["nugetComponents"][0]
        self.assertEqual(component["matchedPayloads"], ["Sample.dll"])
        self.assertNotEqual(component["restoreContentHash"], component["signedArchiveSha512"])
        self.assertEqual(len(component["materialEvidence"]), 2)

    def test_tar_archive_is_compared_to_staging(self):
        """Exercise the Linux archive format using real tiny tar members."""
        self.pack(tar=True)
        self.assertEqual(self.generate()["mechanicalChecks"]["archiveMatchesStaging"], "passed")

    def test_hidden_build_material_is_included_in_tar_inventory(self):
        """Keep dotfiles such as upstream .cmake.conf in complete file accounting."""
        self.write("docs/.cmake.conf", b"hidden source build material")
        self.pack(tar=True)
        self.assertTrue(any(row["path"] == "docs/.cmake.conf" for row in self.generate()["files"]))

    def test_unlisted_staged_file_fails(self):
        """An unmanifested payload cannot disappear from the accounting."""
        self.write("omitted.dll", b"unlisted")
        with self.assertRaisesRegex(ValueError, "manifest does not cover"):
            self.generate()

    def test_changed_manifested_payload_fails(self):
        """Reject staging changes after manifest construction."""
        self.write("Sample.dll", b"changed")
        with self.assertRaisesRegex(ValueError, "manifest mismatch"):
            self.generate()

    def test_final_archive_extra_file_fails(self):
        """The final archive must not contain unaccounted bytes."""
        with zipfile.ZipFile(self.archive, "a") as package:
            package.writestr(self.root.name + "/extra.dll", b"unexpected")
        with self.assertRaisesRegex(ValueError, "unexpected archive"):
            self.generate()

    def test_final_archive_changed_payload_fails(self):
        """Bind the report to archive contents rather than staging alone."""
        self.write("Sample.dll", b"different")
        self.pack()
        self.write("Sample.dll", b"published sample assembly")
        records = INVENTORY.staged_files(self.root)
        self.write("SHA256SUMS", "".join(f"{row['sha256']}  {name}\n" for name, row in records.items() if name != "SHA256SUMS").encode())
        with self.assertRaisesRegex(ValueError, "Archive/staging byte mismatch"):
            self.generate()

    def test_notice_must_match_original_package_not_only_manifest(self):
        """A stale notice remains invalid even if both stage and archive agree."""
        self.write("licenses/nuget/Sample/1.0.0/LICENSE", b"unrelated notice")
        self.pack()
        with self.assertRaisesRegex(ValueError, "differs from nupkg"):
            self.generate()

    def test_missing_required_notice_fails(self):
        """A missing included material is a visible evidence failure."""
        (self.root / "licenses/nuget/Sample/1.0.0/LICENSE").unlink()
        self.pack()
        with self.assertRaisesRegex(ValueError, "material is not shipped"):
            self.generate()

    def test_missing_original_nupkg_fails(self):
        """Do not silently replace original package evidence with the restore graph."""
        self.nupkg.unlink()
        with self.assertRaisesRegex(ValueError, "original nupkg missing"):
            self.generate()

    def test_nupkg_archive_sidecar_mismatch_fails(self):
        """Validate signed bytes without confusing NuGet content-hash semantics."""
        self.nupkg.with_suffix(".nupkg.sha512").write_text("wrong", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "archive/sidecar mismatch"):
            self.generate()

    def test_incorrect_source_archive_fails(self):
        """A similarly named source archive cannot satisfy the pinned material check."""
        self.write("tools/licenses/corresponding-source/source.tar.xz", b"wrong source")
        self.pack()
        with self.assertRaisesRegex(ValueError, "source archive is missing or changed"):
            self.generate()

    def test_unaccounted_tool_payload_fails(self):
        """Retained unused tool libraries still require acquisition evidence."""
        self.write("tools/app-root/usr/lib/unused.a", b"static archive")
        self.pack()
        with self.assertRaisesRegex(ValueError, "does not cover all retained"):
            self.generate()

    def test_wrong_native_observation_platform_is_excluded(self):
        """Never present another platform's researched materials as actual evidence."""
        self.profile.write_text(json.dumps({"pinnedTools": {"win-x64": "2" * 64}, "components": [{"runtimeIdentifier": "linux-x64", "availableMaterials": ["absent"], "component": "Linux-only"}], "unresolved": []}), encoding="utf-8")
        self.assertEqual(self.generate()["nativeComponentEvidence"], [])

    def test_escaping_archive_path_fails(self):
        """Reject ambiguous path names rather than publishing misleading inventory."""
        with zipfile.ZipFile(self.archive, "a") as package:
            package.writestr(self.root.name + "/../outside", b"escape")
        with self.assertRaisesRegex(ValueError, "Unsafe relative path"):
            self.generate()

    def test_missing_revision_fails(self):
        """An anonymous package cannot produce an accepted distribution report."""
        self.json("build-provenance.json", {"runtimeIdentifier": "win-x64", "sourceRevision": ""})
        self.pack()
        with self.assertRaisesRegex(ValueError, "exact build revision"):
            self.generate()


if __name__ == "__main__":
    unittest.main()
