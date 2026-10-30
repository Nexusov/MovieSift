#!/usr/bin/env bash
set -euo pipefail

package_directory="$(realpath "$1")"
report_path="$2"
payload_directory="$package_directory"
if [[ -d "$package_directory/app" ]]; then
    payload_directory="$package_directory/app"
fi
temporary_report="$(mktemp)"
trap 'rm -f "$temporary_report"' EXIT
missing_dependency=0

while IFS= read -r -d '' candidate; do
    if ! file -Lb "$candidate" | grep -q '^ELF'; then
        continue
    fi

    printf '%s\n' "${candidate#"$package_directory"/}" >> "$temporary_report"
    dependency_output="$(LD_LIBRARY_PATH="$payload_directory/tools/lib:$payload_directory/tools/app-root/usr/lib:$payload_directory/tools/app-root/usr/lib/x86_64-linux-gnu" ldd "$candidate" 2>&1 || true)"
    printf '%s\n\n' "$dependency_output" >> "$temporary_report"

    if grep -q 'not found' <<< "$dependency_output"; then
        missing_lines="$(grep 'not found' <<< "$dependency_output")"

        # .NET 10.0.13 deliberately permits failure to load this optional tracing
        # provider. Retain the normal publish file and record its unavailable
        # diagnostic feature; never ignore a missing production dependency.
        # https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/pal/src/misc/tracepointprovider.cpp#L109-L114
        if [[ "$(basename -- "$candidate")" == 'libcoreclrtraceptprovider.so' ]] \
            && ! grep -qvE '^[[:space:]]*liblttng-ust\.so\.0 => not found[[:space:]]*$' <<< "$missing_lines"; then
            printf '%s\n\n' 'Optional .NET LTTng tracing unavailable; application execution does not require liblttng-ust.so.0.' >> "$temporary_report"
            continue
        fi

        missing_dependency=1
    fi
done < <(
    # ldd resolves transitive dependencies of all CLI workers. Unused retained AppImage
    # GUI libraries are provenance material, not additional application requirements.
    find "$package_directory" -path "$payload_directory/tools/app-root" -prune -o -type f -print0
    printf '%s\0' "$payload_directory/tools/app-root/usr/bin/mkvmerge" "$payload_directory/tools/app-root/usr/bin/mkvextract" "$payload_directory/tools/app-root/usr/bin/mkvpropedit"
)

mv "$temporary_report" "$report_path"

if (( missing_dependency )); then
    printf '%s\n' "Unresolved ELF dependencies; inspect $report_path" >&2
    exit 1
fi
