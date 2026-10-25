#!/usr/bin/env python3
"""Copy the evidenced CLI library closure from fixed upstream artifacts without rewriting payloads."""

import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import shutil
import stat


def relative_path(value):
    """Accept only the fixed, component-relative paths used by the checked selection policy."""
    path = PurePosixPath(value)

    if path.is_absolute() or not path.parts or any(part in ("", ".", "..") for part in path.parts) or "\\" in value or path.as_posix() != value:
        raise ValueError("Invalid tool selection path")

    return value


def select_payload(source, destination, runtime_identifier, policy_path, tool_lock_path):
    """Verify every retained/omitted member before selecting only unchanged runtime dependencies."""
    policy = json.loads(policy_path.read_text(encoding="utf-8"))
    tools = json.loads(tool_lock_path.read_text(encoding="utf-8"))
    selected = policy[runtime_identifier]

    if policy["schemaVersion"] != 1 or policy["version"] != tools["version"] or selected["artifactSha256"] != tools[runtime_identifier]["sha256"]:
        raise ValueError("Tool payload selection targets a different upstream artifact")

    rows = selected["includedFiles"] + selected["omittedFiles"]
    expected = {relative_path(row["sourceMember"]): row for row in rows}

    if len(expected) != len(rows):
        raise ValueError("Duplicate source member in tool payload selection")

    observed = set()
    for path in (source / relative_path(selected["sourceDirectory"])).rglob("*"):
        mode = path.lstat().st_mode

        if stat.S_ISDIR(mode):
            continue

        if not stat.S_ISREG(mode):
            raise ValueError("A selected upstream member is redirected or not a regular file")

        name = path.relative_to(source).as_posix()
        row = expected.get(name)

        if row is None or path.stat().st_size != row["size"] or hashlib.sha256(path.read_bytes()).hexdigest() != row["sha256"]:
            raise ValueError("Pinned retained/omitted payload missing, added or changed: " + name)

        observed.add(name)

    if observed != set(expected):
        raise ValueError("The pinned tool selection does not cover the complete upstream directory")

    for row in selected["includedFiles"]:
        target = destination / relative_path(row["path"])

        if target.exists():
            raise ValueError("Tool payload selection never overwrites an existing file")

        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source / row["sourceMember"], target)

    receipt = {
        "schemaVersion": 1, "runtimeIdentifier": runtime_identifier,
        "artifactSha256": selected["artifactSha256"],
        "policySha256": hashlib.sha256(policy_path.read_bytes()).hexdigest(),
        "includedFiles": selected["includedFiles"], "omittedFiles": selected["omittedFiles"],
        "reason": selected["reason"],
    }
    (destination / "payload-selection.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    material = destination / "licenses/payload-selection.lock.json"
    material.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(policy_path, material)
    return receipt


def main():
    """Use only explicit acquisition paths and the repository's hash-pinned selection policy."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime-identifier", required=True, choices=("win-x64", "linux-x64"))
    parser.add_argument("--source", type=Path, required=True)
    parser.add_argument("--destination", type=Path, required=True)
    arguments = parser.parse_args()
    select_payload(arguments.source, arguments.destination, arguments.runtime_identifier,
                   Path(__file__).with_name("tool-payload.lock.json"), Path(__file__).with_name("tools.lock.json"))


if __name__ == "__main__":
    main()
