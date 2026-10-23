"""Protect the isolated Windows MKV source-builder's collection and installation boundary."""

import importlib.util
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


BUILD = Path(__file__).resolve().parents[1]
SPECIFICATION = importlib.util.spec_from_file_location("collect_mkv_build", BUILD / "collect-mkv-build.py")
COLLECTION = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(COLLECTION)


class WindowsMkvBuildTests(unittest.TestCase):
    """Check actual refusal, archive identity and path policies without installing build tools."""

    def test_provisioning_refuses_a_non_actions_environment_without_creating_paths(self):
        """The ephemeral installer cannot become a local development-machine installer."""
        powershell = shutil.which("pwsh")
        self.assertIsNotNone(powershell, "PowerShell is a required repository build prerequisite")
        with tempfile.TemporaryDirectory() as temporary:
            destination = Path(temporary) / "tools must remain absent"
            cache = Path(temporary) / "cache must remain absent"
            environment = dict(os.environ, GITHUB_ACTIONS="false", RUNNER_OS="Windows")
            result = subprocess.run([
                powershell, "-NoProfile", "-File", str(BUILD / "provision-windows-mkv.ps1"),
                "-Destination", str(destination), "-CacheDirectory", str(cache),
            ], env=environment, capture_output=True, text=True, timeout=30)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("restricted to an ephemeral Windows GitHub Actions runner", result.stderr)
            self.assertFalse(destination.exists())
            self.assertFalse(cache.exists())

    def test_changed_cached_package_is_rejected_and_preserved(self):
        """An unexpected cached archive must not silently become an accepted build input."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "library.pkg.tar.zst"
            archive.write_bytes(b"unexpected cached bytes")
            with self.assertRaisesRegex(ValueError, "absent or changed"):
                COLLECTION.require_cached_archive(root, {"fileName": archive.name, "sha256": "0" * 64})
            self.assertEqual(b"unexpected cached bytes", archive.read_bytes())

    def run_target_batch(self, root, artifacts, action="download"):
        """Invoke the real downloader function with cached controlled packages, never an installer."""
        runner = root / "runner.ps1"
        runner.write_text("""param($Helper, $Cache, $Artifacts, $Action)
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$syntax = [Management.Automation.Language.Parser]::ParseFile($Helper, [ref]$tokens, [ref]$parseErrors)
$functions = $syntax.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $false)
foreach ($definition in $functions) { Invoke-Expression $definition.Extent.Text }
try {
    $inputs = @(Get-Content -LiteralPath $Artifacts -Raw | ConvertFrom-Json)
    if ($Action -eq 'list') {
        Write-MkvPackageFileList -Artifacts $inputs -Path (Join-Path $Cache 'package-files.txt')
    }
    else {
        Get-VerifiedMkvTargetPackages -Artifacts $inputs -Directory $Cache
    }
    [Console]::WriteLine('accepted complete package batch')
}
catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
""", encoding="utf-8")
        artifact_path = root / "artifacts.json"
        artifact_path.write_text(json.dumps(artifacts), encoding="utf-8")
        return subprocess.run([
            shutil.which("pwsh"), "-NoProfile", "-NonInteractive", "-File", str(runner),
            "-Helper", str(BUILD / "provision-windows-mkv.ps1"), "-Cache", str(root), "-Artifacts", str(artifact_path),
            "-Action", action,
        ], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=30)

    def test_parallel_target_batch_accepts_verified_cached_packages(self):
        """Exercise supported parallel invocation before any target-package installation."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            artifacts = []
            for name in ("first.pkg.tar.zst", "second.pkg.tar.zst"):
                payload = name.encode("ascii")
                (root / name).write_bytes(payload)
                artifacts.append({"fileName": name, "url": "https://example.invalid/not-needed", "sha256": hashlib.sha256(payload).hexdigest()})
            result = self.run_target_batch(root, artifacts)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertIn("accepted complete package batch", result.stdout)

    def test_parallel_target_batch_failure_prevents_acceptance_and_preserves_bytes(self):
        """An error in a worker runspace must reject the entire install input batch."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            archive = root / "changed.pkg.tar.zst"
            archive.write_bytes(b"changed cache")
            result = self.run_target_batch(root, [{"fileName": archive.name, "url": "https://example.invalid/not-needed", "sha256": "0" * 64}])
            self.assertNotEqual(0, result.returncode)
            self.assertIn("Pinned target package validation failed", result.stderr)
            self.assertNotIn("accepted complete", result.stdout)
            self.assertEqual(b"changed cache", archive.read_bytes())

    def test_archive_file_list_uses_literal_lf_names_for_bash(self):
        """A Windows CRLF must never become part of the package filename passed to pacman."""
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            names = ["first.pkg.tar.zst", "second.pkg.tar.zst"]
            result = self.run_target_batch(root, [{"fileName": name} for name in names], action="list")
            self.assertEqual(0, result.returncode, result.stderr)
            package_list = root / "package-files.txt"
            self.assertEqual(b"first.pkg.tar.zst\nsecond.pkg.tar.zst\n", package_list.read_bytes())
            bash = str(Path(os.environ.get("ProgramFiles", "C:/Program Files")) / "Git/bin/bash.exe") if os.name == "nt" else shutil.which("bash")
            self.assertTrue(bash and Path(bash).is_file(), "The documented Git Bash/Linux Bash build reader is required")
            shell = subprocess.run([
                bash, "--noprofile", "--norc", "-c",
                'export PATH=/usr/bin:$PATH; path="$1"; if command -v cygpath >/dev/null; then path=$(cygpath -u "$path"); fi; mapfile -t packages < "$path"; printf "%s\\0" "${packages[@]}"',
                "read-mkv-package-list", str(package_list),
            ], capture_output=True, timeout=30)
            self.assertEqual(0, shell.returncode, shell.stderr.decode("utf-8", errors="replace"))
            self.assertEqual(b"first.pkg.tar.zst\0second.pkg.tar.zst\0", shell.stdout)

    def test_package_member_paths_are_relative_and_non_traversing(self):
        """Only ordinary package-relative names can become collected material paths."""
        self.assertEqual("ucrt64/bin/Qt6Core.dll", COLLECTION.safe_member("ucrt64/bin/Qt6Core.dll").as_posix())
        for invalid in ("../outside", "/rooted", "C:/redirected", "ucrt64\\bin\\worker.dll", "safe/../../outside"):
            with self.subTest(member=invalid), self.assertRaisesRegex(ValueError, "Unsafe"):
                COLLECTION.safe_member(invalid)

    def test_operating_system_imports_do_not_admit_arbitrary_dlls(self):
        """Runtime DLLs remain explicitly accounted for instead of being treated as OS files."""
        for name in ("KERNEL32.dll", "ucrtbase.dll", "api-ms-win-core-file-l1-2-0.dll"):
            self.assertTrue(COLLECTION.is_system_library(name))
        for name in ("Qt6Core.dll", "libFLAC-14.dll", "libstdc++-6.dll", "api-ms-win-file.exe"):
            self.assertFalse(COLLECTION.is_system_library(name))

    def test_package_metadata_retains_the_binary_bound_recipe_hash(self):
        """The source-to-binary recipe check uses original package metadata, not a homepage."""
        fields = COLLECTION.metadata_fields(b"# build receipt\npkgbuild_sha256sum = abc123\ninstalled = first\ninstalled = second\n")
        self.assertEqual(["abc123"], fields["pkgbuild_sha256sum"])
        self.assertEqual(["first", "second"], fields["installed"])

    def test_target_package_lock_has_unique_immutable_identities(self):
        """The whole target install graph is frozen while host-only observations remain separate."""
        lock = json.loads((BUILD / "mkv-source.lock.json").read_text(encoding="utf-8"))
        packages = lock["packages"]
        names = [package["name"] for package in packages]
        self.assertEqual(len(names), len(set(names)))
        self.assertIn("mingw-w64-ucrt-x86_64-flac", names)
        self.assertIn("mingw-w64-ucrt-x86_64-gmp", names)
        self.assertIn("mingw-w64-ucrt-x86_64-qt6-base", names)
        for package in packages:
            with self.subTest(package=package["name"]):
                self.assertRegex(package["sha256"], r"^[0-9a-f]{64}$")
                self.assertEqual(package["fileName"], package["url"].rsplit("/", 1)[1])
                self.assertIn(package["version"], package["fileName"])


if __name__ == "__main__":
    unittest.main()
