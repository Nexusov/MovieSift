"""Collect the actual source-built Windows MKV CLI closure and its exact materials."""

import argparse
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import subprocess
import tarfile
import urllib.request
import uuid


MAXIMUM_SOURCE_BYTES = 512 * 1024 * 1024
MAXIMUM_MEMBER_BYTES = 128 * 1024 * 1024
MAXIMUM_RECEIPT_BYTES = 1024 * 1024
MAXIMUM_DEPENDENCIES = 100
MAXIMUM_NOTICE_BYTES = 512 * 1024
GMP_PACKAGE = "mingw-w64-ucrt-x86_64-gmp"
SOURCE_NOTICE_PACKAGES = frozenset({GMP_PACKAGE, "mingw-w64-ucrt-x86_64-icu", "mingw-w64-ucrt-x86_64-libvorbis"})
SYSTEM_LIBRARIES = frozenset({
    "advapi32.dll", "authz.dll", "avrt.dll", "bcrypt.dll", "cfgmgr32.dll", "comctl32.dll", "comdlg32.dll",
    "crypt32.dll", "dbghelp.dll", "dnsapi.dll", "dwmapi.dll", "gdi32.dll", "imm32.dll",
    "iphlpapi.dll", "kernel32.dll", "mpr.dll", "msvcrt.dll", "msvcp_win.dll", "netapi32.dll",
    "ntdll.dll", "ole32.dll", "oleaut32.dll", "powrprof.dll", "propsys.dll", "psapi.dll",
    "rpcrt4.dll", "secur32.dll", "setupapi.dll", "shell32.dll", "shlwapi.dll", "synchronization.dll",
    "ucrtbase.dll", "user32.dll", "userenv.dll", "uuid.dll", "version.dll", "winmm.dll",
    "winspool.drv", "wintrust.dll", "ws2_32.dll", "wsock32.dll", "wtsapi32.dll",
})


def file_hash(path):
    """Hash the full input without loading a source archive into memory."""
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def file_row(path, root):
    """Identify one actual shipped payload or included material."""
    return {"path": path.relative_to(root).as_posix(), "size": path.stat().st_size, "sha256": file_hash(path)}


def write_json(path, value):
    """Write contributor evidence in a stable Unicode-preserving representation."""
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")


def is_system_library(name):
    """Leave only explicitly admitted operating-system imports to Windows."""
    normalized = name.casefold()
    return normalized in SYSTEM_LIBRARIES or normalized.startswith(("api-ms-win-", "ext-ms-win-")) and normalized.endswith(".dll")


def safe_member(name):
    """Reject rooted or traversing archive paths before collecting any member bytes."""
    path = PurePosixPath(name)
    invalid = path.is_absolute() or ".." in path.parts or ":" in name or "\\" in name

    if invalid:
        raise ValueError("Unsafe pinned package member: " + name)

    return path


def command(arguments, maximum_bytes=MAXIMUM_MEMBER_BYTES):
    """Run one build-only reader with checked exit status and bounded retained output."""
    result = subprocess.run(arguments, stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=False, timeout=240)

    if result.returncode:
        raise ValueError("Build material command failed: " + result.stderr.decode("utf-8", errors="replace")[-4096:])

    if len(result.stdout) > maximum_bytes:
        raise ValueError("A build material member exceeds its collection bound")

    return result.stdout


def tar_member(archive, member):
    """Read a named package member with the hosted MSYS2 tar/zstd pair."""
    safe_member(member)
    return command([
        "C:/msys64/usr/bin/bash.exe", "--noprofile", "--norc", "-c",
        'set -euo pipefail; export PATH=/usr/bin:$PATH; tar -xOf "$(cygpath -u "$1")" "$2"',
        "collect-mkv-material", str(archive), member,
    ])


def tar_names(archive):
    """List source-package paths without extracting an uncontrolled directory tree."""
    output = command([
        "C:/msys64/usr/bin/bash.exe", "--noprofile", "--norc", "-c",
        'set -euo pipefail; export PATH=/usr/bin:$PATH; tar -tf "$(cygpath -u "$1")"',
        "collect-mkv-material", str(archive),
    ], maximum_bytes=4 * 1024 * 1024)
    names = output.decode("utf-8").splitlines()

    if len(names) > 100000:
        raise ValueError("A source/package archive has too many member names")

    return names


def metadata_fields(data):
    """Preserve repeated package fields while decoding the native package identity."""
    fields = {}
    for line in data.decode("utf-8").splitlines():

        if line.startswith("#") or " = " not in line:
            continue

        name, value = line.split(" = ", 1)
        fields.setdefault(name, []).append(value)
    return fields


def package_member_path(source, prefix):
    """Map the selected Windows DLL to its actual MSYS2 package/owner-query path."""
    relative = source.relative_to(prefix)
    invalid_member = len(relative.parts) != 2 or relative.parts[0] != "bin" or relative.suffix.casefold() != ".dll"

    if invalid_member:
        raise ValueError("A Windows MKV runtime DLL must belong directly to the locked UCRT64 bin directory")

    return "ucrt64/" + relative.as_posix()


def query_dll_owner(pacman, source, prefix):
    """Ask the installed package database for ownership using its native POSIX path."""
    member = package_member_path(source, prefix)
    return command([str(pacman), "-Qqo", "/" + member]).decode("utf-8").strip()


def require_cached_archive(cache, artifact):
    """Require exact package identity and preserve unexpected cached bytes."""
    path = cache / artifact["fileName"]
    invalid = not path.is_file() or path.is_symlink() or file_hash(path) != artifact["sha256"]

    if invalid:
        raise ValueError("Pinned MKV dependency archive is absent or changed: " + artifact["fileName"])

    return path


def acquire_source(cache, artifact):
    """Download a hash-pinned corresponding-source package without overwriting other data."""
    path = cache / artifact["fileName"]

    if path.exists():
        return require_cached_archive(cache, artifact)

    temporary = cache / (artifact["fileName"] + ".download-" + uuid.uuid4().hex)
    size = 0
    with urllib.request.urlopen(artifact["url"], timeout=240) as response, temporary.open("xb") as stream:
        while block := response.read(1024 * 1024):
            size += len(block)

            if size > MAXIMUM_SOURCE_BYTES:
                raise ValueError("A corresponding-source archive exceeds the collection bound")

            stream.write(block)

    if file_hash(temporary) != artifact["sha256"]:
        raise ValueError("Corresponding-source package hash mismatch; downloaded evidence is preserved: " + temporary.name)

    # A concurrent cache producer cannot be silently overwritten.
    os.link(temporary, path)
    temporary.unlink()
    return path


def extract_source_notice_members(archive_bytes, policy):
    """Read exact primary notice bytes from one authenticated, explicitly pinned nested source archive."""
    if hashlib.sha256(archive_bytes).hexdigest() != policy["sourceArchiveSha256"]:
        raise ValueError("The nested source archive differs from its pinned SHA256")

    collected = []
    with tarfile.open(fileobj=io.BytesIO(archive_bytes), mode="r:*") as archive:
        for member in policy["members"]:
            source_path = safe_member(member["path"])
            destination_path = safe_member(member["fileName"])

            if destination_path.name != member["fileName"]:
                raise ValueError("Source notice names must be single literal filenames")

            try:
                metadata = archive.getmember(source_path.as_posix())
            except KeyError as error:
                raise ValueError("A pinned source notice is missing: " + member["path"]) from error

            valid_member = metadata.isfile() and 0 < metadata.size <= MAXIMUM_NOTICE_BYTES and metadata.size == member["size"]

            if not valid_member:
                raise ValueError("A source notice is not the expected bounded regular member")

            data = archive.extractfile(metadata).read(MAXIMUM_NOTICE_BYTES + 1)

            if len(data) != member["size"] or hashlib.sha256(data).hexdigest() != member["sha256"]:
                raise ValueError("A source notice differs from its pinned SHA256")

            collected.append({**member, "data": data})

    return collected


def collect(directory, cache, prefix, pacman, lock_path, recipe):
    """Bind the CLI pair, recursive DLLs and source/recipe/notices to the exact executed build."""
    lock = json.loads(lock_path.read_text(encoding="utf-8"))
    packages = {item["name"]: item for item in lock["packages"]}
    material_root = directory / "licenses/mkv-source-build"
    module_path = Path(__file__).with_name("ffmpeg-runtime-dependencies.py")
    specification = importlib.util.spec_from_file_location("mkv_pe_imports", module_path)
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    workers = [directory / name for name in ("mkvmerge.exe", "mkvextract.exe")]
    dll_index = {path.name.casefold(): path for path in (prefix / "bin").glob("*.dll")}
    pending = workers.copy()
    imported = []
    dependencies = {}
    dependency_packages = {}

    while pending:
        current = pending.pop(0)
        imports = module.windows_imports(current)
        imported.append({"path": current.name, "imports": imports})
        for name in imports:

            if is_system_library(name) or name.casefold() in dependencies:
                continue

            source = dll_index.get(name.casefold())

            if source is None or len(dependencies) >= MAXIMUM_DEPENDENCIES:
                raise ValueError("Unresolved or excessive Windows MKV dependency: " + name)

            archive_path = package_member_path(source, prefix)
            package_name = query_dll_owner(pacman, source, prefix)
            package = packages.get(package_name)

            if package is None:
                raise ValueError("A runtime DLL does not belong to the locked target closure: " + name)

            archive = require_cached_archive(cache, package)
            archive_bytes = tar_member(archive, archive_path)

            if hashlib.sha256(archive_bytes).hexdigest() != file_hash(source):
                raise ValueError("The installed runtime DLL differs from its pinned package: " + name)

            target = directory / source.name
            shutil.copyfile(source, target)
            row = file_row(target, directory)
            row.update(packageName=package_name, packageVersion=package["version"], upstreamMember=archive_path)
            dependencies[name.casefold()] = row
            dependency_packages[package_name] = package
            pending.append(target)

    included_packages = []
    for package_name, package in sorted(dependency_packages.items()):
        source_pin = package.get("sourcePackage")

        if source_pin is None:
            raise ValueError("Missing exact corresponding-source pin for shipped DLL package: " + package_name)

        source_archive = acquire_source(cache, source_pin)
        shutil.copyfile(source_archive, material_root / "source" / source_pin["fileName"])
        binary_archive = require_cached_archive(cache, package)
        package_root = material_root / "packages" / package_name
        package_root.mkdir(parents=True, exist_ok=False)
        package_info = tar_member(binary_archive, ".PKGINFO")
        build_info = tar_member(binary_archive, ".BUILDINFO")
        (package_root / ".PKGINFO").write_bytes(package_info)
        (package_root / ".BUILDINFO").write_bytes(build_info)
        identity = metadata_fields(package_info)

        if identity.get("pkgname") != [package_name] or identity.get("pkgver") != [package["version"]]:
            raise ValueError("Binary-package identity differs from the locked runtime owner")

        source_names = tar_names(source_archive)
        recipe_names = [name for name in source_names if PurePosixPath(name).name == "PKGBUILD"]

        if len(recipe_names) != 1:
            raise ValueError("A source package must contain exactly one original PKGBUILD")

        recipe_bytes = tar_member(source_archive, recipe_names[0])
        expected_recipe = metadata_fields(build_info).get("pkgbuild_sha256sum")

        if expected_recipe != [hashlib.sha256(recipe_bytes).hexdigest()]:
            raise ValueError("The exact source package recipe does not match the binary BUILDINFO: " + package_name)

        (package_root / "PKGBUILD").write_bytes(recipe_bytes)
        notices = []
        for name in tar_names(binary_archive):

            if not name.startswith("ucrt64/share/licenses/") or name.endswith("/"):
                continue

            relative = safe_member(name)
            target = package_root / "notices" / Path(*relative.parts[3:])
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(tar_member(binary_archive, name))
            notices.append(target.relative_to(material_root).as_posix())

        package_evidence = {"name": package_name, "version": package["version"], "sourcePackage": source_pin, "noticePaths": notices}

        if not notices and package_name in SOURCE_NOTICE_PACKAGES:
            # These three exact binary packages omit notices. Collect their actual
            # primary texts from matched complete sources, never a generic fallback.
            policy = package["sourceNotices"]
            nested_source = tar_member(source_archive, policy["sourcePackageMember"])
            source_notices = extract_source_notice_members(nested_source, policy)
            notice_evidence = []
            for notice in source_notices:
                target = package_root / "notices" / notice["fileName"]
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes(notice["data"])
                relative_path = target.relative_to(material_root).as_posix()
                notices.append(relative_path)
                notice_evidence.append({"path": relative_path, "upstreamMember": notice["path"], "size": notice["size"], "sha256": notice["sha256"]})

            package_evidence["sourceNoticeEvidence"] = {
                "sourcePackageSha256": source_pin["sha256"], "sourcePackageMember": policy["sourcePackageMember"],
                "sourceArchiveSha256": policy["sourceArchiveSha256"], "members": notice_evidence,
            }

        included_packages.append(package_evidence)

    configure_arguments = (material_root / "configure-arguments.txt").read_text(encoding="utf-8").splitlines()
    write_json(material_root / "configure-arguments.json", configure_arguments)
    write_json(material_root / "dependency-packages.json", included_packages)
    installed_packages = command([str(pacman), "-Q"]).decode("utf-8").splitlines()
    write_json(material_root / "installed-packages.json", installed_packages)
    for control_name in ("collect-mkv-build.py", "provision-windows-mkv.ps1", "ffmpeg-runtime-dependencies.py"):
        shutil.copyfile(Path(__file__).with_name(control_name), material_root / control_name)
    materials = [file_row(path, material_root) for path in sorted(material_root.rglob("*")) if path.is_file()]
    receipt = {
        "schemaVersion": 1, "version": lock["version"], "runtimeIdentifier": "win-x64",
        "lockSha256": file_hash(lock_path), "recipeSha256": file_hash(recipe),
        "sourceSha256": lock["source"]["sha256"],
        "workers": [file_row(path, directory) for path in workers],
        "dependencies": sorted(dependencies.values(), key=lambda row: row["path"].casefold()),
        "materials": materials, "dllImports": sorted(imported, key=lambda row: row["path"].casefold()),
        "configureArguments": configure_arguments,
        "buildEnvironment": {
            "runnerOs": os.environ.get("RUNNER_OS"), "runnerImage": os.environ.get("ImageVersion"), "msystem": "UCRT64",
            "sourceExtractionExcludedSymlinks": ["drake", "doc/man/mkvtoolnix-doc.css"],
            "extractionScope": "The two unused source symlinks are omitted from the CLI worktree; the complete original source archive remains included. Rake is supplied by the recorded native Ruby package.",
        },
        "limits": "Actual ordinary PE import closure only. GUI/Qt plugins are not built; mandatory production/package tests independently establish runtime behavior. Full pinned source packages include their patches/build recipes. Host-only build tools are recorded, not shipped.",
    }
    receipt_path = material_root / "build-receipt.json"
    write_json(receipt_path, receipt)

    if receipt_path.stat().st_size > MAXIMUM_RECEIPT_BYTES:
        raise ValueError("MKV source-build receipt exceeds its evidence bound")

    write_json(directory / "mkv-source-provenance.json", {
        "schemaVersion": 1, "component": "MKVToolNix", "version": lock["version"],
        "runtimeIdentifier": "win-x64", "buildKind": "windows-ucrt64-source", "source": lock["source"],
        "lockSha256": file_hash(lock_path), "recipeSha256": file_hash(recipe),
        "receiptPath": "licenses/mkv-source-build/build-receipt.json", "receiptSha256": file_hash(receipt_path),
    })


def main():
    """Use explicit owned builder paths; never discover or modify user application data."""
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("directory", "cache", "prefix", "pacman", "lock", "recipe"):
        parser.add_argument("--" + name, type=Path, required=True)
    arguments = parser.parse_args()
    collect(arguments.directory, arguments.cache, arguments.prefix, arguments.pacman, arguments.lock, arguments.recipe)


if __name__ == "__main__":
    main()
