"""Stage only the exact verified binary/material pair and its compact identity reports."""

import argparse
import importlib.util
import json
from pathlib import Path
import re
import shutil
import stat


SPECIFICATION = importlib.util.spec_from_file_location("review_materials", Path(__file__).with_name("prepare-review-materials.py"))
MATERIALS = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(MATERIALS)
RUN_ID_PATTERN = re.compile(r"[1-9][0-9]*")
ARCHIVE_NAMES = {"win-x64": "TrackTrim-win-x64.zip", "linux-x64": "TrackTrim-linux-x64.tar.gz"}
SUMMARY_IDENTITIES = ("sourceRevision", "sourceTree", "runtimeIdentifier")


def require_local_path(path, directory=False):
    """Reject missing, redirected and nonregular retention inputs before reading them."""
    for ancestor in (path, *path.parents):
        metadata = ancestor.lstat()
        redirected = stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & stat.FILE_ATTRIBUTE_REPARSE_POINT

        if redirected:
            raise ValueError("Candidate retention paths must not be redirected")
    valid = stat.S_ISDIR(path.stat().st_mode) if directory else stat.S_ISREG(path.stat().st_mode)

    if not valid:
        raise ValueError("Candidate retention requires regular files and directories")


def prepare(repository, archive, inventory_path, materials, runtime, source_revision, run_id, run_attempt, output):
    """Validate a complete checked set before copying its unchanged bytes into fresh staging."""
    valid_identity = runtime in ARCHIVE_NAMES and MATERIALS.REVISION_PATTERN.fullmatch(source_revision) \
        and RUN_ID_PATTERN.fullmatch(run_id) and RUN_ID_PATTERN.fullmatch(run_attempt)

    if not valid_identity:
        raise ValueError("Invalid candidate runtime, source revision or run identity")
    require_local_path(archive)
    require_local_path(inventory_path)
    require_local_path(materials, directory=True)
    summary_path = materials / "material-summary.json"
    require_local_path(summary_path)
    material_name = "TrackTrim-" + runtime + "-materials-" + source_revision[:12] + ".tar.gz"
    material_archive = materials / material_name
    require_local_path(material_archive)
    inventory, _ = MATERIALS.load_inputs(repository, archive, inventory_path)
    build = inventory["build"]
    checkout = MATERIALS.git_output(repository, "rev-parse", "HEAD").decode().strip()
    correct_checkout = build["sourceRevision"] == source_revision == checkout and build["runtimeIdentifier"] == runtime \
        and archive.name == ARCHIVE_NAMES[runtime]

    if not correct_checkout:
        raise ValueError("Candidate source/runtime differs from the checked revision")
    summary = json.loads(summary_path.read_text(encoding="utf-8-sig"))
    matching_summary = summary.get("schemaVersion") == 1 and all(summary.get(key) == build[key] for key in SUMMARY_IDENTITIES) \
        and summary.get("binaryArchive") == inventory["archive"]

    if not matching_summary:
        raise ValueError("Material summary does not match the checked binary inventory")

    if summary.get("archive") != MATERIALS.file_record(material_archive):
        raise ValueError("Material archive name, size or SHA256 differs from its summary")
    identities = [
        (archive, ARCHIVE_NAMES[runtime], inventory["archive"]), (material_archive, material_name, summary["archive"]),
        (inventory_path, "distribution-" + runtime + ".json", MATERIALS.file_record(inventory_path, "distribution-" + runtime + ".json")),
        (summary_path, "material-summary.json", MATERIALS.file_record(summary_path)),
    ]
    require_local_path(output.parent, directory=True)
    output.mkdir(exist_ok=False)
    for path, name, identity in identities:
        destination = output / name
        shutil.copyfile(path, destination)

        if MATERIALS.file_record(destination) != identity:
            raise ValueError("Candidate bytes changed during staging")
    (output / "SHA256SUMS").write_text(
        "".join(identity["sha256"] + "  " + name + "\n" for _, name, identity in identities), encoding="utf-8")
    return {
        "artifactName": "candidate-" + runtime + "-" + source_revision + "-run-" + run_id + "-attempt-" + run_attempt,
        "materialsName": material_name,
    }


def main():
    """Accept explicit CI paths and identities; this helper never uploads or dispatches anything."""
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("repository", "archive", "inventory", "materials", "output"):
        parser.add_argument("--" + name, type=Path, required=True)
    for name in ("runtime", "source-revision", "run-id", "run-attempt"):
        parser.add_argument("--" + name, required=True)
    arguments = parser.parse_args()
    selection = prepare(arguments.repository.absolute(), arguments.archive.absolute(), arguments.inventory.absolute(),
                        arguments.materials.absolute(), arguments.runtime, arguments.source_revision,
                        arguments.run_id, arguments.run_attempt, arguments.output.absolute())
    print(json.dumps(selection))


if __name__ == "__main__":
    main()
