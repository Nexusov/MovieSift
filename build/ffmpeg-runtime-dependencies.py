"""Record the ordinary runtime dependencies of the two pinned FFmpeg workers."""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import re


WINDOWS_SYSTEM_LIBRARIES = frozenset({
    "advapi32.dll", "bcrypt.dll", "kernel32.dll", "msvcrt.dll", "ntdll.dll", "ole32.dll", "shell32.dll",
    "user32.dll", "userenv.dll", "ucrtbase.dll", "version.dll", "winmm.dll", "ws2_32.dll",
})
LINUX_SYSTEM_LIBRARIES = frozenset({
    "linux-vdso.so.1", "ld-linux-x86-64.so.2", "libc.so.6", "libm.so.6", "libmvec.so.1",
    "libdl.so.2", "librt.so.1", "libpthread.so.0", "libgcc_s.so.1",
})


def validate_runtime_libraries(names, runtime_identifier):
    """Do not let a compiler silently add an undeclared optional/native dependency."""
    for name in names:
        normalized = name.casefold() if runtime_identifier == "win-x64" else name
        is_api_set = runtime_identifier == "win-x64" and normalized.endswith(".dll") and normalized.startswith(("api-ms-win-", "ext-ms-win-"))
        allowed = normalized in (WINDOWS_SYSTEM_LIBRARIES if runtime_identifier == "win-x64" else LINUX_SYSTEM_LIBRARIES)

        if not allowed and not is_api_set:
            raise ValueError("Unexpected source-built FFmpeg runtime library: " + name)


def windows_imports(path):
    """Read bounded PE import descriptors; delay/dynamic hardware loads are not required imports."""
    data = path.read_bytes()

    if data[:2] != b"MZ":
        raise ValueError("Expected the pinned PE worker")

    header_offset = struct.unpack_from("<I", data, 60)[0]

    if data[header_offset:header_offset + 4] != b"PE\0\0":
        raise ValueError("Invalid PE signature")

    section_count = struct.unpack_from("<H", data, header_offset + 6)[0]
    optional_size = struct.unpack_from("<H", data, header_offset + 20)[0]
    optional_offset = header_offset + 24

    if section_count > 96 or struct.unpack_from("<H", data, optional_offset)[0] != 0x20B:
        raise ValueError("Expected a bounded x64 PE32+ worker")

    import_address, import_size = struct.unpack_from("<II", data, optional_offset + 120)
    sections = []
    for index in range(section_count):
        offset = optional_offset + optional_size + 40 * index
        virtual_size, virtual_address, raw_size, raw_offset = struct.unpack_from("<IIII", data, offset + 8)
        sections.append((virtual_address, max(virtual_size, raw_size), raw_offset, raw_size))

    def file_offset(address, length):
        """Resolve only file-backed ranges inside a declared section."""
        for virtual_address, virtual_size, raw_offset, raw_size in sections:
            relative = address - virtual_address
            offset = raw_offset + relative
            is_file_backed = 0 <= relative and relative + length <= min(virtual_size, raw_size) and offset + length <= len(data)

            if is_file_backed:
                return offset

        raise ValueError("A PE import range is not backed by the pinned file")

    if import_size > 65536:
        raise ValueError("PE import table exceeds the build-time evidence bound")

    imports = []
    for index in range(min(import_size // 20, 256)):
        offset = file_offset(import_address + 20 * index, 20)
        descriptor = struct.unpack_from("<IIIII", data, offset)

        if not any(descriptor):
            return sorted(set(imports), key=str.casefold)

        name_offset = file_offset(descriptor[3], 1)
        terminator = data.find(b"\0", name_offset, name_offset + 256)

        if terminator < 0:
            raise ValueError("A PE import name exceeds the evidence bound")

        imports.append(data[name_offset:terminator].decode("ascii"))

    raise ValueError("The PE import table has no bounded terminator")


def generate(directory, runtime_identifier, output):
    """Inspect actual selected workers, without assuming the static archive has no OS closure."""
    workers = []
    extension = ".exe" if runtime_identifier == "win-x64" else ""
    for name in ("ffmpeg", "ffprobe"):
        path = directory / (name + extension)
        digest = hashlib.sha256()
        with path.open("rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        row = {"path": name + extension, "sha256": digest.hexdigest()}

        if extension:
            row["ordinaryPeImports"] = windows_imports(path)
            validate_runtime_libraries(row["ordinaryPeImports"], runtime_identifier)
            row["method"] = "Bounded read of ordinary PE32+ import descriptors; Windows system DLLs supplied by the declared OS"
        else:
            result = subprocess.run(["ldd", str(path)], capture_output=True, text=True, check=False, timeout=30)
            dependency_text = result.stdout + result.stderr

            if "not found" in dependency_text or (result.returncode and "not a dynamic executable" not in dependency_text):
                raise ValueError("Unresolved FFmpeg runtime dependency: " + dependency_text)

            libraries = re.findall(r"(?:^|\n)\s*(?:/[^\s]*/)?([^\s/]+\.so(?:\.[0-9]+)*)(?:\s|$)", dependency_text)
            validate_runtime_libraries(libraries, runtime_identifier)

            row["ldd"] = dependency_text.strip()
            row["method"] = "Actual ldd resolution on the matching Linux builder; package/container execution is checked separately"

        workers.append(row)

    output.write_text(json.dumps({"schemaVersion": 1, "runtimeIdentifier": runtime_identifier, "workers": workers, "limits": "The explicit source build disables autodetection/network and statically includes only the pinned zlib beyond native FFmpeg code. Ordinary dynamic imports are restricted to the declared OS/compiler runtime; mandatory package/container execution is checked separately."}, indent=2) + "\n", encoding="utf-8")


def main():
    """Accept explicit acquisition paths only."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--directory", type=Path, required=True)
    parser.add_argument("--runtime-identifier", choices=("win-x64", "linux-x64"), required=True)
    parser.add_argument("--output", type=Path, required=True)
    arguments = parser.parse_args()
    generate(arguments.directory, arguments.runtime_identifier, arguments.output)


if __name__ == "__main__":
    main()
