"""Inventory actual portable bytes; mechanical evidence is not redistribution approval."""

import argparse
import base64
from collections import Counter
import hashlib
import importlib.util
import json
import os
from pathlib import Path, PurePosixPath
import shutil
import stat
import tarfile
import zipfile
from urllib.parse import urlparse


MKV_BUILD_JSON_BYTES = 1024 * 1024
MKV_BUILD_MAX_FILES = 4096
MKV_BUILD_MAX_PAYLOAD_BYTES = 128 * 1024 * 1024
MKV_BUILD_MATERIAL_PREFIX = "licenses/mkv-source-build/"
MKV_BUILD_WORKERS = frozenset({"mkvmerge.exe", "mkvextract.exe"})
MKV_SOURCE_NOTICE_PACKAGES = frozenset({
    "mingw-w64-ucrt-x86_64-gmp", "mingw-w64-ucrt-x86_64-icu", "mingw-w64-ucrt-x86_64-libvorbis",
})


def digest(stream, algorithm="sha256"):
    """Hash a stream without retaining binary payloads in the report."""
    result = hashlib.new(algorithm)
    for block in iter(lambda: stream.read(1024 * 1024), b""):
        result.update(block)
    return result.digest()


def file_hash(path):
    """Return the SHA256 of the bytes actually distributed."""
    with path.open("rb") as stream:
        return digest(stream).hex()


def read_json(path):
    """Require an existing UTF-8 evidence input."""
    return json.loads(path.read_text(encoding="utf-8-sig"))


def relative_path(value):
    """Reject ambiguous or escaping evidence and archive paths."""
    path = PurePosixPath(value)

    if not value or "\\" in value or path.is_absolute() or ".." in path.parts or ":" in value:
        raise ValueError(f"Unsafe relative path: {value!r}")

    return path.as_posix()


def read_mkv_build_json(path):
    """Read only a bounded regular source-build record, without waiting on a FIFO."""
    attributes = path.lstat()
    redirected = getattr(attributes, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT

    if not stat.S_ISREG(attributes.st_mode) or redirected:
        raise ValueError("MKV source-build evidence must be a bounded regular file")

    descriptor = os.open(path, os.O_RDONLY | getattr(os, "O_NONBLOCK", 0) | getattr(os, "O_NOFOLLOW", 0))
    with os.fdopen(descriptor, "rb") as stream:
        metadata = os.fstat(stream.fileno())

        redirected = getattr(metadata, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT
        unchanged_identity = (metadata.st_dev, metadata.st_ino) == (attributes.st_dev, attributes.st_ino)

        if not stat.S_ISREG(metadata.st_mode) or redirected or not unchanged_identity or metadata.st_size > MKV_BUILD_JSON_BYTES:
            raise ValueError("MKV source-build evidence must be a bounded regular file")

        data = stream.read(MKV_BUILD_JSON_BYTES + 1)

    if len(data) > MKV_BUILD_JSON_BYTES:
        raise ValueError("MKV source-build evidence exceeds its JSON bound")

    return json.loads(data.decode("utf-8-sig"))


def mkv_windows_imports(path):
    """Reuse the existing bounded ordinary-PE reader for the independently built MKV workers."""
    specification = importlib.util.spec_from_file_location("tracktrim_pe_evidence", Path(__file__).with_name("ffmpeg-runtime-dependencies.py"))
    module = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(module)
    return module.windows_imports(path)


def mkv_source_build_evidence(directory, records, lock_path, recipe_path, standalone=False):
    """Bind the Windows replacement to its exact recipe, sources, complete payload and actual imports."""
    provenance = read_mkv_build_json(directory / "mkv-source-provenance.json")
    lock = read_mkv_build_json(lock_path)
    lock_hash = file_hash(lock_path)
    recipe_hash = file_hash(recipe_path)
    expected_receipt = MKV_BUILD_MATERIAL_PREFIX + "build-receipt.json"
    matching_provenance = provenance.get("schemaVersion") == 1 and provenance.get("component") == "MKVToolNix" \
        and provenance.get("version") == "102.0" and provenance.get("runtimeIdentifier") == "win-x64" \
        and provenance.get("buildKind") == "windows-ucrt64-source" and provenance.get("source") == lock.get("source") \
        and provenance.get("lockSha256") == lock_hash and provenance.get("recipeSha256") == recipe_hash \
        and provenance.get("receiptPath") == expected_receipt

    if not matching_provenance:
        raise ValueError("MKV source-build provenance differs from the pinned source/lock/recipe")

    if expected_receipt not in records or records[expected_receipt]["sha256"] != provenance.get("receiptSha256"):
        raise ValueError("MKV source-build receipt is missing or changed")

    receipt = read_mkv_build_json(directory / expected_receipt)
    matching_receipt = receipt.get("schemaVersion") == 1 and receipt.get("version") == "102.0" \
        and receipt.get("runtimeIdentifier") == "win-x64" and receipt.get("sourceSha256") == lock["source"]["sha256"] \
        and receipt.get("lockSha256") == lock_hash and receipt.get("recipeSha256") == recipe_hash

    if not matching_receipt:
        raise ValueError("MKV source-build receipt inputs differ from the pinned build")

    workers, dependencies, materials = (receipt.get(name) for name in ("workers", "dependencies", "materials"))

    if any(not isinstance(rows, list) for rows in (workers, dependencies, materials)):
        raise ValueError("MKV source-build receipt lacks its complete payload/material lists")

    if any(not isinstance(row, dict) for row in workers + dependencies + materials):
        raise ValueError("MKV source-build receipt contains an invalid file record")

    if len(workers) != len(MKV_BUILD_WORKERS) or {row.get("path") for row in workers} != MKV_BUILD_WORKERS:
        raise ValueError("MKV source-build receipt requires exactly the two intended workers")

    all_rows = workers + dependencies + materials

    if len(all_rows) > MKV_BUILD_MAX_FILES:
        raise ValueError("MKV source-build receipt exceeds its file-count bound")

    expected_files = {"mkv-source-provenance.json", expected_receipt}
    declared_dependencies = {}
    for row in all_rows:
        name = row.get("path")

        if not isinstance(name, str) or relative_path(name) != name:
            raise ValueError("MKV source-build receipt contains an unsafe path")

        if row in materials:
            name = MKV_BUILD_MATERIAL_PREFIX + name

        if name in expected_files:
            raise ValueError("MKV source-build receipt contains duplicate files")

        expected_files.add(name)
        actual = records.get(name)
        valid_size = isinstance(row.get("size"), int) and not isinstance(row["size"], bool) and row["size"] >= 0

        if not valid_size or actual is None or actual["sha256"] != row.get("sha256") or actual["size"] != row["size"]:
            raise ValueError("MKV source-build receipt does not match the actual worker/material")

        metadata = (directory / name).lstat()

        if not stat.S_ISREG(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
            raise ValueError("MKV source-build payload/material is redirected or non-regular")

        for parent in (directory / name).parents:

            if parent == directory:
                break

            attributes = parent.lstat()

            if not stat.S_ISDIR(attributes.st_mode) or getattr(attributes, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT:
                raise ValueError("MKV source-build material has a redirected directory ancestor")

    for row in dependencies:
        name = row["path"]
        valid_dependency = PurePosixPath(name).name == name and name.endswith(".dll") \
            and isinstance(row.get("packageName"), str) and bool(row["packageName"]) \
            and isinstance(row.get("packageVersion"), str) and bool(row["packageVersion"]) \
            and row.get("upstreamMember") == "ucrt64/bin/" + name

        if not valid_dependency or name.casefold() in declared_dependencies:
            raise ValueError("MKV source-build dependency names/packages are invalid or duplicate")

        declared_dependencies[name.casefold()] = row

    if any(row["size"] > MKV_BUILD_MAX_PAYLOAD_BYTES for row in workers + dependencies):
        raise ValueError("MKV source-build PE payload exceeds its inspection bound")

    observed_files = set(records) if standalone else {
        name for name in records if name.startswith(MKV_BUILD_MATERIAL_PREFIX) or name == "mkv-source-provenance.json"
        or name.endswith(".dll") or name.endswith(".exe") and name not in {"ffmpeg.exe", "ffprobe.exe"}
    }

    if observed_files != expected_files:
        raise ValueError("MKV source-build receipt does not cover exactly the actual build files")

    recipe_license = lock["recipeSnapshot"]["license"]
    required_materials = {
        "mkv-source.lock.json", "build-mkv-windows.sh", "config.h", "build-config", "config.log",
        "configure-arguments.json", "installed-packages.json", "compiler-versions.txt", "dependency-packages.json",
        "collect-mkv-build.py", "provision-windows-mkv.ps1", "ffmpeg-runtime-dependencies.py",
        relative_path(recipe_license["fileName"]),
        "source/" + lock["source"]["fileName"],
    }
    material_names = {row["path"] for row in materials}

    if not required_materials.issubset(material_names):
        raise ValueError("MKV source-build required source/configuration/notices are missing")

    pinned_materials = {
        "mkv-source.lock.json": lock_hash, "build-mkv-windows.sh": recipe_hash,
        "source/" + lock["source"]["fileName"]: lock["source"]["sha256"],
        recipe_license["fileName"]: recipe_license["sha256"],
    }
    for name in ("collect-mkv-build.py", "provision-windows-mkv.ps1", "ffmpeg-runtime-dependencies.py"):
        pinned_materials[name] = file_hash(Path(__file__).with_name(name))
    for name, expected_hash in pinned_materials.items():

        if records[MKV_BUILD_MATERIAL_PREFIX + name]["sha256"] != expected_hash:
            raise ValueError("MKV source-build included source/lock/recipe differs from its pin")

    if records[MKV_BUILD_MATERIAL_PREFIX + recipe_license["fileName"]]["size"] != recipe_license["size"]:
        raise ValueError("MKV source-build recipe snapshot license size differs from its pin")

    arguments = read_mkv_build_json(directory / MKV_BUILD_MATERIAL_PREFIX / "configure-arguments.json")

    if not isinstance(arguments, list) or not arguments or arguments != receipt.get("configureArguments") \
            or any(not isinstance(argument, str) for argument in arguments) or not isinstance(receipt.get("buildEnvironment"), dict):
        raise ValueError("MKV source-build configuration/environment evidence is incomplete")

    packages = read_mkv_build_json(directory / MKV_BUILD_MATERIAL_PREFIX / "dependency-packages.json")

    if not isinstance(packages, list) or any(not isinstance(package, dict) for package in packages):
        raise ValueError("MKV source-build dependency package evidence is invalid")

    pinned_packages = {package["name"]: package for package in lock["packages"]}
    declared_packages = {package.get("name"): package for package in packages}
    expected_packages = {row["packageName"] for row in dependencies}

    if len(declared_packages) != len(packages) or set(declared_packages) != expected_packages:
        raise ValueError("MKV source-build dependency sources do not cover exactly the shipped packages")

    for name, package in declared_packages.items():
        pinned = pinned_packages.get(name)

        if pinned is None or package.get("version") != pinned["version"] or not pinned.get("sourcePackage") \
                or package.get("sourcePackage") != pinned["sourcePackage"]:
            raise ValueError("MKV source-build dependency source/version is not pinned")

        source_path = "source/" + relative_path(package["sourcePackage"]["fileName"])
        notices = package.get("noticePaths")

        if source_path not in material_names or records[MKV_BUILD_MATERIAL_PREFIX + source_path]["sha256"] != package["sourcePackage"]["sha256"] \
                or not isinstance(notices, list) or not notices or any(notice not in material_names for notice in notices):
            raise ValueError("MKV source-build exact dependency sources/notices are missing or changed")

        source_notice_policy = pinned.get("sourceNotices")
        source_notice_evidence = package.get("sourceNoticeEvidence")

        if source_notice_policy or source_notice_evidence:

            if name not in MKV_SOURCE_NOTICE_PACKAGES or not isinstance(source_notice_policy, dict) or not isinstance(source_notice_evidence, dict):
                raise ValueError("MKV source-build source notice evidence lacks its explicit package policy")

            expected_notices = [{"path": "packages/" + name + "/notices/" + relative_path(row["fileName"]),
                                 "upstreamMember": row["path"], "size": row["size"], "sha256": row["sha256"]}
                                for row in source_notice_policy["members"]]
            expected_evidence = {
                "sourcePackageSha256": pinned["sourcePackage"]["sha256"],
                "sourcePackageMember": source_notice_policy["sourcePackageMember"],
                "sourceArchiveSha256": source_notice_policy["sourceArchiveSha256"], "members": expected_notices,
            }

            if source_notice_evidence != expected_evidence or notices != [row["path"] for row in expected_notices]:
                raise ValueError("MKV source-build notice evidence differs from the exact pinned source policy")

            for row in expected_notices:
                included_notice = records[MKV_BUILD_MATERIAL_PREFIX + row["path"]]

                if included_notice["sha256"] != row["sha256"] or included_notice["size"] != row["size"]:
                    raise ValueError("MKV source-build source notice bytes differ from the exact pin")

    for dependency in dependencies:

        if dependency["packageVersion"] != declared_packages[dependency["packageName"]]["version"]:
            raise ValueError("MKV source-build DLL owner differs from its pinned source package")

    imports = receipt.get("dllImports", [])
    payloads = workers + dependencies

    if not isinstance(imports, list) or any(not isinstance(row, dict) for row in imports):
        raise ValueError("MKV source-build import evidence is invalid")

    if len(imports) != len(payloads) or {row.get("path") for row in imports} != {row["path"] for row in payloads}:
        raise ValueError("MKV source-build import evidence does not cover the complete payload")

    specification = importlib.util.spec_from_file_location("tracktrim_mkv_collector", Path(__file__).with_name("collect-mkv-build.py"))
    collector = importlib.util.module_from_spec(specification)
    specification.loader.exec_module(collector)
    for row in imports:
        observed_imports = mkv_windows_imports(directory / row["path"])

        declared_imports = row.get("imports")

        if not isinstance(declared_imports, list) or any(not isinstance(name, str) for name in declared_imports) \
                or sorted(observed_imports, key=str.casefold) != sorted(declared_imports, key=str.casefold):
            raise ValueError("MKV source-build import evidence differs from the actual PE payload")

        for imported in observed_imports:
            normalized = imported.casefold()

            if not collector.is_system_library(imported) and normalized not in declared_dependencies:
                raise ValueError("Undeclared MKV source-build runtime dependency: " + imported)

    return {"provenance": provenance, "receipt": receipt, "lock": lock}


def staged_files(root):
    """Enumerate all payloads, including retained AppImage libraries and links."""
    records = {}
    for path in sorted(root.rglob("*")):

        if path.is_symlink() and not path.resolve().is_relative_to(root.resolve()):
            raise ValueError("A staged symlink escapes the package")

        if not path.is_file():
            continue

        name = path.relative_to(root).as_posix()
        records[name] = {"path": name, "size": path.stat().st_size, "sha256": file_hash(path)}

        if path.is_symlink():
            records[name]["linkTarget"] = path.readlink().as_posix()

    return records


def verify_manifest(root, records):
    """Require complete and exact coverage by the existing package manifest."""
    entries = {}
    for line in (root / "SHA256SUMS").read_text(encoding="utf-8-sig").splitlines():
        expected, separator, name = line.partition("  ")
        name = relative_path(name)

        if not separator or len(expected) != 64 or name in entries:
            raise ValueError("Malformed or duplicate package manifest entry")

        entries[name] = expected

    expected_files = set(records) - {"SHA256SUMS"}

    if set(entries) != expected_files:
        raise ValueError("The package manifest does not cover exactly the staged payloads")

    for name, expected in entries.items():

        if records[name]["sha256"] != expected:
            raise ValueError(f"Package manifest mismatch: {name}")


def verify_archive(archive, root_name, records):
    """Compare final archive members with staging, not just a restore graph."""
    found = set()

    def check(name, stream):
        """Validate one final archive member against the staged bytes."""
        parts = PurePosixPath(relative_path(name)).parts

        if len(parts) < 2 or parts[0] != root_name:
            raise ValueError("Archive does not have the expected single package root")

        name = PurePosixPath(*parts[1:]).as_posix()

        if name in found or name not in records:
            raise ValueError(f"Duplicate or unexpected archive member: {name}")

        found.add(name)

        if digest(stream).hex() != records[name]["sha256"]:
            raise ValueError(f"Archive/staging byte mismatch: {name}")

    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as package:
            for member in package.infolist():

                if member.is_dir():
                    continue

                with package.open(member) as stream:
                    check(member.filename, stream)
    else:
        with tarfile.open(archive, "r:gz") as package:
            for member in package.getmembers():

                if member.isdir():
                    continue

                if not (member.isfile() or member.issym() or member.islnk()):
                    raise ValueError("Unsupported special file in package archive")

                if member.issym() or member.islnk():
                    relative_path(member.linkname)

                with package.extractfile(member) as stream:
                    check(member.name, stream)

    if found != set(records):
        raise ValueError("The final archive is missing staged payloads")


def nuget_origins(assets, packages, records):
    """Match published bytes to signed nupkg members and retain source evidence."""
    candidates = {(Path(name).name.casefold(), record["size"]) for name, record in records.items() if "/" not in name}
    origins = {}
    components = []
    for package in packages:
        identifier, version = package["id"], package["version"]
        key = f"{identifier}/{version}"
        cache_name = f"{identifier.lower()}.{version}.nupkg"
        locations = [Path(folder) / identifier.lower() / version / cache_name for folder in assets["packageFolders"]]
        location = next((candidate for candidate in locations if candidate.is_file()), None)

        if location is None:
            raise ValueError(f"Restored original nupkg missing: {key}")

        with location.open("rb") as stream:
            archive_sha512 = base64.b64encode(digest(stream, "sha512")).decode("ascii")
        sidecar_hash = location.with_suffix(".nupkg.sha512").read_text(encoding="utf-8-sig").strip()

        if archive_sha512 != sidecar_hash:
            raise ValueError(f"Cached nupkg archive/sidecar mismatch: {key}")

        prefix = f"licenses/nuget/{key}/"
        materials = [prefix + name for name in package["includedLicenseFiles"]]
        materials.append(prefix + package["specificationFile"])
        for material in package["supplementalMaterials"]:
            material_path = prefix + relative_path(material["path"])

            if material_path not in records or records[material_path]["sha256"] != material["sha256"]:
                raise ValueError(f"Included supplemental notice missing or changed: {key}")

            materials.append(material_path)

        if any(name not in records for name in materials):
            raise ValueError(f"Referenced NuGet material is not shipped: {key}")

        component = {
            "id": f"nuget:{key}", "name": identifier, "version": version,
            "licenseExpressionOrFile": package["license"],
            "repositoryUrl": package["repositoryUrl"], "repositoryCommit": package["repositoryCommit"],
            "restoreContentHash": package["sha512"], "signedArchiveSha512": archive_sha512,
            "hashNote": "NuGet restore content hash and signed archive SHA512 have different semantics; archive verified against its cache sidecar.",
            "includedMaterials": materials, "supplementalMaterials": package["supplementalMaterials"],
            "matchedPayloads": [],
        }
        component["materialEvidence"] = []
        with zipfile.ZipFile(location) as source:
            package_members = {member.filename.casefold(): member for member in source.infolist()}
            for material_name in package["includedLicenseFiles"] + [package["specificationFile"]]:
                member = package_members.get(material_name.casefold())

                if member is None:
                    raise ValueError(f"Included notice/specification absent from nupkg: {key}/{material_name}")

                with source.open(member) as stream:
                    expected = digest(stream).hex()
                material_path = prefix + material_name

                if records[material_path]["sha256"] != expected:
                    raise ValueError(f"Included notice/specification differs from nupkg: {key}/{material_name}")

                component["materialEvidence"].append({"path": material_path, "member": member.filename, "sha256": expected})

            for member in source.infolist():

                if member.is_dir() or (PurePosixPath(member.filename).name.casefold(), member.file_size) not in candidates:
                    continue

                with source.open(member) as stream:
                    member_hash = digest(stream).hex()
                identity = (PurePosixPath(member.filename).name.casefold(), member.file_size, member_hash)
                origins.setdefault(identity, []).append({"component": component["id"], "member": member.filename})

        components.append(component)

    return origins, components


def classify(records, origins, components, tool_lock, profile):
    """Account for every shipped file without claiming unknown origins are resolved."""
    component_map = {component["id"]: component for component in components}
    for name, record in records.items():
        identity = (Path(name).name.casefold(), record["size"], record["sha256"])
        matches = origins.get(identity, []) if "/" not in name else []

        if matches:
            runtime_payload = any(match["component"].startswith("nuget:Microsoft.NETCore.App.Runtime.") for match in matches)
            record.update(category="self-contained-runtime" if runtime_payload else "third-party-payload", origins=matches)
            record["payloadKind"] = "native" if any("/native/" in match["member"] for match in matches) else "managed-or-runtime-data"

            if name.endswith(".pdb"):
                record["payloadKind"] = "debug-symbols-distributed"

            for match in matches:
                component_map[match["component"]]["matchedPayloads"].append(name)
            continue

        if name.startswith("licenses/nuget/"):
            record.update(category="notice-or-package-metadata", origin="NuGet collection; see licenses/nuget/inventory.json")
            continue

        if name.startswith("tools/"):
            record.update(category="bundled-tool-material", origin=f"MKVToolNix upstream {tool_lock['version']} acquisition")

            ffmpeg_material = name.startswith("tools/licenses/ffmpeg/") or name == "tools/ffmpeg-provenance.json"

            if ffmpeg_material:
                record.update(origin="Pinned FFmpeg source/build/binary acquisition; see ffmpegProvenance")

            if name.endswith((".tar.xz", ".tar.gz", ".src.rpm", ".src.tar.zst")):
                record["category"] = "included-source-archive"

            if name in ("tools/ffmpeg.exe", "tools/ffprobe.exe", "tools/ffmpeg", "tools/ffprobe"):
                record.update(category="bundled-executable", component="FFmpeg", origin="Source-built from the pinned FFmpeg and static zlib archives", staticComponentStatus="Exact source, local recipe, compiler/target and complete worker hashes are recorded; no optional external codec libraries")
                continue

            if "/app-root/usr/lib/" in name:
                record["category"] = "bundled-native-library"
                record["versionStatus"] = "unresolved unless separately evidenced in nativeComponentEvidence"

            if name.endswith(".a"):
                record["category"] = "distributed-static-archive"

            if name.endswith(".tar.xz"):
                record["category"] = "included-source-archive"

            if name in ("tools/mkvmerge.exe", "tools/mkvextract.exe", "tools/app-root/usr/bin/mkvmerge", "tools/app-root/usr/bin/mkvextract"):
                record["category"] = "bundled-executable"
                record["component"] = f"MKVToolNix/{tool_lock['version']}"
                record["staticComponentStatus"] = "See nativeComponentEvidence and unresolved build/source findings"

            if name in ("tools/mkvmerge", "tools/mkvextract", "tools/provenance.json"):
                record.update(category="generated-build-material", origin="build/acquire-tools.ps1 and build/tools.lock.json")
            continue

        if name == "docs/third-party/Filee-LICENSE.txt":
            notice = Path(__file__).parents[1] / name
            if record["sha256"].lower() != file_hash(notice).lower():
                raise ValueError("The adapted Filee Soft notice differs from the repository notice")
            record.update(category="third-party-notice", component="Filee Soft visual reference",
                          origin="Filee e830916e7f02526bff6b7576edc22e51ecf9c126; MIT notice for adapted styles/motion")
            continue

        original = name.startswith("TrackTrim") or name.startswith("docs/") or name == "ru/TrackTrim.resources.dll" or name in {
            "LICENSE", "README.md", "CONTRIBUTING.md", "THIRD_PARTY_NOTICES.md", "portable.flag", "SHA256SUMS",
            "build-provenance.json", "tool-provenance.json", "ffmpeg-provenance.json", "mkv-source-provenance.json", "native-dependencies.txt",
        }

        if original:
            record.update(category="original-or-generated-project-output", origin="Recorded build revision and packaging scripts")

            if name in ("TrackTrim", "TrackTrim.exe"):
                record.update(origin="SDK-generated .NET apphost; SDK version in build-provenance.json", sourceCompleteness="Exact apphost template/source relationship requires review")
            continue

        record.update(category="unresolved-origin", origin="No exact nupkg or documented project/tool match")

    for component in components:
        component["distributionRole"] = "payload-and-materials" if component["matchedPayloads"] else "metadata-only; restored dependency, no matched published code/native payload"



def tool_origins(root, records, tool_lock, runtime_identifier, ffmpeg_lock, mkv_source=None):
    """Bind every acquired tool/material payload to the hash-checked acquisition."""
    provenance = read_json(root / "tools/file-provenance.json")
    supplemental = read_json(root / "tools/licenses/upstream-materials.json")
    permitted_hashes = {tool_lock["source"]["sha256"], tool_lock[runtime_identifier]["sha256"]}
    permitted_hashes.update(ffmpeg_lock[key]["sha256"] for key in ("source", "zlib"))

    if mkv_source:
        permitted_hashes.update(package["sourcePackage"]["sha256"] for package in mkv_source["lock"]["packages"] if package.get("sourcePackage"))
        shipped_packages = {row["packageName"] for row in mkv_source["receipt"]["dependencies"]}
        permitted_hashes.update(package["sha256"] for package in mkv_source["lock"]["packages"] if package["name"] in shipped_packages)
        permitted_hashes.add(mkv_source["lock"]["recipeSnapshot"]["license"]["sha256"])
    for material in supplemental:
        directory = material.get("outputDirectory", "upstream-terms")

        if directory not in ("upstream-terms", "corresponding-source"):
            raise ValueError("Unsupported included-material directory")

        name = "tools/licenses/" + directory + "/" + relative_path(material["path"])

        if name not in records or records[name]["sha256"] != material["sha256"]:
            raise ValueError("An included upstream terms/header material is missing or changed")

        permitted_hashes.add(material["sha256"])
        if material.get("sourceArtifact"):
            permitted_hashes.add(material["sourceArtifact"]["sha256"])
        for payload in material.get("correspondsTo", []):
            shipped = records.get("tools/" + relative_path(payload["path"]))

            if shipped is None or shipped["sha256"] != payload["sha256"] or shipped["size"] != payload["size"]:
                raise ValueError("Included source material does not correspond to the actual shipped library")
    observed = set()
    for origin in provenance:
        name = "tools/" + relative_path(origin["path"])

        if name in observed or name not in records or records[name]["sha256"] != origin["sha256"] or records[name]["size"] != origin["size"]:
            raise ValueError(f"Tool acquisition evidence does not match shipped payload: {name}")

        observed.add(name)
        upstream_hash = origin["upstreamArchiveSha256"]

        if upstream_hash and upstream_hash not in permitted_hashes:
            raise ValueError(f"Unpinned tool/material origin: {name}")

        records[name]["acquisitionEvidence"] = origin

    expected = {name for name in records if name.startswith("tools/")} - {"tools/file-provenance.json"}

    if observed != expected:
        raise ValueError("Tool acquisition evidence does not cover all retained payloads")


def payload_selection_evidence(root, records, tool_lock, runtime_identifier):
    """Require exact, auditable omissions rather than silently dropping unknown upstream files."""
    receipt = read_json(root / "tools/payload-selection.json")
    policy_path = root / "tools/licenses/payload-selection.lock.json"
    policy = read_json(policy_path)
    selected = policy[runtime_identifier]
    matching_identity = receipt["schemaVersion"] == 1 and receipt["runtimeIdentifier"] == runtime_identifier \
        and receipt["policySha256"] == file_hash(policy_path) \
        and receipt["artifactSha256"] == selected["artifactSha256"] == tool_lock[runtime_identifier]["sha256"]

    if not matching_identity or receipt["includedFiles"] != selected["includedFiles"] or receipt["omittedFiles"] != selected["omittedFiles"]:
        raise ValueError("Tool payload selection does not match the pinned artifact/policy")

    for row in receipt["includedFiles"]:
        name = "tools/" + relative_path(row["path"])

        if name not in records or records[name]["sha256"] != row["sha256"] or records[name]["size"] != row["size"]:
            raise ValueError("A required CLI library was omitted or changed")

    for row in receipt["omittedFiles"]:

        if "tools/" + relative_path(row["previousPackagePath"]) in records:
            raise ValueError("A payload expressly excluded by the CLI selection was distributed")

    actual_libraries = {name for name in records if name.startswith("tools/app-root/usr/lib/")}
    declared_libraries = {"tools/" + row["path"] for row in receipt["includedFiles"]}

    if actual_libraries != declared_libraries:
        raise ValueError("The package contains an undeclared CLI library")

    return receipt


def ffmpeg_evidence(root, records, runtime_identifier):
    """Require actual binaries and exact source/build/configuration material for this RID."""
    lock = read_json(root / "ffmpeg-provenance.json")

    if lock != read_json(root / "tools/ffmpeg-provenance.json"):
        raise ValueError("FFmpeg package and acquisition provenance differ")

    extension = ".exe" if runtime_identifier == "win-x64" else ""

    if any(PurePosixPath(name).name in ("ffplay", "ffplay.exe") for name in records):
        raise ValueError("Unneeded ffplay must not be distributed")

    for key in ("source", "zlib"):
        name = "tools/licenses/ffmpeg/" + relative_path(lock[key]["fileName"])

        if name not in records or records[name]["sha256"] != lock[key]["sha256"]:
            raise ValueError("Pinned FFmpeg/zlib source archive missing or changed")

    recipe_name = "tools/licenses/ffmpeg/build-recipes/" + relative_path(lock["recipe"])
    receipt_name = "tools/licenses/ffmpeg/source-build/build-receipt.json"

    if recipe_name not in records or records[recipe_name]["sha256"] != lock["recipeSha256"] \
            or receipt_name not in records or records[receipt_name]["sha256"] != lock["buildReceiptSha256"]:
        raise ValueError("FFmpeg local source-build recipe/receipt missing or changed")

    receipt = read_json(root / receipt_name)
    matching_inputs = receipt["schemaVersion"] == 1 and receipt["runtimeIdentifier"] == runtime_identifier \
        and receipt["version"] == lock["version"] and receipt["sourceSha256"] == lock["source"]["sha256"] \
        and receipt["zlibSha256"] == lock["zlib"]["sha256"] and receipt["recipeSha256"] == lock["recipeSha256"]
    expected_workers = {tool + extension for tool in ("ffmpeg", "ffprobe")}
    expected_materials = {"configure-arguments.txt", "configure-output.txt", "config.h", "config_components.h", "config.mak",
                          "compiler-version.txt", "compiler-target.txt", "compiler-runtime-macros.txt", "make-version.txt",
                          "assembler-version.txt", "build-host.txt"}
    complete_receipt = len(receipt["workers"]) == len(expected_workers) \
        and {row["path"] for row in receipt["workers"]} == expected_workers \
        and len(receipt["materials"]) == len(expected_materials) \
        and {row["path"] for row in receipt["materials"]} == expected_materials

    if not matching_inputs or not complete_receipt:
        raise ValueError("FFmpeg source-build receipt inputs or complete worker/material set differ")

    for row in receipt["workers"] + receipt["materials"]:
        prefix = "tools/" if row in receipt["workers"] else "tools/licenses/ffmpeg/source-build/"
        name = prefix + relative_path(row["path"])

        if name not in records or records[name]["sha256"] != row["sha256"] or records[name]["size"] != row["size"]:
            raise ValueError("FFmpeg source-build receipt does not match the actual worker/material")

    for tool in ("ffmpeg", "ffprobe"):
        name = "tools/" + tool + extension

        if name not in records:
            raise ValueError("A required pinned FFmpeg executable is missing")

        version = (root / "tools/licenses/ffmpeg" / (tool + "-version.txt")).read_text(encoding="utf-8-sig")
        configuration = (root / "tools/licenses/ffmpeg" / (tool + "-buildconf.txt")).read_text(encoding="utf-8-sig")
        required_configuration = ("--disable-autodetect", "--disable-network", "--disable-everything", "--enable-zlib")
        invalid_configuration = "--enable-nonfree" in configuration or "--enable-gpl" in configuration or "--enable-version3" in configuration \
            or "--enable-lib" in configuration or not all(flag in configuration for flag in required_configuration)

        if not version.startswith(tool + " version " + lock["version"] + " ") or invalid_configuration:
            raise ValueError("FFmpeg actual version/configuration is outside the pinned LGPL build")

        records[name]["componentVersion"] = lock["version"]

    dependencies = read_json(root / "tools/licenses/ffmpeg/runtime-dependencies.json")
    expected_workers = {tool + extension for tool in ("ffmpeg", "ffprobe")}
    workers = dependencies["workers"]

    if dependencies["runtimeIdentifier"] != runtime_identifier or len(workers) != 2 or {worker["path"] for worker in workers} != expected_workers:
        raise ValueError("FFmpeg runtime dependency evidence is for a different worker/platform")

    for worker in workers:

        if worker["sha256"] != records["tools/" + worker["path"]]["sha256"]:
            raise ValueError("FFmpeg runtime dependency evidence does not match the actual worker")

    expected_static_dependencies = [{"component": "zlib", "version": lock["zlib"]["version"], "sourceSha256": lock["zlib"]["sha256"]}]

    if receipt["staticExternalDependencies"] != expected_static_dependencies:
        raise ValueError("FFmpeg static dependency receipt differs from the explicit source-built closure")

    required_notices = ["GCC-GPL-3.0.txt", "GCC-Runtime-Exception-3.1.txt"]

    if runtime_identifier == "win-x64":
        required_notices.append("MinGW-w64-13.0.0-COPYING.MinGW-w64-runtime.txt")

    if any("tools/licenses/upstream-terms/compiler-runtime/" + name not in records for name in required_notices):
        raise ValueError("Source-built compiler-runtime notices/exception are missing")

    return lock


def native_evidence(profile, records, tool_lock, runtime_identifier, components, omitted_paths=frozenset(), mkv_source=None):
    """Apply bounded researched observations only to the pinned, actually shipped components."""
    if profile["pinnedTools"][runtime_identifier] != tool_lock[runtime_identifier]["sha256"]:
        raise ValueError("Native research profile is for a different pinned tool artifact")

    component_ids = {component["id"] for component in components if component["matchedPayloads"]}
    result = []
    for row in profile["components"]:

        if row.get("runtimeIdentifier", runtime_identifier) != runtime_identifier:
            continue

        owner = row.get("owningPackage")

        if owner and "nuget:" + owner not in component_ids:
            continue

        paths = row.get("paths", [])

        replaced_windows_worker = mkv_source and row.get("kind") == "tool-binary-observation" \
            and row.get("runtimeIdentifier") == "win-x64" and paths \
            and set(paths).issubset({"tools/" + name for name in MKV_BUILD_WORKERS})

        if replaced_windows_worker:
            continue

        if paths and all(name in omitted_paths for name in paths):
            continue

        row = dict(row, paths=[name for name in paths if name not in omitted_paths]) if paths else row

        for name in row.get("paths", []):

            if name not in records:
                raise ValueError(f"Native observation references an absent payload: {name}")

            if row.get("sha256") and row["sha256"] != records[name]["sha256"]:
                raise ValueError(f"Native observation hash mismatch: {name}")

            records[name]["nativeComponent"] = row["component"]
            records[name]["componentVersion"] = row.get("version")

        for name in row.get("availableMaterials", []):

            if name not in records:
                raise ValueError(f"Native observation references an absent included material: {name}")

        result.append(row)
    if mkv_source:
        result.append({"component": "MKVToolNix", "version": "102.0", "runtimeIdentifier": "win-x64",
                       "kind": "validated-source-build", "paths": ["tools/" + name for name in sorted(MKV_BUILD_WORKERS)],
                       "observationMethod": "Actual worker/material hashes, exact source/lock/recipe and recursive ordinary-PE import closure; official ZIP observations do not describe these replacement workers.",
                       "provenance": "tools/mkv-source-provenance.json", "receipt": "tools/" + mkv_source["provenance"]["receiptPath"]})

    return result


def generate(package_root, archive, assets_file, output, profile_file, mkv_lock_file=None, mkv_recipe_file=None):
    """Produce a compact report only after all mechanical package checks succeed."""
    root = package_root.resolve()
    records = staged_files(root)
    verify_manifest(root, records)
    verify_archive(archive, root.name, records)
    license_record = records.get("LICENSE")
    expected_license = Path(__file__).parents[1] / "LICENSE"

    if license_record is None or license_record["sha256"] != file_hash(expected_license):
        raise ValueError("The operative application license is missing or differs from the repository LICENSE")

    if "licenses/APPLICATION-LICENSE-PENDING.txt" in records:
        raise ValueError("The obsolete application-license-pending marker must not be packaged")

    build = read_json(root / "build-provenance.json")
    tool_lock = read_json(root / "tool-provenance.json")
    packages = read_json(root / "licenses/nuget/inventory.json")

    if build["runtimeIdentifier"] not in ("win-x64", "linux-x64") or len(build["sourceRevision"]) != 40:
        raise ValueError("Missing exact build revision or runtime identifier")

    source_name = "tools/licenses/corresponding-source/" + PurePosixPath(urlparse(tool_lock["source"]["url"]).path).name

    if source_name not in records or records[source_name]["sha256"] != tool_lock["source"]["sha256"]:
        raise ValueError("The pinned MKVToolNix source archive is missing or changed")

    origins, components = nuget_origins(read_json(assets_file), packages, records)
    profile = read_json(profile_file)
    classify(records, origins, components, tool_lock, profile)
    mkv_source = None
    source_provenance = root / "tools/mkv-source-provenance.json"
    expects_source_build = tool_lock[build["runtimeIdentifier"]].get("kind") == "source-build"

    if source_provenance.exists() or expects_source_build:

        if build["runtimeIdentifier"] != "win-x64":
            raise ValueError("A Windows MKV source build cannot describe another runtime identifier")

        if not source_provenance.exists() or not (root / "mkv-source-provenance.json").is_file() \
                or read_mkv_build_json(source_provenance) != read_mkv_build_json(root / "mkv-source-provenance.json"):
            raise ValueError("MKV source-build package and acquisition provenance differ or are missing")

        tool_records = {name.removeprefix("tools/"): row for name, row in records.items() if name.startswith("tools/")}
        mkv_source = mkv_source_build_evidence(root / "tools", tool_records,
                                             mkv_lock_file or Path(__file__).with_name("mkv-source.lock.json"),
                                             mkv_recipe_file or Path(__file__).with_name("build-mkv-windows.sh"))

        if mkv_source["lock"]["source"]["sha256"] != tool_lock["source"]["sha256"]:
            raise ValueError("MKV source build and the application use different pinned source archives")

        if "tools/payload-selection.json" in records or "tools/licenses/payload-selection.lock.json" in records:
            raise ValueError("Source-built MKV workers cannot inherit an official binary payload-selection receipt")

        for row in mkv_source["receipt"]["workers"] + mkv_source["receipt"]["dependencies"]:
            record = records["tools/" + row["path"]]
            record.update(category="bundled-executable" if row["path"] in MKV_BUILD_WORKERS else "bundled-native-library",
                          component="MKVToolNix/102.0" if row["path"] in MKV_BUILD_WORKERS else row["packageName"],
                          componentVersion="102.0" if row["path"] in MKV_BUILD_WORKERS else row["packageVersion"],
                          origin="Source-built Windows CLI closure; exact source/recipe/configuration and DLL owner sources in mkvSourceBuildEvidence")

        for name, record in records.items():

            if name.startswith("tools/" + MKV_BUILD_MATERIAL_PREFIX) or name == "tools/mkv-source-provenance.json":
                record["origin"] = "Pinned Windows MKV source-build source, recipe or evidence material; see mkvSourceBuildEvidence"

    selection = None if mkv_source else payload_selection_evidence(root, records, tool_lock, build["runtimeIdentifier"])
    ffmpeg_lock = ffmpeg_evidence(root, records, build["runtimeIdentifier"])
    tool_origins(root, records, tool_lock, build["runtimeIdentifier"], ffmpeg_lock, mkv_source)
    omitted_paths = {"tools/" + row["previousPackagePath"] for row in selection["omittedFiles"]} if selection else frozenset()
    observations = native_evidence(profile, records, tool_lock, build["runtimeIdentifier"], components, omitted_paths, mkv_source)
    unknowns = [name for name, record in records.items() if record["category"] == "unresolved-origin"]
    report = {
        "schemaVersion": 1, "purpose": "Distribution inventory and unresolved licensing preparation; not legal approval",
        "redistributionReady": False, "build": build,
        "originalApplicationLicense": {"identifier": "MIT", "copyright": "Copyright (c) 2026 Nexusov",
                                       "path": "LICENSE", "sha256": license_record["sha256"],
                                       "scope": "Original TrackTrim code; third-party terms remain independent"},
        "archive": {"name": archive.name, "size": archive.stat().st_size, "sha256": file_hash(archive)},
        "manifest": records["SHA256SUMS"], "toolProvenance": tool_lock, "ffmpegProvenance": ffmpeg_lock,
        "toolPayloadSelection": selection,
        "mkvSourceBuildEvidence": mkv_source,
        "inventoryGeneratorSha256": file_hash(Path(__file__)),
        "mechanicalChecks": {"manifestCoverage": "passed", "archiveMatchesStaging": "passed", "includedMaterialReferences": "passed", "pinnedMkvSourceHash": "passed", "pinnedMkvSourceBuild": "passed" if mkv_source else "not applicable; pinned upstream payload", "pinnedFfmpegSourceBuildAndVersion": "passed"},
        "counts": dict(Counter(record["category"] for record in records.values())),
        "files": list(records.values()), "nugetComponents": components,
        "buildOnlyDependencies": [".NET SDK (version in build provenance)", "xUnit, Microsoft.NET.Test.Sdk, test runner and Avalonia.Headless (test projects only)", "Python, PowerShell, archive utilities and Linux build/container tools", "GCC/MinGW-w64, binutils, Make, NASM and MSYS2; actual compiler/target and build-tool evidence is bound by the FFmpeg source-build receipt"],
        "nativeComponentEvidence": observations,
        "unresolved": profile["unresolved"] + [{"component": name, "missing": "Exact origin/version and applicable materials", "nextAction": "Inspect the unmatched actual payload before release"} for name in unknowns],
        "limits": [
            "NuGet match proves byte identity to a signed archive member, not complete source or legal compliance.",
            "Omissions are limited to the hash-pinned CLI selection, backed by source, dependency, representative runtime and mandatory packaged-workflow evidence.",
            "Third-party-notice inclusion alone does not prove which optional components were compiled.",
            "Original TrackTrim code is MIT; combined-work analysis and actual covered-source/build delivery remain independent requirements.",
        ],
    }
    output.mkdir(parents=True, exist_ok=True)
    report_path = output / f"distribution-{build['runtimeIdentifier']}.json"
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    for source, destination in {
        "LICENSE": "LICENSE", "SHA256SUMS": "package-SHA256SUMS.txt", "build-provenance.json": "build-provenance.json",
        "tool-provenance.json": "tool-provenance.json", "licenses/nuget/inventory.json": "nuget-inventory.json",
        "ffmpeg-provenance.json": "ffmpeg-provenance.json",
        "tools/file-provenance.json": "tool-file-provenance.json",
        "tools/licenses/upstream-materials.json": "upstream-materials.json",
    }.items():
        shutil.copyfile(root / source, output / destination)

    if selection:
        shutil.copyfile(root / "tools/payload-selection.json", output / "tool-payload-selection.json")

    if mkv_source:
        shutil.copyfile(source_provenance, output / "mkv-source-provenance.json")
        shutil.copyfile(root / "tools" / mkv_source["provenance"]["receiptPath"], output / "mkv-source-build-receipt.json")
        mkv_evidence = output / "mkv-source-build"
        mkv_evidence.mkdir(exist_ok=True)
        for row in mkv_source["receipt"]["materials"]:
            name = relative_path(row["path"])

            if name.startswith("source/"):
                continue

            target = mkv_evidence / name
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(root / "tools" / MKV_BUILD_MATERIAL_PREFIX / name, target)
    for name in ("ffmpeg-version.txt", "ffprobe-version.txt", "ffmpeg-buildconf.txt", "ffprobe-buildconf.txt", "runtime-dependencies.json"):
        shutil.copyfile(root / "tools/licenses/ffmpeg" / name, output / name)
    shutil.copyfile(root / "tools/licenses/ffmpeg/source-build/build-receipt.json", output / "ffmpeg-build-receipt.json")
    build_receipt = read_json(root / "tools/licenses/ffmpeg/source-build/build-receipt.json")
    build_evidence = output / "ffmpeg-source-build"
    build_evidence.mkdir(exist_ok=True)
    for material in build_receipt["materials"]:
        name = relative_path(material["path"])
        shutil.copyfile(root / "tools/licenses/ffmpeg/source-build" / name, build_evidence / name)
    print(f"Distribution inventory: {report_path.name}; {len(records)} shipped files; {len(unknowns)} unmatched origins; redistribution remains pending")
    return report


def main():
    """Use only explicit build inputs; never scan a developer's unrelated files."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package-root", type=Path)
    parser.add_argument("--archive", type=Path)
    parser.add_argument("--assets", type=Path)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--validate-windows-mkv-build", type=Path)
    parser.add_argument("--native-profile", type=Path, default=Path(__file__).with_name("native-components.json"))
    arguments = parser.parse_args()

    if arguments.validate_windows_mkv_build:
        directory = arguments.validate_windows_mkv_build.resolve()
        mkv_source_build_evidence(directory, staged_files(directory), Path(__file__).with_name("mkv-source.lock.json"),
                                  Path(__file__).with_name("build-mkv-windows.sh"), standalone=True)
        print("Verified the exact Windows MKV source-build payload and material closure.")
        return

    if any(value is None for value in (arguments.package_root, arguments.archive, arguments.assets, arguments.output)):
        parser.error("--package-root, --archive, --assets and --output are required for distribution inventory")

    generate(arguments.package_root, arguments.archive, arguments.assets, arguments.output, arguments.native_profile)


if __name__ == "__main__":
    main()
