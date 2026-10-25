"""Exercise upstream version discovery through the actual source-build helper."""

import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest


class FfmpegBuildVersionTests(unittest.TestCase):
    """An enclosing application repository must not supply FFmpeg's version."""

    def test_owned_build_cannot_discover_enclosing_repository_revision(self):
        """Stop at the first controlled Make invocation after observing upstream version.sh."""
        build_directory = Path(__file__).resolve().parents[1]
        repository_directory = build_directory.parent
        lock = json.loads((build_directory / "ffmpeg.lock.json").read_text(encoding="utf-8"))
        tools = Path(os.environ.get("TRACKTRIM_TOOL_DIRECTORY", str(repository_directory / "tools")))
        source_materials = tools / "licenses/ffmpeg"
        source_archive = source_materials / lock["source"]["fileName"]
        zlib_archive = source_materials / lock["zlib"]["fileName"]
        self.assertTrue(source_archive.is_file(), "Acquire the mandatory pinned tool sources before build-script tests")
        self.assertTrue(zlib_archive.is_file(), "Acquire the mandatory pinned zlib source before build-script tests")
        bash = shutil.which("bash")

        if os.name == "nt":
            candidates = [Path("C:/msys64/usr/bin/bash.exe"), Path("C:/Program Files/Git/bin/bash.exe")]
            bash = next((str(path) for path in candidates if path.is_file()), None)

        self.assertIsNotNone(bash, "Bash is a required source-build tool")

        with tempfile.TemporaryDirectory(prefix="ffmpeg-version-") as temporary:
            temporary_directory = Path(temporary)
            enclosing_repository = temporary_directory / "application"
            subprocess.run(["git", "init", "--quiet", str(enclosing_repository)], check=True, capture_output=True)
            subprocess.run(["git", "-C", str(enclosing_repository), "-c", "user.name=Controlled regression",
                            "-c", "user.email=regression@example.invalid", "commit", "--quiet", "--allow-empty",
                            "-m", "Controlled enclosing revision"], check=True, capture_output=True)
            owned_build = enclosing_repository / "artifacts/downloads/ffmpeg-build-controlled"
            observation = temporary_directory / "observed-version.txt"
            make = temporary_directory / "controlled-make.sh"
            make.write_text("""#!/usr/bin/env bash
set -eu
source_directory=$(CDPATH= cd -- ../ffmpeg-source && pwd)
sh "$source_directory/ffbuild/version.sh" "$source_directory" "" "$TRACKTRIM_TEST_EXTRA_VERSION" > "$TRACKTRIM_TEST_VERSION_OUTPUT"
exit 86
""", encoding="utf-8", newline="\n")
            make.chmod(0o755)
            environment = dict(os.environ, TRACKTRIM_BUILD_MAKE=make.as_posix(),
                               TRACKTRIM_TEST_EXTRA_VERSION=lock["extraVersion"],
                               TRACKTRIM_TEST_VERSION_OUTPUT=observation.as_posix())

            for name in ("GIT_DIR", "GIT_WORK_TREE", "GIT_CEILING_DIRECTORIES", "revision"):
                environment.pop(name, None)

            result = subprocess.run([bash, str(build_directory / "build-ffmpeg.sh"), "win-x64", str(source_archive),
                                     str(zlib_archive), str(owned_build)], capture_output=True, text=True,
                                    encoding="utf-8", env=environment, timeout=90)
            self.assertEqual(86, result.returncode, result.stdout + result.stderr)
            self.assertEqual(lock["version"], observation.read_text(encoding="utf-8").strip(),
                             "The actual helper allowed upstream version.sh to discover the enclosing application's Git revision")


if __name__ == "__main__":
    unittest.main()
