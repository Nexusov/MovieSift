"""Prepare inventory-bound review materials without copying runtime workers."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import shutil
import stat
import subprocess
import tarfile
import zipfile


HASH_PATTERN = re.compile(r"[a-f0-9]{64}")
REVISION_PATTERN = re.compile(r"[a-f0-9]{40}")
COPY_PREFIXES = ("licenses/", "tools/licenses/")
COPY_RECORDS = frozenset({
    "LICENSE", "THIRD_PARTY_NOTICES.md", "SHA256SUMS", "build-provenance.json",
    "tool-provenance.json", "ffmpeg-provenance.json", "mkv-source-provenance.json",
    "tools/file-provenance.json",
})
REQUIRED_RECORDS = COPY_RECORDS - {"mkv-source-provenance.json"}
PENDING_MARKER = "licenses/APPLICATION-LICENSE-PENDING.txt"
COPYRIGHT = "Copyright (c) 2026 Nexusov"


def digest(path):
    """Hash complete artifacts without retaining their contents in memory."""
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def relative_path(value):
    """Reject ambiguous archive/inventory paths before creating any output file."""
    if not isinstance(value, str) or not value or "\\" in value or ":" in value or any(ord(character) < 32 for character in value):
        raise ValueError("Unsafe material path")
    parts = value.split("/")

    if any(part in ("", ".", "..") for part in parts) or PurePosixPath(value).is_absolute():
        raise ValueError("Unsafe material path")
    return value


def git_output(repository, *arguments):
    """Resolve only explicit source objects, without changing repository state."""
    try:
        return subprocess.check_output(["git", "-C", str(repository), *arguments], stderr=subprocess.PIPE)
    except subprocess.CalledProcessError as failure:
        raise ValueError("The identified source commit or its required material cannot be resolved") from failure


def file_record(path, name=None):
    """Describe exact local bytes using a public-safe relative name."""
    return {"name": name or path.name, "size": path.stat().st_size, "sha256": digest(path)}


def load_inputs(repository, archive_path, inventory_path):
    """Require the exact final binary archive and a resolvable clean source revision."""
    inventory = json.loads(inventory_path.read_text(encoding="utf-8-sig"))

    if inventory.get("schemaVersion") != 1:
        raise ValueError("Unsupported distribution inventory schema")
    build = inventory["build"]
    revision = build["sourceRevision"]
    valid_build = build.get("worktreeDirty") is False and isinstance(revision, str) \
        and REVISION_PATTERN.fullmatch(revision) and build.get("runtimeIdentifier") in ("win-x64", "linux-x64")

    if not valid_build:
        raise ValueError("Review materials require an identified clean package source revision")
    resolved = git_output(repository, "rev-parse", revision + "^{commit}").decode().strip()
    source_tree = git_output(repository, "rev-parse", revision + "^{tree}").decode().strip()

    if resolved != revision or build.get("sourceTree") != source_tree:
        raise ValueError("The package source revision/tree does not match the resolved commit")

    if inventory["archive"] != file_record(archive_path):
        raise ValueError("The binary archive name, size or SHA256 differs from its inventory")
    records = {}
    for row in inventory["files"]:
        name = relative_path(row["path"])
        valid_record = type(row.get("size")) is int and row["size"] >= 0 \
            and isinstance(row.get("sha256"), str) and HASH_PATTERN.fullmatch(row["sha256"])

        if name in records or not valid_record:
            raise ValueError("Duplicate or invalid inventory material record")
        records[name] = row

    if PENDING_MARKER in records:
        raise ValueError("Obsolete application-license pending marker is present")

    if not REQUIRED_RECORDS.issubset(records):
        raise ValueError("A required source/notice/provenance record is absent")
    license_bytes = git_output(repository, "show", revision + ":LICENSE")

    if not license_bytes.startswith(b"MIT License") or COPYRIGHT.encode() not in license_bytes:
        raise ValueError("The source commit does not contain the selected MIT/Nexusov license")
    license_record = records["LICENSE"]

    if license_record["size"] != len(license_bytes) or license_record["sha256"] != hashlib.sha256(license_bytes).hexdigest():
        raise ValueError("The packaged LICENSE differs from the identified source commit")
    return inventory, records


def copy_package(archive_path, inventory, records, target):
    """Adapt the existing material copier, binding every copied member to its inventory."""
    wanted = {name for name in records if name.startswith(COPY_PREFIXES) or name in COPY_RECORDS}
    seen = set()
    seen_files = set()
    copied = []
    prefix = "TrackTrim-" + inventory["build"]["runtimeIdentifier"]

    def accept_member(member_name, size, directory, regular, open_stream):
        """Reject unsafe, duplicate and nonregular members rather than extracting them."""
        checked_name = relative_path(member_name[:-1] if directory and member_name.endswith("/") else member_name)

        if checked_name in seen or not regular:
            raise ValueError("Duplicate or nonregular archive member")
        seen.add(checked_name)

        if checked_name != prefix and not checked_name.startswith(prefix + "/"):
            raise ValueError("Archive member is outside the expected package root")

        if directory:
            return
        name = checked_name.removeprefix(prefix + "/")

        if name not in records or size != records[name]["size"]:
            raise ValueError("Archive member is missing from or differs in size from the inventory")
        seen_files.add(name)

        if name not in wanted:
            return
        output = target.joinpath(*PurePosixPath(name).parts)
        output.parent.mkdir(parents=True, exist_ok=True)
        with open_stream() as source, output.open("xb") as destination:
            shutil.copyfileobj(source, destination)

        if output.stat().st_size != records[name]["size"] or digest(output) != records[name]["sha256"]:
            raise ValueError("Included material differs from the package inventory")
        copied.append({"path": name, "size": records[name]["size"], "sha256": records[name]["sha256"]})

    if archive_path.suffix == ".zip":
        with zipfile.ZipFile(archive_path) as archive:
            for member in archive.infolist():
                mode = member.external_attr >> 16
                regular = not stat.S_ISLNK(mode) and (not stat.S_IFMT(mode) or stat.S_ISREG(mode) or member.is_dir())
                accept_member(member.filename, member.file_size, member.is_dir(), regular,
                              lambda member=member: archive.open(member))
    else:
        with tarfile.open(archive_path, "r:gz") as archive:
            for member in archive:
                accept_member(member.name, member.size, member.isdir(), member.isfile() or member.isdir(),
                              lambda member=member: archive.extractfile(member))

    if seen_files != records.keys() or {row["path"] for row in copied} != wanted:
        raise ValueError("Some inventoried package files or source/notice materials are absent")
    return sorted(copied, key=lambda row: row["path"])


def prepare(repository, archive_path, inventory_path, output):
    """Create one matching material bundle and a compact CI-safe identity report."""
    inventory, records = load_inputs(repository, archive_path, inventory_path)
    build = inventory["build"]
    revision = build["sourceRevision"]
    output.mkdir(parents=True, exist_ok=False)
    staging = output / "review-materials"
    target = staging / build["runtimeIdentifier"]
    copied = copy_package(archive_path, inventory, records, target)
    shutil.copyfile(inventory_path, target / "distribution-inventory.json")
    source = staging / ("TrackTrim-source-" + revision[:12] + ".tar.gz")
    subprocess.run(["git", "-C", str(repository), "archive", "--format=tar.gz",
                    "--prefix=TrackTrim-source-" + revision[:12] + "/", "--output", str(source.resolve()), revision], check=True)
    license_evidence = {"spdxId": "MIT", "copyright": COPYRIGHT, "path": "LICENSE", "sha256": records["LICENSE"]["sha256"]}
    provenance = {
        "schemaVersion": 1, "purpose": "source/build-material review", "publicDeliveryPerformed": False,
        "completeRedistributionAssurance": False, "sourceRevision": revision, "sourceTree": build["sourceTree"],
        "runtimeIdentifier": build["runtimeIdentifier"], "binaryArchive": inventory["archive"],
        "sourceArchive": file_record(source), "originalApplicationLicense": license_evidence, "copiedMaterials": copied,
    }
    (staging / "materials-provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    (staging / "READ-ME-FIRST.txt").write_text(
        "Review materials; preparing this bundle does not publish it.\n"
        "Workflow artifacts in a public repository are not private; check the run's visibility and retention.\n"
        "Original TrackTrim code is MIT-licensed. Third-party components retain their own terms.\n"
        "Package materials match the identified binary inventory; no standalone runtime workers are copied.\n"
        "Deliver applicable covered-source/build materials and notices with any eventual authorized public binaries.\n",
        encoding="utf-8")
    files = sorted(path for path in staging.rglob("*") if path.is_file())
    (staging / "SHA256SUMS").write_text("".join(digest(path) + "  " + path.relative_to(staging).as_posix() + "\n" for path in files), encoding="utf-8")
    archive = output / ("TrackTrim-" + build["runtimeIdentifier"] + "-materials-" + revision[:12] + ".tar.gz")
    with tarfile.open(archive, "w:gz") as bundle:
        bundle.add(staging, arcname="TrackTrim-review-materials", recursive=True)
    summary = {key: provenance[key] for key in ("schemaVersion", "sourceRevision", "sourceTree", "runtimeIdentifier", "binaryArchive",
                                               "originalApplicationLicense", "publicDeliveryPerformed", "completeRedistributionAssurance")}
    summary.update(archive=file_record(archive), sourceArchive=provenance["sourceArchive"], materialFiles=len(copied), manifestedFiles=len(files))
    (output / "material-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    (output / (archive.name + ".sha256")).write_text(summary["archive"]["sha256"] + "  " + archive.name + "\n", encoding="utf-8")
    return summary


def main():
    """Accept explicit artifacts only; never search unrelated local files or publish results."""
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repository", "archive", "inventory", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    arguments = parser.parse_args()
    summary = prepare(arguments.repository.resolve(), arguments.archive.resolve(), arguments.inventory.resolve(), arguments.output.resolve())
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    main()
