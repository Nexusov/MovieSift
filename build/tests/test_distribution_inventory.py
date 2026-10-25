"""Exercise distribution evidence against tiny real archives, not media or disk filling."""

import base64
import contextlib
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import struct
import tarfile
import tempfile
import unittest
import zipfile


SPECIFICATION = importlib.util.spec_from_file_location("inventory", Path(__file__).parents[1] / "distribution-inventory.py")
INVENTORY = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(INVENTORY)


class DistributionInventoryTests(unittest.TestCase):
    """Verify complete actual payload coverage and fail closed on missing evidence."""

    def setUp(self):
        """Create an isolated package with one exact NuGet payload and notice."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.root = self.directory / "TrackTrim-win-x64"
        self.root.mkdir()
        self.archive = self.directory / "TrackTrim-win-x64.zip"
        self.assets = self.directory / "assets.json"
        self.profile = self.directory / "profile.json"
        self.output = self.directory / "evidence"
        self.write("Sample.dll", b"published sample assembly")
        self.write("TrackTrim.dll", b"original application")
        self.write("LICENSE", (Path(__file__).parents[2] / "LICENSE").read_bytes())
        self.write("unknown.dat", b"explicitly unknown")
        self.write("tools/licenses/corresponding-source/source.tar.xz", b"small source archive stand-in")
        self.json("tools/licenses/upstream-materials.json", [])
        self.json("build-provenance.json", {"runtimeIdentifier": "win-x64", "sourceRevision": "1" * 40, "worktreeDirty": False})
        self.json("tool-provenance.json", {
            "version": "102.0", "win-x64": {"sha256": "2" * 64},
            "source": {"url": "https://example.invalid/source.tar.xz", "sha256": INVENTORY.file_hash(self.root / "tools/licenses/corresponding-source/source.tar.xz")},
        })
        self.profile.write_text(json.dumps({"pinnedTools": {"win-x64": "2" * 64}, "components": [], "unresolved": []}), encoding="utf-8")
        cache = self.directory / "cache" / "sample" / "1.0.0"
        cache.mkdir(parents=True)
        self.nupkg = cache / "sample.1.0.0.nupkg"
        with zipfile.ZipFile(self.nupkg, "w") as package:
            package.writestr("lib/net10.0/Sample.dll", b"published sample assembly")
            package.writestr("LICENSE", b"original license text")
            package.writestr("sample.nuspec", b"package specification")
        signed_hash = base64.b64encode(hashlib.sha512(self.nupkg.read_bytes()).digest()).decode("ascii")
        self.nupkg.with_suffix(".nupkg.sha512").write_text(signed_hash, encoding="utf-8")
        self.assets.write_text(json.dumps({"packageFolders": {str(self.directory / "cache"): {}}}), encoding="utf-8")
        self.write("licenses/nuget/Sample/1.0.0/LICENSE", b"original license text")
        self.write("licenses/nuget/Sample/1.0.0/sample.nuspec", b"package specification")
        self.json("licenses/nuget/inventory.json", [{
            "id": "Sample", "version": "1.0.0", "license": "MIT", "sha512": "different-signature-independent-content-hash",
            "repositoryUrl": "https://example.invalid/sample", "repositoryCommit": "3" * 40,
            "includedLicenseFiles": ["LICENSE"], "specificationFile": "sample.nuspec", "supplementalMaterials": [],
        }])
        self.write("tools/ffmpeg.exe", b"tiny ffmpeg stand-in")
        self.write("tools/ffprobe.exe", b"tiny ffprobe stand-in")
        ffmpeg_lock = {"schemaVersion": 2, "version": "test-pinned", "recipe": "build-ffmpeg.sh"}
        for key, name in (("source", "source.tar.gz"), ("zlib", "zlib.tar.xz")):
            self.write("tools/licenses/ffmpeg/" + name, ("small " + key + " source stand-in").encode())
            ffmpeg_lock[key] = {"fileName": name, "sha256": INVENTORY.file_hash(self.root / "tools/licenses/ffmpeg" / name)}
        ffmpeg_lock["zlib"]["version"] = "1.3.2"
        self.write("tools/licenses/ffmpeg/build-recipes/build-ffmpeg.sh", b"controlled source recipe")
        ffmpeg_lock["recipeSha256"] = INVENTORY.file_hash(self.root / "tools/licenses/ffmpeg/build-recipes/build-ffmpeg.sh")
        receipt = {"schemaVersion": 1, "runtimeIdentifier": "win-x64", "version": "test-pinned",
                   "sourceSha256": ffmpeg_lock["source"]["sha256"], "zlibSha256": ffmpeg_lock["zlib"]["sha256"],
                   "recipeSha256": ffmpeg_lock["recipeSha256"], "workers": [], "materials": [],
                   "staticExternalDependencies": [{"component": "zlib", "version": "1.3.2", "sourceSha256": ffmpeg_lock["zlib"]["sha256"]}]}
        for name in ("ffmpeg.exe", "ffprobe.exe"):
            receipt["workers"].append({"path": name, "size": (self.root / "tools" / name).stat().st_size,
                                       "sha256": INVENTORY.file_hash(self.root / "tools" / name)})
        material_names = ("configure-arguments.txt", "configure-output.txt", "config.h", "config_components.h", "config.mak",
                          "compiler-version.txt", "compiler-target.txt", "compiler-runtime-macros.txt", "make-version.txt",
                          "assembler-version.txt", "build-host.txt")
        for name in material_names:
            target = "tools/licenses/ffmpeg/source-build/" + name
            self.write(target, ("controlled " + name).encode())
            receipt["materials"].append({"path": name, "size": (self.root / target).stat().st_size,
                                         "sha256": INVENTORY.file_hash(self.root / target)})
        self.json("tools/licenses/ffmpeg/source-build/build-receipt.json", receipt)
        ffmpeg_lock["buildReceiptSha256"] = INVENTORY.file_hash(self.root / "tools/licenses/ffmpeg/source-build/build-receipt.json")
        self.json("ffmpeg-provenance.json", ffmpeg_lock)
        self.json("tools/ffmpeg-provenance.json", ffmpeg_lock)
        for tool in ("ffmpeg", "ffprobe"):
            self.write("tools/licenses/ffmpeg/" + tool + "-version.txt", (tool + " version test-pinned Copyright fixture\n").encode())
            self.write("tools/licenses/ffmpeg/" + tool + "-buildconf.txt", b"configuration: --disable-autodetect --disable-network --disable-everything --enable-zlib\n")
        for name in ("GCC-GPL-3.0.txt", "GCC-Runtime-Exception-3.1.txt", "MinGW-w64-13.0.0-COPYING.MinGW-w64-runtime.txt"):
            self.write("tools/licenses/upstream-terms/compiler-runtime/" + name, b"controlled complete notice stand-in")
        self.json("tools/licenses/ffmpeg/runtime-dependencies.json", {"runtimeIdentifier": "win-x64", "workers": [{"path": name, "sha256": INVENTORY.file_hash(self.root / "tools" / name)} for name in ("ffmpeg.exe", "ffprobe.exe")]})
        self.selection_policy = {"schemaVersion": 1, "win-x64": {"artifactSha256": "2" * 64, "includedFiles": [], "omittedFiles": []}}
        self.json("tools/licenses/payload-selection.lock.json", self.selection_policy)
        self.json("tools/payload-selection.json", {"schemaVersion": 1, "runtimeIdentifier": "win-x64", "artifactSha256": "2" * 64,
                                                  "policySha256": INVENTORY.file_hash(self.root / "tools/licenses/payload-selection.lock.json"),
                                                  "includedFiles": [], "omittedFiles": []})
        tool_records = []
        for path in (self.root / "tools").rglob("*"):

            if path.is_file():
                tool_records.append({"path": path.relative_to(self.root / "tools").as_posix(), "size": path.stat().st_size, "sha256": INVENTORY.file_hash(path), "upstreamArchiveSha256": ""})
        self.json("tools/file-provenance.json", tool_records)
        self.pack()

    def write(self, name, value):
        """Write a fixture payload under the owned temporary directory."""
        destination = self.root / name
        destination.parent.mkdir(parents=True, exist_ok=True)
        destination.write_bytes(value)

    def json(self, name, value):
        """Write a fixture evidence input with the real production schema."""
        self.write(name, json.dumps(value).encode("utf-8"))

    def pack(self, tar=False):
        """Create the real manifest and final ZIP or tar.gz from current fixture bytes."""
        records = INVENTORY.staged_files(self.root)
        manifest = "".join(f"{record['sha256']}  {name}\n" for name, record in records.items() if name != "SHA256SUMS")
        self.write("SHA256SUMS", manifest.encode("utf-8"))

        if tar:
            self.archive = self.directory / "TrackTrim-win-x64.tar.gz"
            with tarfile.open(self.archive, "w:gz") as package:
                package.add(self.root, arcname=self.root.name)
            return

        with zipfile.ZipFile(self.archive, "w") as package:
            for path in self.root.rglob("*"):

                if path.is_file():
                    package.write(path, arcname=self.root.name + "/" + path.relative_to(self.root).as_posix())

    def generate(self):
        """Run the public evidence entry point with only explicit fixture inputs."""
        with contextlib.redirect_stdout(io.StringIO()):
            return INVENTORY.generate(self.root, self.archive, self.assets, self.output, self.profile,
                                      getattr(self, "mkv_lock", None), getattr(self, "mkv_recipe", None))

    def refresh_tool_origins(self):
        """Refresh acquisition evidence after deliberately constructing controlled tool payloads."""
        records = []
        for path in (self.root / "tools").rglob("*"):

            if path.is_file() and path.name != "file-provenance.json":
                records.append({"path": path.relative_to(self.root / "tools").as_posix(), "size": path.stat().st_size,
                                "sha256": INVENTORY.file_hash(path), "upstreamArchiveSha256": ""})

        self.json("tools/file-provenance.json", records)

    def pe_payload(self, imports):
        """Build a tiny file-backed PE import table; no fixture executable is ever launched."""
        data = bytearray(2048)
        data[:2] = b"MZ"
        struct.pack_into("<I", data, 60, 128)
        data[128:132] = b"PE\0\0"
        struct.pack_into("<H", data, 134, 1)
        struct.pack_into("<H", data, 148, 240)
        struct.pack_into("<H", data, 152, 0x20B)
        struct.pack_into("<II", data, 272, 4096, (len(imports) + 1) * 20)
        struct.pack_into("<IIII", data, 400, 1536, 4096, 1536, 512)
        name_offset = 1024
        for index, name in enumerate(imports):
            encoded = name.encode("ascii") + b"\0"
            struct.pack_into("<IIIII", data, 512 + index * 20, 0, 0, 0, 4096 + name_offset - 512, 0)
            data[name_offset:name_offset + len(encoded)] = encoded
            name_offset += len(encoded)
        return bytes(data)

    def prepare_mkv_source_build(self, source_notices=False, source_notice_package="mingw-w64-ucrt-x86_64-gmp"):
        """Create a complete source-built Windows tool/material closure with real PE import evidence."""
        self.mkv_lock = self.directory / "mkv-source.lock.json"
        self.mkv_recipe = self.directory / "build-mkv-windows.sh"
        self.mkv_recipe.write_bytes(b"controlled exact MKV source-build recipe\n")
        source_data = (self.root / "tools/licenses/corresponding-source/source.tar.xz").read_bytes()
        source = {"url": "https://example.invalid/source.tar.xz", "fileName": "source.tar.xz", "sha256": hashlib.sha256(source_data).hexdigest()}
        dependency_source = b"complete controlled Qt source package"
        package = {"name": "mingw-w64-ucrt-x86_64-qt6-base", "version": "6.10.2-1",
                   "fileName": "qt6-base.pkg.tar.zst", "url": "https://example.invalid/qt6-base.pkg.tar.zst", "sha256": "7" * 64,
                   "sourcePackage": {"fileName": "qt6-base.src.tar.zst", "url": "https://example.invalid/qt6-base.src.tar.zst",
                                     "sha256": hashlib.sha256(dependency_source).hexdigest()}}
        notice_data = {"README": b"controlled GMP copyright and dual-license grant", "COPYINGv2": b"controlled complete GPL2 text"}

        if source_notices:
            package.update(name=source_notice_package, version="6.3.0-3")
            package["sourceNotices"] = {"sourcePackageMember": "mingw-w64-gmp/gmp-6.3.0.tar.xz", "sourceArchiveSha256": "8" * 64,
                                        "members": [{"path": "gmp-6.3.0/" + name, "fileName": name, "size": len(data),
                                                     "sha256": hashlib.sha256(data).hexdigest()} for name, data in notice_data.items()]}
        recipe_notice = b"controlled original recipe copyright and license notice"
        recipe_license = {"fileName": "MSYS2-MINGW-packages-LICENSE", "url": "https://example.invalid/pinned-recipe/LICENSE",
                          "sha256": hashlib.sha256(recipe_notice).hexdigest(), "size": len(recipe_notice)}
        lock = {"schemaVersion": 1, "version": "102.0", "source": source, "packages": [package],
                "recipeSnapshot": {"license": recipe_license}}
        self.mkv_lock.write_text(json.dumps(lock), encoding="utf-8")
        prefix = "tools/licenses/mkv-source-build/"
        self.write(prefix + "mkv-source.lock.json", self.mkv_lock.read_bytes())
        self.write(prefix + "build-mkv-windows.sh", self.mkv_recipe.read_bytes())
        self.write(prefix + recipe_license["fileName"], recipe_notice)
        for name in ("collect-mkv-build.py", "provision-windows-mkv.ps1", "ffmpeg-runtime-dependencies.py"):
            self.write(prefix + name, (Path(__file__).parents[1] / name).read_bytes())
        self.write(prefix + "source/source.tar.xz", source_data)
        self.write(prefix + "source/qt6-base.src.tar.zst", dependency_source)
        for name in ("config.h", "build-config", "config.log", "compiler-versions.txt", "notices/Qt-COPYING"):
            self.write(prefix + name, ("controlled " + name).encode())
        self.json(prefix + "configure-arguments.json", ["--disable-gui", "--disable-update-check"])
        self.json(prefix + "installed-packages.json", [package])
        package_evidence = dict(package, noticePaths=["notices/Qt-COPYING"])

        if source_notices:
            notice_members = []
            for name, data in notice_data.items():
                path = "packages/" + package["name"] + "/notices/" + name
                self.write(prefix + path, data)
                notice_members.append({"path": path, "upstreamMember": "gmp-6.3.0/" + name,
                                       "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
            package_evidence["noticePaths"] = [row["path"] for row in notice_members]
            package_evidence["sourceNoticeEvidence"] = {"sourcePackageSha256": package["sourcePackage"]["sha256"],
                                                       "sourcePackageMember": package["sourceNotices"]["sourcePackageMember"],
                                                       "sourceArchiveSha256": package["sourceNotices"]["sourceArchiveSha256"], "members": notice_members}
        self.json(prefix + "dependency-packages.json", [package_evidence])
        imports = {"mkvmerge.exe": ["Qt6Core.dll", "KERNEL32.dll"], "mkvextract.exe": ["Qt6Core.dll", "KERNEL32.dll"],
                   "Qt6Core.dll": ["KERNEL32.dll"]}
        for name, names in imports.items():
            self.write("tools/" + name, self.pe_payload(names))
        self.mkv_receipt = {"schemaVersion": 1, "version": "102.0", "runtimeIdentifier": "win-x64",
                            "sourceSha256": source["sha256"], "lockSha256": INVENTORY.file_hash(self.mkv_lock),
                            "recipeSha256": INVENTORY.file_hash(self.mkv_recipe), "workers": [], "dependencies": [], "materials": [],
                            "dllImports": [{"path": name, "imports": names} for name, names in imports.items()],
                            "configureArguments": ["--disable-gui", "--disable-update-check"], "buildEnvironment": {"compiler": "controlled"}}
        for name in imports:
            path = self.root / "tools" / name
            row = {"path": name, "size": path.stat().st_size, "sha256": INVENTORY.file_hash(path)}

            if name.endswith(".dll"):
                row.update(packageName=package["name"], packageVersion=package["version"], upstreamMember="ucrt64/bin/" + name)
                self.mkv_receipt["dependencies"].append(row)
                continue

            self.mkv_receipt["workers"].append(row)
        for path in (self.root / prefix).rglob("*"):

            if path.is_file():
                self.mkv_receipt["materials"].append({"path": path.relative_to(self.root / prefix).as_posix(),
                                                       "size": path.stat().st_size, "sha256": INVENTORY.file_hash(path)})
        self.mkv_provenance = {"schemaVersion": 1, "component": "MKVToolNix", "version": "102.0", "runtimeIdentifier": "win-x64",
                               "buildKind": "windows-ucrt64-source", "source": source, "lockSha256": INVENTORY.file_hash(self.mkv_lock),
                               "recipeSha256": INVENTORY.file_hash(self.mkv_recipe), "receiptPath": "licenses/mkv-source-build/build-receipt.json"}
        (self.root / "tools/payload-selection.json").unlink()
        (self.root / "tools/licenses/payload-selection.lock.json").unlink()
        self.write_mkv_receipt()

    def write_mkv_receipt(self):
        """Bind a controlled receipt to both actual acquisition and packaged provenance."""
        self.json("tools/" + self.mkv_provenance["receiptPath"], self.mkv_receipt)
        self.mkv_provenance["receiptSha256"] = INVENTORY.file_hash(self.root / "tools" / self.mkv_provenance["receiptPath"])
        self.json("tools/mkv-source-provenance.json", self.mkv_provenance)
        self.json("mkv-source-provenance.json", self.mkv_provenance)
        self.refresh_tool_origins()
        self.pack()

    def test_operative_application_license_is_inventoried_and_copied(self):
        """Bind the selected original-code license to the actual shipped bytes."""
        report = self.generate()
        license_record = report["originalApplicationLicense"]
        self.assertEqual(license_record["identifier"], "MIT")
        self.assertEqual(license_record["copyright"], "Copyright (c) 2026 Nexusov")
        self.assertEqual(license_record["sha256"], INVENTORY.file_hash(self.root / "LICENSE"))
        self.assertEqual((self.output / "LICENSE").read_bytes(), (self.root / "LICENSE").read_bytes())
        records = {record["path"]: record for record in report["files"]}
        self.assertEqual(records["LICENSE"]["category"], "original-or-generated-project-output")
        self.assertFalse(report["redistributionReady"])

    def test_filee_adaptation_notice_is_bound_to_reference_bytes(self):
        """Keep the adapted style copyright separate from original TrackTrim documentation."""
        notice = Path(__file__).parents[2] / "docs/third-party/Filee-LICENSE.txt"
        self.write("docs/third-party/Filee-LICENSE.txt", notice.read_bytes())
        self.pack()
        report = self.generate()
        record = next(item for item in report["files"] if item["path"] == "docs/third-party/Filee-LICENSE.txt")
        self.assertEqual(record["category"], "third-party-notice")
        self.assertEqual(record["sha256"], INVENTORY.file_hash(notice))
        self.write("docs/third-party/Filee-LICENSE.txt", b"truncated notice")
        self.pack()

        with self.assertRaisesRegex(ValueError, "Filee"):
            self.generate()

    def test_missing_application_license_is_rejected(self):
        """Manifest coverage cannot excuse a package without its operative license."""
        (self.root / "LICENSE").unlink()
        self.pack()

        with self.assertRaisesRegex(ValueError, "operative application license"):
            self.generate()

    def test_changed_application_license_is_rejected(self):
        """A self-consistent archive must still carry the actual repository grant."""
        self.write("LICENSE", b"unapproved replacement terms")
        self.pack()

        with self.assertRaisesRegex(ValueError, "operative application license"):
            self.generate()

    def test_obsolete_application_license_marker_is_rejected(self):
        """An obsolete pending claim is invalid even when present in the manifest."""
        self.write("licenses/APPLICATION-LICENSE-PENDING.txt", b"No license selected")
        self.pack()

        with self.assertRaisesRegex(ValueError, "pending marker"):
            self.generate()

    def test_zip_accounts_for_every_actual_byte_and_explicit_unknown(self):
        """Report the final hash, every file, exact origin and unresolved payload."""
        report = self.generate()
        self.assertEqual(report["archive"]["sha256"], INVENTORY.file_hash(self.archive))
        self.assertEqual(len(report["files"]), len(INVENTORY.staged_files(self.root)))
        self.assertFalse(report["redistributionReady"])
        self.assertTrue(any(row["component"] == "unknown.dat" for row in report["unresolved"]))
        component = report["nugetComponents"][0]
        self.assertEqual(component["matchedPayloads"], ["Sample.dll"])
        self.assertNotEqual(component["restoreContentHash"], component["signedArchiveSha512"])
        self.assertEqual(len(component["materialEvidence"]), 2)

    def test_tar_archive_is_compared_to_staging(self):
        """Exercise the Linux archive format using real tiny tar members."""
        self.pack(tar=True)
        self.assertEqual(self.generate()["mechanicalChecks"]["archiveMatchesStaging"], "passed")

    def test_hidden_build_material_is_included_in_tar_inventory(self):
        """Keep dotfiles such as upstream .cmake.conf in complete file accounting."""
        self.write("docs/.cmake.conf", b"hidden source build material")
        self.pack(tar=True)
        self.assertTrue(any(row["path"] == "docs/.cmake.conf" for row in self.generate()["files"]))

    def test_unlisted_staged_file_fails(self):
        """An unmanifested payload cannot disappear from the accounting."""
        self.write("omitted.dll", b"unlisted")
        with self.assertRaisesRegex(ValueError, "manifest does not cover"):
            self.generate()

    def test_changed_manifested_payload_fails(self):
        """Reject staging changes after manifest construction."""
        self.write("Sample.dll", b"changed")
        with self.assertRaisesRegex(ValueError, "manifest mismatch"):
            self.generate()

    def test_final_archive_extra_file_fails(self):
        """The final archive must not contain unaccounted bytes."""
        with zipfile.ZipFile(self.archive, "a") as package:
            package.writestr(self.root.name + "/extra.dll", b"unexpected")
        with self.assertRaisesRegex(ValueError, "unexpected archive"):
            self.generate()

    def test_final_archive_changed_payload_fails(self):
        """Bind the report to archive contents rather than staging alone."""
        self.write("Sample.dll", b"different")
        self.pack()
        self.write("Sample.dll", b"published sample assembly")
        records = INVENTORY.staged_files(self.root)
        self.write("SHA256SUMS", "".join(f"{row['sha256']}  {name}\n" for name, row in records.items() if name != "SHA256SUMS").encode())
        with self.assertRaisesRegex(ValueError, "Archive/staging byte mismatch"):
            self.generate()

    def test_notice_must_match_original_package_not_only_manifest(self):
        """A stale notice remains invalid even if both stage and archive agree."""
        self.write("licenses/nuget/Sample/1.0.0/LICENSE", b"unrelated notice")
        self.pack()
        with self.assertRaisesRegex(ValueError, "differs from nupkg"):
            self.generate()

    def test_missing_required_notice_fails(self):
        """A missing included material is a visible evidence failure."""
        (self.root / "licenses/nuget/Sample/1.0.0/LICENSE").unlink()
        self.pack()
        with self.assertRaisesRegex(ValueError, "material is not shipped"):
            self.generate()

    def test_missing_original_nupkg_fails(self):
        """Do not silently replace original package evidence with the restore graph."""
        self.nupkg.unlink()
        with self.assertRaisesRegex(ValueError, "original nupkg missing"):
            self.generate()

    def test_nupkg_archive_sidecar_mismatch_fails(self):
        """Validate signed bytes without confusing NuGet content-hash semantics."""
        self.nupkg.with_suffix(".nupkg.sha512").write_text("wrong", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "archive/sidecar mismatch"):
            self.generate()

    def test_incorrect_source_archive_fails(self):
        """A similarly named source archive cannot satisfy the pinned material check."""
        self.write("tools/licenses/corresponding-source/source.tar.xz", b"wrong source")
        self.pack()
        with self.assertRaisesRegex(ValueError, "source archive is missing or changed"):
            self.generate()

    def test_unaccounted_tool_payload_fails(self):
        """Retained unused tool libraries still require acquisition evidence."""
        self.write("tools/app-root/usr/lib/unused.a", b"static archive")
        self.pack()
        with self.assertRaisesRegex(ValueError, "undeclared CLI library"):
            self.generate()

    def test_wrong_native_observation_platform_is_excluded(self):
        """Never present another platform's researched materials as actual evidence."""
        self.profile.write_text(json.dumps({"pinnedTools": {"win-x64": "2" * 64}, "components": [{"runtimeIdentifier": "linux-x64", "availableMaterials": ["absent"], "component": "Linux-only"}], "unresolved": []}), encoding="utf-8")
        self.assertEqual(self.generate()["nativeComponentEvidence"], [])

    def test_escaping_archive_path_fails(self):
        """Reject ambiguous path names rather than publishing misleading inventory."""
        with zipfile.ZipFile(self.archive, "a") as package:
            package.writestr(self.root.name + "/../outside", b"escape")
        with self.assertRaisesRegex(ValueError, "Unsafe relative path"):
            self.generate()

    def test_missing_revision_fails(self):
        """An anonymous package cannot produce an accepted distribution report."""
        self.json("build-provenance.json", {"runtimeIdentifier": "win-x64", "sourceRevision": ""})
        self.pack()
        with self.assertRaisesRegex(ValueError, "exact build revision"):
            self.generate()

    def test_ffmpeg_materials_and_actual_executables_are_accounted_for(self):
        """Keep both backends and their exact-source evidence in the full inventory."""
        report = self.generate()
        self.assertEqual(report["mechanicalChecks"]["pinnedFfmpegSourceBuildAndVersion"], "passed")
        ffmpeg = next(row for row in report["files"] if row["path"] == "tools/ffmpeg.exe")
        self.assertEqual(ffmpeg["componentVersion"], "test-pinned")
        self.assertEqual(ffmpeg["category"], "bundled-executable")
        self.assertEqual(report["ffmpegProvenance"]["recipeSha256"], INVENTORY.file_hash(self.root / "tools/licenses/ffmpeg/build-recipes/build-ffmpeg.sh"))
        self.assertFalse(any(row["component"] == "FFmpeg static third-party closure" for row in report["unresolved"]))

    def test_ffmpeg_source_or_recipe_hash_must_match(self):
        """A similarly named source/build archive cannot satisfy the new tool evidence."""
        self.write("tools/licenses/ffmpeg/build-recipes/build-ffmpeg.sh", b"wrong recipes")
        self.pack()
        with self.assertRaisesRegex(ValueError, "FFmpeg local source-build recipe/receipt"):
            self.generate()

    def test_nonfree_or_gpl_configuration_is_not_the_selected_build(self):
        """Fail packaging on accidental substitution of another FFmpeg variant."""
        self.write("tools/licenses/ffmpeg/ffmpeg-buildconf.txt", b"configuration: --enable-version3 --enable-nonfree\n")
        self.pack()
        with self.assertRaisesRegex(ValueError, "outside the pinned LGPL build"):
            self.generate()

    def test_unneeded_ffplay_is_rejected(self):
        """Do not ship a development player without a production runtime purpose."""
        self.write("tools/ffplay.exe", b"unneeded player")
        self.pack()
        with self.assertRaisesRegex(ValueError, "ffplay must not be distributed"):
            self.generate()

    def test_lgpl_v3_configuration_cannot_claim_lgpl_21(self):
        """Keep the effective license consistent with the explicitly selected build."""
        path = self.root / "tools/licenses/ffmpeg/ffmpeg-buildconf.txt"
        path.write_text(path.read_text() + " --enable-version3", encoding="utf-8")
        self.pack()
        with self.assertRaisesRegex(ValueError, "outside the pinned LGPL build"):
            self.generate()

    def test_missing_ffprobe_cannot_silently_produce_inventory(self):
        """Both required executables must be actual package payloads."""
        (self.root / "tools/ffprobe.exe").unlink()
        self.pack()
        with self.assertRaisesRegex(ValueError, "does not match the actual worker/material"):
            self.generate()

    def test_dependency_observation_must_match_actual_ffmpeg_worker(self):
        """Do not carry a native-dependency receipt across different binary builds."""
        self.write("tools/ffmpeg.exe", b"substituted worker")
        self.pack()
        with self.assertRaisesRegex(ValueError, "does not match the actual worker/material"):
            self.generate()

    def test_package_must_preserve_acquired_build_provenance(self):
        """A bare input lock must not overwrite the acquisition's actual recipe/worker receipt."""
        lock = json.loads((self.root / "ffmpeg-provenance.json").read_text())
        lock.pop("recipeSha256")
        lock.pop("buildReceiptSha256")
        self.json("ffmpeg-provenance.json", lock)
        self.pack()
        with self.assertRaisesRegex(ValueError, "package and acquisition provenance differ"):
            self.generate()

    def test_missing_source_build_material_fails_visibly(self):
        """A version label alone cannot replace complete source/configuration evidence."""
        (self.root / "tools/licenses/ffmpeg/source-build/config.h").unlink()
        self.pack()
        with self.assertRaisesRegex(ValueError, "actual worker/material"):
            self.generate()

    def test_changed_zlib_source_is_rejected(self):
        """The one explicit static dependency must retain its exact complete source archive."""
        self.write("tools/licenses/ffmpeg/zlib.tar.xz", b"different similarly named source")
        self.pack()
        with self.assertRaisesRegex(ValueError, "FFmpeg/zlib source archive"):
            self.generate()

    def test_receipted_omitted_asset_cannot_reappear(self):
        """The exact omission policy is checked against the actual package files."""
        omitted = {"previousPackagePath": "licenses/data/gui-resource.rcc", "sourceMember": "data/gui-resource.rcc", "size": 1, "sha256": "0" * 64}
        self.selection_policy["win-x64"]["omittedFiles"] = [omitted]
        self.json("tools/licenses/payload-selection.lock.json", self.selection_policy)
        self.json("tools/payload-selection.json", {"schemaVersion": 1, "runtimeIdentifier": "win-x64", "artifactSha256": "2" * 64,
                                                  "policySha256": INVENTORY.file_hash(self.root / "tools/licenses/payload-selection.lock.json"),
                                                  "includedFiles": [], "omittedFiles": [omitted]})
        self.write("tools/licenses/data/gui-resource.rcc", b"unwanted")
        self.pack()
        with self.assertRaisesRegex(ValueError, "expressly excluded"):
            self.generate()

    def test_source_built_mkv_uses_actual_sources_workers_and_imports(self):
        """A replacement reports its own exact closure instead of inheriting official ZIP claims."""
        self.prepare_mkv_source_build()
        self.profile.write_text(json.dumps({"pinnedTools": {"win-x64": "2" * 64}, "components": [
            {"component": "mkvmerge.exe", "runtimeIdentifier": "win-x64", "kind": "tool-binary-observation",
             "paths": ["tools/mkvmerge.exe"], "sha256": "0" * 64},
        ], "unresolved": []}), encoding="utf-8")
        report = self.generate()
        self.assertEqual("passed", report["mechanicalChecks"]["pinnedMkvSourceBuild"])
        self.assertIsNone(report["toolPayloadSelection"])
        self.assertEqual("validated-source-build", report["nativeComponentEvidence"][0]["kind"])
        self.assertEqual(self.mkv_provenance, report["mkvSourceBuildEvidence"]["provenance"])
        self.assertTrue((self.output / "mkv-source-build-receipt.json").is_file())
        self.assertTrue((self.output / "mkv-source-build/config.h").is_file())
        self.assertFalse((self.output / "mkv-source-build/source").exists())
        dependency = next(row for row in report["files"] if row["path"] == "tools/Qt6Core.dll")
        self.assertEqual("bundled-native-library", dependency["category"])
        self.assertEqual("mingw-w64-ucrt-x86_64-qt6-base", dependency["component"])
        self.assertEqual("6.10.2-1", dependency["componentVersion"])
        worker = next(row for row in report["files"] if row["path"] == "tools/mkvmerge.exe")
        self.assertEqual("MKVToolNix/102.0", worker["component"])
        self.assertEqual("102.0", worker["componentVersion"])

    def test_source_built_mkv_changed_worker_or_material_is_rejected(self):
        """A freshly manifested package must still match its exact compiled worker/material receipt."""
        self.prepare_mkv_source_build()
        for name in ("tools/mkvmerge.exe", "tools/licenses/mkv-source-build/config.h"):
            with self.subTest(name=name):
                original = (self.root / name).read_bytes()
                self.write(name, b"substituted content")
                self.refresh_tool_origins()
                self.pack()
                with self.assertRaisesRegex(ValueError, "does not match the actual worker/material"):
                    self.generate()
                self.write(name, original)

    def test_source_built_mkv_missing_dependency_source_is_rejected(self):
        """A notice or approximate archive cannot stand in for the pinned DLL owner's complete source."""
        self.prepare_mkv_source_build()
        name = "source/qt6-base.src.tar.zst"
        self.mkv_receipt["materials"] = [row for row in self.mkv_receipt["materials"] if row["path"] != name]
        (self.root / "tools/licenses/mkv-source-build" / name).unlink()
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "exact dependency sources/notices"):
            self.generate()

    def test_source_built_mkv_undeclared_dll_is_rejected_from_actual_pe(self):
        """Installed developer DLLs cannot satisfy an unshipped portable dependency."""
        self.prepare_mkv_source_build()
        self.write("tools/mkvmerge.exe", self.pe_payload(["Qt6Core.dll", "unshipped.dll"]))
        row = next(row for row in self.mkv_receipt["workers"] if row["path"] == "mkvmerge.exe")
        row.update(size=(self.root / "tools/mkvmerge.exe").stat().st_size, sha256=INVENTORY.file_hash(self.root / "tools/mkvmerge.exe"))
        next(row for row in self.mkv_receipt["dllImports"] if row["path"] == "mkvmerge.exe")["imports"] = ["Qt6Core.dll", "unshipped.dll"]
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "Undeclared MKV.*unshipped"):
            self.generate()

    def test_source_built_mkv_cannot_reuse_official_payload_selection(self):
        """Do not attribute a local build to omitted members of the unrelated official ZIP."""
        self.prepare_mkv_source_build()
        self.json("tools/payload-selection.json", {"schemaVersion": 1})
        self.refresh_tool_origins()
        self.pack()
        with self.assertRaisesRegex(ValueError, "cannot inherit an official binary"):
            self.generate()

    def test_source_built_mkv_package_provenance_must_match_acquisition(self):
        """A package must retain actual acquired identity rather than replacing it with an input lock."""
        self.prepare_mkv_source_build()
        self.json("mkv-source-provenance.json", {"version": "102.0"})
        self.pack()
        with self.assertRaisesRegex(ValueError, "package and acquisition provenance differ"):
            self.generate()

    def test_source_built_mkv_wrong_recipe_and_extra_payload_are_rejected(self):
        """Require both the chosen recipe and complete, bounded payload accounting."""
        self.prepare_mkv_source_build()
        self.mkv_recipe.write_bytes(b"another source-build recipe")
        with self.assertRaisesRegex(ValueError, "pinned source/lock/recipe"):
            self.generate()
        self.mkv_recipe.write_bytes(b"controlled exact MKV source-build recipe\n")
        self.write("tools/unlisted.dll", self.pe_payload(["KERNEL32.dll"]))
        self.refresh_tool_origins()
        self.pack()
        with self.assertRaisesRegex(ValueError, "cover exactly the actual build files"):
            self.generate()

    def test_source_built_mkv_control_scripts_and_required_materials_are_bound(self):
        """The collector/provisioner cannot be substituted behind an otherwise valid recipe identity."""
        self.prepare_mkv_source_build()
        name = "collect-mkv-build.py"
        self.write("tools/licenses/mkv-source-build/" + name, b"different collection logic")
        row = next(row for row in self.mkv_receipt["materials"] if row["path"] == name)
        path = self.root / "tools/licenses/mkv-source-build" / name
        row.update(size=path.stat().st_size, sha256=INVENTORY.file_hash(path))
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "included source/lock/recipe differs"):
            self.generate()

    def test_source_build_record_rejects_nonregular_or_oversized_files(self):
        """A directory/FIFO cannot block record loading, and JSON is bounded before decoding."""
        path = self.directory / "invalid-receipt"
        path.mkdir()
        with self.assertRaisesRegex(ValueError, "bounded regular file"):
            INVENTORY.read_mkv_build_json(path)
        path.rmdir()
        path.write_bytes(b" " * (INVENTORY.MKV_BUILD_JSON_BYTES + 1))
        with self.assertRaisesRegex(ValueError, "bounded regular file"):
            INVENTORY.read_mkv_build_json(path)

    def test_source_built_mkv_recipe_snapshot_notice_is_required_and_exact(self):
        """A source recipe's author notice cannot be omitted or replaced behind a fresh receipt."""
        self.prepare_mkv_source_build()
        name = "MSYS2-MINGW-packages-LICENSE"
        path = self.root / "tools/licenses/mkv-source-build" / name
        row = next(row for row in self.mkv_receipt["materials"] if row["path"] == name)
        self.mkv_receipt["materials"].remove(row)
        path.unlink()
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "required source/configuration/notices are missing"):
            self.generate()
        path.write_bytes(b"replacement recipe terms")
        row.update(size=path.stat().st_size, sha256=INVENTORY.file_hash(path))
        self.mkv_receipt["materials"].append(row)
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "included source/lock/recipe differs"):
            self.generate()

    def test_source_built_mkv_gmp_notices_require_exact_nested_source_evidence(self):
        """The explicit GMP source route keeps its grant/text while unrelated packages retain the normal notice gate."""
        self.prepare_mkv_source_build(source_notices=True)
        self.assertEqual("passed", self.generate()["mechanicalChecks"]["pinnedMkvSourceBuild"])
        path = self.root / "tools/licenses/mkv-source-build/dependency-packages.json"
        packages = json.loads(path.read_text(encoding="utf-8"))
        packages[0]["sourceNoticeEvidence"]["sourceArchiveSha256"] = "0" * 64
        self.json("tools/licenses/mkv-source-build/dependency-packages.json", packages)
        row = next(row for row in self.mkv_receipt["materials"] if row["path"] == "dependency-packages.json")
        row.update(size=path.stat().st_size, sha256=INVENTORY.file_hash(path))
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "notice evidence differs"):
            self.generate()
        packages[0]["sourceNoticeEvidence"]["sourceArchiveSha256"] = "8" * 64
        self.json("tools/licenses/mkv-source-build/dependency-packages.json", packages)
        row.update(size=path.stat().st_size, sha256=INVENTORY.file_hash(path))
        notice = packages[0]["noticePaths"][0]
        notice_path = self.root / "tools/licenses/mkv-source-build" / notice
        notice_path.write_bytes(b"substituted grant")
        notice_row = next(row for row in self.mkv_receipt["materials"] if row["path"] == notice)
        notice_row.update(size=notice_path.stat().st_size, sha256=INVENTORY.file_hash(notice_path))
        self.write_mkv_receipt()
        with self.assertRaisesRegex(ValueError, "source notice bytes differ"):
            self.generate()

    def test_source_built_mkv_icu_source_notice_policy_is_accepted(self):
        """The exact ICU source-notice route uses the same complete package/member/hash validation."""
        self.prepare_mkv_source_build(source_notices=True, source_notice_package="mingw-w64-ucrt-x86_64-icu")
        self.assertEqual("passed", self.generate()["mechanicalChecks"]["pinnedMkvSourceBuild"])

    def test_source_built_mkv_vorbis_source_notice_policy_is_accepted(self):
        """The exact Vorbis source-notice route does not require a nonexistent binary notice directory."""
        self.prepare_mkv_source_build(source_notices=True, source_notice_package="mingw-w64-ucrt-x86_64-libvorbis")
        self.assertEqual("passed", self.generate()["mechanicalChecks"]["pinnedMkvSourceBuild"])

    def test_unlisted_package_cannot_use_source_notice_fallback(self):
        """A new empty-notice package requires its own concrete assessment instead of a generic waiver."""
        self.prepare_mkv_source_build(source_notices=True, source_notice_package="unlisted-runtime-package")
        with self.assertRaisesRegex(ValueError, "lacks its explicit package policy"):
            self.generate()


if __name__ == "__main__":
    unittest.main()
