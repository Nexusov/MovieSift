#!/bin/bash
set -euo pipefail

if (( $(id -u) == 0 )); then
    printf '%s\n' 'The portable acceptance container must run without root.' >&2
    exit 1
fi

for unavailable_tool in dotnet mkvmerge mkvextract ffmpeg ffprobe; do
    if command -v "$unavailable_tool" >/dev/null 2>&1; then
        printf '%s\n' "Unexpected installed development/media tool: $unavailable_tool" >&2
        exit 1
    fi
done

application='/check/app Кириллица/TrackTrim-linux-x64/TrackTrim'
source='/check/input Кириллица/--source with spaces; &.mkv'
destination='/check/output Кириллица/result with spaces; &.mkv'
evidence='/check/evidence'
original_hash="$(sha256sum "$source" | cut -d ' ' -f 1)"

mkdir -p "$evidence"
cp /usr/local/share/tracktrim-smoke/environment.txt "$evidence/container-environment.txt"
{
    printf 'Runtime user: %s:%s\n' "$(id -u)" "$(id -g)"
    printf '%s\n' 'Installed SDK/runtime/media commands: absent' 'Container network: disabled by docker run --network none'
    printf 'Original source SHA256: %s\n' "$original_hash"
} >> "$evidence/container-environment.txt"

cd /tmp
"$application" --verify-tools
"$application" --verify-portable "$source" "$destination"

if [[ ! -f "$destination" ]]; then
    printf '%s\n' 'The clean-container operation did not publish its output.' >&2
    exit 1
fi

final_hash="$(sha256sum "$source" | cut -d ' ' -f 1)"

if [[ "$original_hash" != "$final_hash" ]]; then
    printf '%s\n' 'The clean-container operation changed its source.' >&2
    exit 1
fi

printf 'Published output: %s\nUnchanged source SHA256: %s\n' "$destination" "$final_hash" >> "$evidence/container-environment.txt"
printf '%s\n' 'Portable CLI workflow passed without installed .NET/media tools, without network, and without root.'
