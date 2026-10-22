"""Reject compiler-introduced dependencies outside the deliberately small source-built closure."""

import importlib.util
from pathlib import Path
import unittest


SPECIFICATION = importlib.util.spec_from_file_location("ffmpeg_dependencies", Path(__file__).parents[1] / "ffmpeg-runtime-dependencies.py")
DEPENDENCIES = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(DEPENDENCIES)


class FfmpegRuntimeDependenciesTests(unittest.TestCase):
    """OS support is allowed; accidental DLL/shared-zlib dependencies must fail acquisition."""

    def test_windows_os_crt_and_api_sets_are_admitted(self):
        """Both observed MSVCRT cross builds and UCRT native CI builds are supported."""
        DEPENDENCIES.validate_runtime_libraries(["KERNEL32.dll", "msvcrt.dll", "ucrtbase.dll", "api-ms-win-crt-stdio-l1-1-0.dll"], "win-x64")

    def test_windows_optional_library_is_not_hidden_by_ci_path(self):
        """An installed CI DLL must not become an undeclared portable dependency."""
        with self.assertRaisesRegex(ValueError, "Unexpected.*libwinpthread"):
            DEPENDENCIES.validate_runtime_libraries(["libwinpthread-1.dll"], "win-x64")

    def test_linux_glibc_is_allowed_but_zlib_must_be_static(self):
        """The identical explicit zlib source is included statically for both platforms."""
        DEPENDENCIES.validate_runtime_libraries(["libc.so.6", "libm.so.6", "ld-linux-x86-64.so.2"], "linux-x64")
        with self.assertRaisesRegex(ValueError, "Unexpected.*libz"):
            DEPENDENCIES.validate_runtime_libraries(["libz.so.1"], "linux-x64")


if __name__ == "__main__":
    unittest.main()
