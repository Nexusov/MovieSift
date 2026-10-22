"""Check real PowerShell cache validation and owned-build cleanup with tiny controlled files."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class FfmpegBuildCacheTests(unittest.TestCase):
    """Reject malformed worker sets, changed bytes and redirected cleanup targets."""

    def setUp(self):
        """Prepare a complete fake build receipt; no media tool is executed."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.cache = self.root / "cache"
        self.cache.mkdir()
        self.helper = Path(__file__).parents[1] / "ffmpeg-build-cache.ps1"
        self.lock = self.root / "lock.json"
        self.lock.write_text(json.dumps({"version": "test-source", "source": {"sha256": "1" * 64}, "zlib": {"sha256": "2" * 64}}))
        self.receipt = {
            "schemaVersion": 1, "runtimeIdentifier": "win-x64", "version": "test-source",
            "sourceSha256": "1" * 64, "zlibSha256": "2" * 64, "recipeSha256": "3" * 64,
            "workers": [], "materials": [],
        }
        for name in ["ffmpeg.exe", "ffprobe.exe"]:
            self.receipt["workers"].append(self.file(name))
        for name in ["configure-arguments.txt", "configure-output.txt", "config.h", "config_components.h", "config.mak",
                     "compiler-version.txt", "compiler-target.txt", "compiler-runtime-macros.txt", "make-version.txt",
                     "assembler-version.txt", "build-host.txt"]:
            self.receipt["materials"].append(self.file(name))
        self.write_receipt()
        self.runner = self.root / "runner.ps1"
        self.runner.write_text("""param($Helper, $Action, $CacheDirectory, $LockPath, $BuildDirectory)
$ErrorActionPreference = 'Stop'
. $Helper
if ($Action -eq 'validate') {
    $toolLock = Get-Content -LiteralPath $LockPath -Raw | ConvertFrom-Json
    Read-VerifiedFfmpegBuild -CacheDirectory $CacheDirectory -RuntimeIdentifier 'win-x64' -ToolLock $toolLock -RecipeHash ('3' * 64) | ConvertTo-Json -Depth 10
    exit
}
Remove-OwnedFfmpegBuild -CacheDirectory $CacheDirectory -BuildDirectory $BuildDirectory
""", encoding="utf-8")

    def file(self, name):
        """Bind a tiny non-executable stand-in to its actual complete bytes."""
        data = ("controlled " + name).encode()
        (self.cache / name).write_bytes(data)
        return {"path": name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()}

    def write_receipt(self):
        """Persist the controlled receipt exactly as the production reader receives it."""
        (self.cache / "build-receipt.json").write_text(json.dumps(self.receipt), encoding="utf-8")

    def run_helper(self, action="validate", build_directory=None):
        """Invoke the public script functions through their actual PowerShell runtime."""
        powershell = shutil.which("pwsh")
        self.assertIsNotNone(powershell, "PowerShell 7 is a required repository build tool")
        return subprocess.run([powershell, "-NoProfile", "-File", str(self.runner), "-Helper", str(self.helper),
                               "-Action", action, "-CacheDirectory", str(self.cache), "-LockPath", str(self.lock),
                               "-BuildDirectory", str(build_directory or self.cache)], capture_output=True, text=True,
                              encoding="utf-8", timeout=30)

    def test_two_distinct_worker_names_are_accepted(self):
        """Protect the array-construction regression for the Windows worker pair."""
        result = self.run_helper()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["ffmpeg.exe", "ffprobe.exe"], [row["path"] for row in json.loads(result.stdout)["workers"]])

    def test_combined_worker_name_is_rejected_without_changing_cache(self):
        """A single joined name cannot masquerade as the required pair."""
        self.receipt["workers"] = [{"path": "ffmpeg.exe ffprobe.exe", "size": 1, "sha256": "0" * 64}]
        self.write_receipt()
        before = (self.cache / "build-receipt.json").read_bytes()
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("exactly the intended workers", result.stderr)
        self.assertEqual(before, (self.cache / "build-receipt.json").read_bytes())

    def test_changed_worker_and_missing_recipe_material_fail(self):
        """Receipts never authorize changed binaries or incomplete build evidence."""
        (self.cache / "ffmpeg.exe").write_bytes(b"different executable")
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("missing or changed", result.stderr)
        self.file("ffmpeg.exe")
        (self.cache / "config.mak").unlink()
        self.assertNotEqual(0, self.run_helper().returncode)

    def test_worker_names_require_exact_case(self):
        """Case-insensitive comparison cannot authorize a different Linux pathname."""
        self.receipt["workers"][0]["path"] = "FFMPEG.EXE"
        self.write_receipt()
        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("exactly the intended workers", result.stderr)

    def test_nonregular_receipt_is_rejected_before_read(self):
        """A Linux FIFO or Windows directory cannot hang or masquerade as a JSON record."""
        receipt = self.cache / "build-receipt.json"
        receipt.unlink()

        if os.name == "nt":
            receipt.mkdir()
        else:
            os.mkfifo(receipt)

        result = self.run_helper()
        self.assertNotEqual(0, result.returncode)
        self.assertIn("bounded regular source-build receipt", result.stderr)

    def test_owned_ordinary_build_directory_can_be_removed(self):
        """Successful cleanup touches only the verified generated sibling."""
        build = self.cache / ("ffmpeg-build-" + "a" * 32)
        build.mkdir()
        (build / "owned.tmp").write_bytes(b"owned build output")
        result = self.run_helper("cleanup", build)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertFalse(build.exists())
        self.assertTrue((self.cache / "ffmpeg.exe").exists())

    def test_redirected_build_root_does_not_delete_target(self):
        """A UUID-shaped symlink/junction cannot redirect recursive cleanup."""
        outside = self.root / "unrelated"
        outside.mkdir()
        sentinel = outside / "sentinel.txt"
        sentinel.write_bytes(b"preserve")
        build = self.cache / ("ffmpeg-build-" + "b" * 32)

        if os.name == "nt":
            command = "param($LinkPath,$TargetPath); New-Item -ItemType Junction -Path $LinkPath -Value $TargetPath | Out-Null"
            script = self.root / "junction.ps1"
            script.write_text(command)
            result = subprocess.run([shutil.which("pwsh"), "-NoProfile", "-File", str(script), str(build), str(outside)],
                                    capture_output=True, text=True, timeout=30)
            self.assertEqual(0, result.returncode, result.stderr)
        else:
            build.symlink_to(outside, target_is_directory=True)

        result = self.run_helper("cleanup", build)
        self.assertNotEqual(0, result.returncode)
        self.assertEqual(b"preserve", sentinel.read_bytes())


if __name__ == "__main__":
    unittest.main()
