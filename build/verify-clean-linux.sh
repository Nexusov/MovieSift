#!/bin/bash
set -euo pipefail

script_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
repository_directory="$(cd -- "$script_directory/.." && pwd)"
archive_path="$(realpath "$1")"
fixture_path="$(realpath "$2")"
mp4_fixture_path="$(realpath "$3")"
check_directory="$(mktemp -d "$repository_directory/artifacts/clean Linux Кириллица.XXXXXXXX")"

mkdir -p "$check_directory/app Кириллица" "$check_directory/input Кириллица" "$check_directory/output Кириллица" "$check_directory/evidence"
tar -xzf "$archive_path" -C "$check_directory/app Кириллица"
cp -- "$fixture_path" "$check_directory/input Кириллица/--source with spaces; &.mkv"
cp -- "$mp4_fixture_path" "$check_directory/input Кириллица/--source with spaces; &.mp4"

docker build --platform linux/amd64 --file "$script_directory/portable-smoke.Dockerfile" --tag tracktrim-portable-smoke "$script_directory"
docker run --rm --platform linux/amd64 --network none --read-only \
    --user "$(id -u):$(id -g)" --cap-drop ALL --security-opt no-new-privileges \
    --env LANG=C --env LC_ALL=C --env LC_CTYPE=C \
    --tmpfs /tmp:rw,nosuid,nodev,noexec \
    --mount "type=bind,source=$check_directory,target=/check" \
    tracktrim-portable-smoke | tee "$check_directory/evidence/container-workflow.txt"

printf 'Environment and workflow evidence: %s\n' "$check_directory/evidence"
