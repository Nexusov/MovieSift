"""Keep demonstrated upstream attributions in the actual portable collection path."""

import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


BUILD_DIRECTORY = Path(__file__).parents[1]
AVALONIA_NOTICE = "avalonia-12.1.4-notice"
HARFBUZZ_COPYING = "harfbuzz-native-8.3.1.3-copying"


class LicenseCollectionTests(unittest.TestCase):
    """Verify exact required mappings and execute the collector with controlled caches."""

    def setUp(self):
        """Read the real pins and reserve all synthetic packages in one owned directory."""
        self.materials = json.loads((BUILD_DIRECTORY / "license-materials.lock.json").read_text(encoding="utf-8"))["materials"]
        self.temporary = tempfile.TemporaryDirectory(prefix="TrackTrim notice checks ")
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)

    def material(self, identifier):
        """Require a known upstream material rather than silently omitting its test."""
        matching = [material for material in self.materials if material["id"] == identifier]
        self.assertEqual(len(matching), 1, f"Required upstream notice is not pinned exactly once: {identifier}")
        return matching[0]

    def test_avalonia_notice_is_pinned_to_shipped_package_commit(self):
        """The root MIT license cannot replace the compiled Metsys.Bson BSD notice."""
        material = self.material(AVALONIA_NOTICE)
        self.assertEqual(material["version"], "12.1.4")
        self.assertEqual(material["repositoryCommit"], "2d79ba55fae1b1e71c69f93a668080c9029e6a31")
        self.assertEqual(material["sha256"], "dd6f80852d16320c865a2e403ffd1a8b07a0e34c5b37122c0bc2343639ff68e6")
        self.assertEqual(material["url"], "https://raw.githubusercontent.com/AvaloniaUI/Avalonia/2d79ba55fae1b1e71c69f93a668080c9029e6a31/NOTICE.md")
        self.assertEqual(material["fileName"], "upstream/NOTICE.md")
        owning_packages = {"Avalonia", "Avalonia.Remote.Protocol"}
        self.assertTrue(owning_packages.issubset(material["packageIds"]))

    def test_harfbuzz_copying_is_pinned_for_both_shipped_native_packages(self):
        """Keep the actual native dependency's current copyright list, not its old bundle."""
        material = self.material(HARFBUZZ_COPYING)
        self.assertEqual(material["version"], "8.3.1.3")
        self.assertEqual(material["repositoryCommit"], "2888c737ad016d584c74525e2d35db5097ea8576")
        self.assertEqual(material["sha256"], "ba8f810f2455c2f08e2d56bb49b72f37fcf68f1f4fade38977cfd7372050ad64")
        self.assertEqual(material["url"], "https://raw.githubusercontent.com/harfbuzz/harfbuzz/2b3631a866b3077d9d675caa4ec9010b342b5a7c/COPYING")
        self.assertEqual(material["fileName"], "upstream/harfbuzz-COPYING")
        self.assertEqual(set(material["packageIds"]), {"HarfBuzzSharp.NativeAssets.Win32", "HarfBuzzSharp.NativeAssets.Linux"})

    def fixture(self):
        """Copy the unmodified collector and inject tiny cached bytes for an offline test."""
        runner = self.directory / "collector"
        runner.mkdir()
        shutil.copyfile(BUILD_DIRECTORY / "collect-licenses.ps1", runner / "collect-licenses.ps1")
        cache = self.directory / "material cache"
        cache.mkdir()
        packages = self.directory / "packages"
        libraries = {}
        materials = []

        for identifier in (AVALONIA_NOTICE, HARFBUZZ_COPYING):
            material = dict(self.material(identifier))
            payload = f"Controlled notice bytes for {identifier}\n".encode("utf-8")
            material["sha256"] = hashlib.sha256(payload).hexdigest()
            material["url"] = f"https://example.invalid/{identifier}"
            (cache / identifier).write_bytes(payload)
            materials.append(material)

            for package_identifier in material["packageIds"]:
                package_path = f"{package_identifier.lower()}/{material['version']}"
                package = packages / package_path
                package.mkdir(parents=True)
                specification = (
                    '<package><metadata><license type="expression">MIT</license>'
                    f'<repository type="git" url="https://example.invalid/package" commit="{material["repositoryCommit"]}" />'
                    '</metadata></package>'
                )
                (package / "package.nuspec").write_text(specification, encoding="utf-8")
                libraries[f"{package_identifier}/{material['version']}"] = {"type": "package", "path": package_path, "sha512": "controlled-content-hash"}

        (runner / "license-materials.lock.json").write_text(json.dumps({"materials": materials}), encoding="utf-8")
        assets = self.directory / "assets.json"
        assets.write_text(json.dumps({"packageFolders": {str(packages): {}}, "libraries": libraries}), encoding="utf-8")
        return runner, cache, assets

    def collect(self, runner, cache, assets):
        """Execute the real PowerShell entry point without network or restored media tools."""
        executable = shutil.which("pwsh")
        self.assertIsNotNone(executable, "PowerShell is required for the portable license-collection regression.")
        output = self.directory / "collected"
        result = subprocess.run([
            executable, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(runner / "collect-licenses.ps1"),
            "-AssetsFile", str(assets), "-Destination", str(output), "-CacheDirectory", str(cache),
        ], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False)
        return result, output

    def test_collector_includes_notice_bytes_and_inventory_for_actual_owning_packages(self):
        """Supplemental notices reach every mapped package with their verified hashes."""
        runner, cache, assets = self.fixture()
        result, output = self.collect(runner, cache, assets)
        self.assertEqual(result.returncode, 0, result.stderr)
        inventory = json.loads((output / "inventory.json").read_text(encoding="utf-8-sig"))

        for package in inventory:
            self.assertEqual(len(package["supplementalMaterials"]), 1)
            material = package["supplementalMaterials"][0]
            payload = (output / package["id"] / package["version"] / material["path"]).read_bytes()
            self.assertEqual(hashlib.sha256(payload).hexdigest(), material["sha256"])
            self.assertIn(b"Controlled notice bytes", payload)

        expected = set(json.loads(assets.read_text(encoding="utf-8"))["libraries"])
        self.assertEqual({f"{package['id']}/{package['version']}" for package in inventory}, expected)

    def test_changed_cached_notice_fails_instead_of_emitting_successful_inventory(self):
        """A same-named cached file cannot replace the pinned attribution text."""
        runner, cache, assets = self.fixture()
        (cache / HARFBUZZ_COPYING).write_bytes(b"wrong upstream notice")
        result, output = self.collect(runner, cache, assets)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Supplemental license hash mismatch", result.stderr)
        self.assertFalse((output / "inventory.json").exists())

    def test_wrong_nuspec_commit_cannot_receive_unrelated_upstream_notice(self):
        """The collector must bind both required materials to the actual owning package."""
        runner, cache, assets = self.fixture()
        specification = self.directory / "packages/harfbuzzsharp.nativeassets.linux/8.3.1.3/package.nuspec"
        specification.write_text(specification.read_text(encoding="utf-8").replace("2888c737ad016d584c74525e2d35db5097ea8576", "0" * 40), encoding="utf-8")
        result, output = self.collect(runner, cache, assets)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Supplemental license commit does not match", result.stderr)
        self.assertFalse((output / "inventory.json").exists())


if __name__ == "__main__":
    unittest.main()
