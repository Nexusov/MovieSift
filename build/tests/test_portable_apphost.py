"""Check exact apphost-template resolution through the real SDK binding helper."""

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import unittest


class PortableApphostTests(unittest.TestCase):
    """Native host packs can be installed with the SDK instead of restored into NuGet."""

    @classmethod
    def setUpClass(cls):
        """Resolve only the actual pinned SDK/model/template needed by the packaging prerequisite."""
        cls.repository = Path(__file__).resolve().parents[2]
        cls.sdk_version = json.loads((cls.repository / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]
        cls.runtime_version = "10.0.13"
        dotnet = shutil.which("dotnet")
        if not dotnet:
            raise AssertionError("The pinned .NET SDK is required for apphost packaging tests")
        lines = subprocess.check_output([dotnet, "--list-sdks"], text=True).splitlines()
        matches = [re.fullmatch(re.escape(cls.sdk_version) + r" \[(.+)\]", line) for line in lines]
        locations = [Path(match.group(1)) for match in matches if match]
        if len(locations) != 1:
            raise AssertionError("Expected one location for the pinned SDK")
        cls.sdk_directory = locations[0] / cls.sdk_version
        cls.runtime = "win-x64" if os.name == "nt" else "linux-x64"
        cls.template_name = "apphost.exe" if os.name == "nt" else "apphost"
        relative = Path(f"runtimes/{cls.runtime}/native/{cls.template_name}")
        candidates = [locations[0].parent / "packs" / f"Microsoft.NETCore.App.Host.{cls.runtime}" / cls.runtime_version / relative]
        cache = Path(os.environ.get("NUGET_PACKAGES", str(Path.home() / ".nuget/packages")))
        candidates.append(cache / f"microsoft.netcore.app.host.{cls.runtime}" / cls.runtime_version / relative)
        cls.template = next((path for path in candidates if path.is_file()), None)
        if cls.template is None:
            raise AssertionError("The exact pinned native apphost must be provisioned before packaging tests")

    def bind(self, mode):
        """Provide controlled SDK/package locations but use the real pinned HostWriter and template bytes."""
        with tempfile.TemporaryDirectory(prefix="moviesift host source ") as temporary:
            root = Path(temporary)
            sdk = root / "dotnet/sdk" / self.sdk_version
            sdk.mkdir(parents=True)
            model = self.sdk_directory / "Microsoft.NET.HostModel.dll"
            shutil.copyfile(model, sdk / model.name)
            package = root / "package"
            (package / "app").mkdir(parents=True)
            shutil.copyfile(model, package / "app/MovieSift.dll")
            cache = root / "cache"
            cache.mkdir()
            relative = Path(f"runtimes/{self.runtime}/native/{self.template_name}")
            sdk_template = root / "dotnet/packs" / f"Microsoft.NETCore.App.Host.{self.runtime}" / self.runtime_version / relative
            cached_template = cache / f"microsoft.netcore.app.host.{self.runtime}" / self.runtime_version / relative
            targets = [sdk_template] if mode == "sdk" else [cached_template]
            if mode.startswith("both"):
                targets = [sdk_template, cached_template]
            for destination in targets:
                destination.parent.mkdir(parents=True)
                shutil.copy2(self.template, destination)
            if mode == "both-conflicting":
                cached_template.write_bytes(cached_template.read_bytes() + b"changed")
            assets = root / "assets.json"
            assets.write_text(json.dumps({"packageFolders": {str(cache) + os.sep: {}}}), encoding="utf-8")
            command = root / ("controlled-dotnet.cmd" if os.name == "nt" else "controlled-dotnet")
            if os.name == "nt":
                command.write_text(f'@echo off\nif "%~1"=="--version" echo {self.sdk_version}\nif "%~1"=="--list-sdks" echo {self.sdk_version} [{root / "dotnet/sdk"}]\nexit /b 0\n', encoding="utf-8")
            else:
                command.write_text(f'#!/bin/sh\ncase "$1" in\n--version) printf "%s\\n" "{self.sdk_version}";;\n--list-sdks) printf "%s\\n" "{self.sdk_version} [{root / "dotnet/sdk"}]";;\nesac\n', encoding="utf-8")
                command.chmod(0o755)
            result = subprocess.run(["pwsh", "-NoProfile", "-File", str(self.repository / "build/create-portable-apphost.ps1"),
                                     "-PackageRoot", str(package), "-RuntimeIdentifier", self.runtime,
                                     "-AssetsFile", str(assets), "-RuntimeVersion", self.runtime_version,
                                     "-Dotnet", str(command)], capture_output=True, text=True)
            launcher = package / ("MovieSift.exe" if os.name == "nt" else "MovieSift")
            if mode == "both-conflicting":
                self.assertNotEqual(0, result.returncode)
                self.assertIn("Conflicting pinned native apphost", result.stderr)
                self.assertFalse(launcher.exists())
                return
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn(b"app/MovieSift.dll", launcher.read_bytes())

    def test_sdk_bundled_template_without_nuget_copy(self):
        """The native CI runner's installed host pack is a valid exact source."""
        self.bind("sdk")

    def test_restored_template_without_sdk_pack(self):
        """The existing cross-runtime NuGet route remains supported."""
        self.bind("nuget")

    def test_identical_sources_are_unambiguous(self):
        """Equivalent pinned copies do not require an arbitrary cache precedence."""
        self.bind("both-identical")

    def test_conflicting_sources_cannot_choose_a_silent_fallback(self):
        """A changed template fails before an executable is written."""
        self.bind("both-conflicting")


if __name__ == "__main__":
    unittest.main()
