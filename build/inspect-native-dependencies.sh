#!/usr/bin/env bash
set -euo pipefail

package_directory="$(realpath "$1")"
report_path="$2"
temporary_report="$(mktemp)"
trap 'rm -f "$temporary_report"' EXIT
missing_dependency=0

while IFS= read -r -d '' candidate; do
    if ! file -Lb "$candidate" | grep -q '^ELF'; then
        continue
    fi

    printf '%s\n' "${candidate#"$package_directory"/}" >> "$temporary_report"
    dependency_output="$(LD_LIBRARY_PATH="$package_directory/tools/lib:$package_directory/tools/app-root/usr/lib:$package_directory/tools/app-root/usr/lib/x86_64-linux-gnu" ldd "$candidate" 2>&1 || true)"
    printf '%s\n\n' "$dependency_output" >> "$temporary_report"

    if grep -q 'not found' <<< "$dependency_output"; then
        missing_dependency=1
    fi
done < <(
    # ldd resolves transitive dependencies of the CLI pair. Unused retained AppImage
    # GUI libraries are provenance material, not additional application requirements.
    find "$package_directory" -path "$package_directory/tools/app-root" -prune -o -type f -print0
    printf '%s\0' "$package_directory/tools/app-root/usr/bin/mkvmerge" "$package_directory/tools/app-root/usr/bin/mkvextract"
)

mv "$temporary_report" "$report_path"

if (( missing_dependency )); then
    printf '%s\n' "Unresolved ELF dependencies; inspect $report_path" >&2
    exit 1
fi
