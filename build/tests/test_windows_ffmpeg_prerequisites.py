"""Validate Windows build preparation without installing anything on the test host."""

import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
import zipfile


class WindowsFfmpegPrerequisiteTests(unittest.TestCase):
    """Exercise the real helper functions against small owned archives and cache files."""

    def setUp(self):
        """Load helper function definitions without executing its CI-only installation entry point."""
        self.temporary = tempfile.TemporaryDirectory(prefix="TrackTrim build prerequisites ")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.helper = Path(__file__).parents[1] / "provision-windows-ffmpeg.ps1"
        self.cache = self.root / "tools with spaces"
        self.cache.mkdir()
        self.runner = self.root / "runner.ps1"
        self.runner.write_text("""param($Helper, $Action, $CacheDirectory, $ArtifactPath, $ArchivePath)
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($Helper, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'Invalid prerequisite script syntax.' }
$functions = $syntax.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($definition in $functions) { Invoke-Expression $definition.Extent.Text }
switch ($Action) {
    'plan' { Get-WindowsFfmpegPrerequisitePlan -CacheDirectory $CacheDirectory | ConvertTo-Json -Depth 8 }
    'download' {
        $artifact = Get-Content -LiteralPath $ArtifactPath -Raw | ConvertFrom-Json
        Get-VerifiedFfmpegPrerequisite -CacheDirectory $CacheDirectory -Artifact $artifact | ConvertTo-Json
    }
    'extract' { Expand-OwnedFfmpegAssembler -ArchivePath $ArchivePath -CacheDirectory $CacheDirectory | ConvertTo-Json }
    default { throw 'Unexpected test action.' }
}
""", encoding="utf-8")

    def run_helper(self, action, artifact=None, archive=None):
        """Call the actual narrowly scoped functions with literal native arguments."""
        powershell = shutil.which("pwsh")
        self.assertIsNotNone(powershell, "PowerShell 7 is a required repository build tool")
        return subprocess.run([
            powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-File", str(self.runner),
            "-Helper", str(self.helper), "-Action", action, "-CacheDirectory", str(self.cache),
            "-ArtifactPath", str(artifact or self.root / "unused"), "-ArchivePath", str(archive or self.root / "unused"),
        ], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, check=False)

    def cached_artifact(self):
        """Prepare a hash-bound offline input; no real upstream download is needed in tests."""
        archive = self.cache / "controlled.pkg.tar.zst"
        archive.write_bytes(b"controlled prerequisite archive")
        artifact = self.root / "artifact.json"
        artifact.write_text(json.dumps({
            "FileName": archive.name, "Url": "https://example.invalid/not-needed",
            "Sha256": hashlib.sha256(archive.read_bytes()).hexdigest(),
        }), encoding="utf-8")
        return artifact, archive

    def test_plan_uses_exact_official_archives_and_native_command_arguments(self):
        """Pin the verified small inputs and avoid floating updates or another compiler path."""
        result = self.run_helper("plan")
        self.assertEqual(result.returncode, 0, result.stderr)
        plan = json.loads(result.stdout)
        self.assertEqual(plan["Bash"], "C:/msys64/usr/bin/bash.exe")
        self.assertEqual(plan["Compiler"], "C:/mingw64/bin/gcc.exe")
        self.assertEqual(plan["Make"], "C:/msys64/usr/bin/make.exe")
        self.assertEqual(plan["PreflightArguments"], ["-Q", "bash", "msys2-runtime", "libintl"])
        self.assertEqual(plan["InstallArguments"], ["-U", "--needed", "--noconfirm", str(self.cache / "make-4.4.1-3-x86_64.pkg.tar.zst")])
        self.assertEqual(plan["MakePackage"]["Url"], "https://repo.msys2.org/msys/x86_64/make-4.4.1-3-x86_64.pkg.tar.zst")
        self.assertEqual(plan["MakePackage"]["Sha256"], "af0bdba17f06fe037f0194069adaa31a8fe45f1a11381501896aea1fae37bd5d")
        self.assertEqual(plan["NasmPackage"]["Url"], "https://www.nasm.us/pub/nasm/releasebuilds/3.01/win64/nasm-3.01-win64.zip")
        self.assertEqual(plan["NasmPackage"]["Sha256"], "e0ba5157007abc7b1a65118a96657a961ddf55f7e3f632ee035366dfce039ca4")

    def test_verified_cache_is_reused_without_downloading_or_mutation(self):
        """A complete verified input remains byte-identical and requires no network."""
        artifact, archive = self.cached_artifact()
        before = archive.read_bytes()
        result = self.run_helper("download", artifact=artifact)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(Path(json.loads(result.stdout)), archive)
        self.assertEqual(archive.read_bytes(), before)

    def test_changed_cache_fails_and_preserves_problematic_bytes(self):
        """A same-named replacement cannot silently become an accepted prerequisite."""
        artifact, archive = self.cached_artifact()
        archive.write_bytes(b"changed prerequisite")
        result = self.run_helper("download", artifact=artifact)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("hash or regular-file check failed", result.stderr)
        self.assertEqual(archive.read_bytes(), b"changed prerequisite")

    def test_assembler_extraction_uses_unique_owned_directories(self):
        """Repeated preparation cannot overwrite a prior extraction or unrelated sentinel."""
        archive = self.root / "assembler.zip"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("nasm-3.01/nasm.exe", b"controlled assembler")
        sentinel = self.cache / "sentinel.txt"
        sentinel.write_bytes(b"preserve existing files")
        first = self.run_helper("extract", archive=archive)
        second = self.run_helper("extract", archive=archive)
        self.assertEqual(first.returncode, 0, first.stderr)
        self.assertEqual(second.returncode, 0, second.stderr)
        first_path, second_path = Path(json.loads(first.stdout)), Path(json.loads(second.stdout))
        self.assertNotEqual(first_path, second_path)
        self.assertTrue(first_path.is_relative_to(self.cache))
        self.assertTrue(second_path.is_relative_to(self.cache))
        self.assertEqual(first_path.read_bytes(), b"controlled assembler")
        self.assertEqual(second_path.read_bytes(), b"controlled assembler")
        self.assertEqual(sentinel.read_bytes(), b"preserve existing files")

    def test_archive_without_expected_assembler_fails(self):
        """An unexpected archive layout must not be added to the build PATH."""
        archive = self.root / "wrong-layout.zip"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("other.exe", b"different tool")
        result = self.run_helper("extract", archive=archive)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("pinned NASM executable is absent", result.stderr)

    def test_entry_point_refuses_local_host_installation(self):
        """The executable entry point fails before touching cache on a non-Actions host."""
        environment = dict(os.environ)
        environment["GITHUB_ACTIONS"] = "false"
        destination = self.root / "must remain absent"
        entry_point = self.root / "entry-point.ps1"
        entry_point.write_text("""param($Helper, $CacheDirectory, $EvidencePath)
try { & $Helper -CacheDirectory $CacheDirectory -EvidencePath $EvidencePath }
catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
""", encoding="utf-8")
        result = subprocess.run([
            shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File", str(entry_point), "-Helper", str(self.helper),
            "-CacheDirectory", str(destination), "-EvidencePath", str(self.root / "not-created.json"),
        ], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30, env=environment, check=False)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("restricted to an ephemeral Windows GitHub Actions runner", result.stderr)
        self.assertFalse(destination.exists())
        self.assertFalse((self.root / "not-created.json").exists())


if __name__ == "__main__":
    unittest.main()
