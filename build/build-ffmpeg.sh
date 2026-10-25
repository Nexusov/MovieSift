#!/usr/bin/env bash
# Build the two local-file workers from the pinned FFmpeg and zlib sources.
set -euo pipefail

if [[ $# -lt 4 || $# -gt 5 ]]; then
    printf 'Usage: build-ffmpeg.sh RID FFMPEG_SOURCE ZLIB_SOURCE NEW_BUILD_DIRECTORY [CROSS_PREFIX]\n' >&2
    exit 2
fi

readonly runtime_identifier=$1
source_archive=$2
zlib_archive=$3
build_root=$4
readonly cross_prefix=${5-}
readonly script_directory=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
readonly python_command=$(command -v python || command -v python3)

if command -v cygpath >/dev/null 2>&1; then
    source_archive=$(cygpath -u "$source_archive")
    zlib_archive=$(cygpath -u "$zlib_archive")
    build_root=$(cygpath -u "$build_root")
fi

if [[ "$runtime_identifier" != win-x64 && "$runtime_identifier" != linux-x64 ]]; then
    printf 'Unsupported FFmpeg build target.\n' >&2
    exit 2
fi

# FFmpeg's generated makefiles require a build directory without whitespace.
if [[ "$build_root" =~ [[:space:]] || -e "$build_root" ]]; then
    printf 'Use a new build directory without whitespace; existing build directories are not overwritten.\n' >&2
    exit 2
fi

mkdir -p "$build_root/tmp" "$build_root/result" "$build_root/ffmpeg-source" "$build_root/zlib-source" "$build_root/build"
export TMPDIR="$build_root/tmp" LANG=C.UTF-8 LC_ALL=C.UTF-8
# A source archive has no Git metadata; do not discover the enclosing application repository.
export GIT_CEILING_DIRECTORIES="$build_root"
readonly result_directory="$build_root/result"
readonly zlib_prefix="$build_root/zlib-install"
readonly compiler="${cross_prefix}gcc"
readonly make_command=${TRACKTRIM_BUILD_MAKE:-make}

# Validate complete source archives before invoking the upstream extractors/build scripts.
"$python_command" - "$script_directory/ffmpeg.lock.json" "$source_archive" "$zlib_archive" <<'PYTHON'
import hashlib
import json
from pathlib import Path
import sys

lock_path, source_path, zlib_path = map(Path, sys.argv[1:])
lock = json.loads(lock_path.read_text(encoding="utf-8"))
for name, path in (("source", source_path), ("zlib", zlib_path)):
    if hashlib.sha256(path.read_bytes()).hexdigest() != lock[name]["sha256"]:
        raise ValueError("Pinned " + name + " source hash mismatch")
PYTHON

tar -xzf "$source_archive" -C "$build_root/ffmpeg-source" --strip-components=1 --no-same-owner
tar -xJf "$zlib_archive" -C "$build_root/zlib-source" --strip-components=1 --no-same-owner
readonly extra_version=$("$python_command" -c 'import json,sys; print(json.load(open(sys.argv[1]))["extraVersion"])' "$script_directory/ffmpeg.lock.json")
mkdir -p "$zlib_prefix/include" "$zlib_prefix/lib"
cd "$build_root/zlib-source"

if [[ "$runtime_identifier" == win-x64 ]]; then
    "$make_command" -f win32/Makefile.gcc -j4 PREFIX="$cross_prefix" libz.a > "$build_root/zlib-make.log" 2>&1
    cp libz.a "$zlib_prefix/lib/"
    cp zlib.h zconf.h "$zlib_prefix/include/"
else
    ./configure --static --prefix="$zlib_prefix" > "$build_root/zlib-configure.log" 2>&1
    "$make_command" -j4 > "$build_root/zlib-make.log" 2>&1
    "$make_command" check >> "$build_root/zlib-make.log" 2>&1
    "$make_command" install >> "$build_root/zlib-make.log" 2>&1
fi

cd "$build_root/build"
configuration=(
    --prefix="$build_root/install"
    --extra-version="$extra_version"
    --extra-cflags="-I$zlib_prefix/include"
    --extra-ldflags="-L$zlib_prefix/lib"
    --disable-autodetect --disable-network --disable-everything
    --disable-ffplay --disable-doc --disable-debug
    --disable-shared --enable-static --enable-ffmpeg --enable-ffprobe
    --enable-protocol=file,pipe
    --enable-demuxer=mov,matroska,srt,image2,ffmetadata
    --enable-muxer=mov,mp4,image2,data,hash,framemd5,framehash,null,adts,h264,srt
    --enable-parser=h264,aac,mjpeg,png
    --enable-decoder=h264,aac,movtext,subrip,png,mjpeg,pcm_s16le
    --enable-encoder=movtext,aac,mjpeg,png,pcm_s16le
    --enable-bsf=aac_adtstoasc,h264_mp4toannexb,extract_extradata
    --enable-filter=anull,null,format,aformat,aresample,scale,sine
    --enable-indev=lavfi --enable-avfilter --enable-swscale --enable-swresample
    --enable-zlib
)
extension=

if [[ "$runtime_identifier" == win-x64 ]]; then
    extension=.exe
    configuration+=(--target-os=mingw32 --arch=x86_64 --disable-pthreads --enable-w32threads)
    configuration+=(--extra-ldflags="-L$zlib_prefix/lib -static -static-libgcc")
fi

if [[ -n "$cross_prefix" ]]; then
    configuration+=(--enable-cross-compile --cross-prefix="$cross_prefix")
fi

printf '%s\n' "${configuration[@]}" > "$result_directory/configure-arguments.txt"
../ffmpeg-source/configure "${configuration[@]}" > "$result_directory/configure-output.txt" 2>&1
"$make_command" -j4 "ffmpeg$extension" "ffprobe$extension" > "$build_root/ffmpeg-make.log" 2>&1
cp "ffmpeg$extension" "ffprobe$extension" "$result_directory/"
cp config.h config_components.h "$result_directory/"
cp ffbuild/config.mak "$result_directory/config.mak"
"$compiler" --version > "$result_directory/compiler-version.txt"
"$compiler" -dumpmachine > "$result_directory/compiler-target.txt"
if [[ "$runtime_identifier" == win-x64 ]]; then
    printf '#include <_mingw.h>\n' | "$compiler" -dM -E -x c - > "$result_directory/compiler-runtime-macros.txt"
else
    printf '#include <features.h>\n' | "$compiler" -dM -E -x c - > "$result_directory/compiler-runtime-macros.txt"
fi
"$make_command" --version > "$result_directory/make-version.txt"
nasm -v > "$result_directory/assembler-version.txt"
uname -a > "$result_directory/build-host.txt"
"$python_command" - "$script_directory/ffmpeg.lock.json" "$0" "$result_directory" "$runtime_identifier" "$cross_prefix" <<'PYTHON'
import hashlib
import json
from pathlib import Path
import sys

lock_path, recipe_path, directory, runtime_identifier, cross_prefix = sys.argv[1:]
result = Path(directory)
lock = json.loads(Path(lock_path).read_text(encoding="utf-8"))
extension = ".exe" if runtime_identifier == "win-x64" else ""
workers = []
for tool in ("ffmpeg", "ffprobe"):
    path = result / (tool + extension)
    workers.append({"path": path.name, "size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
materials = [{"path": path.name, "size": path.stat().st_size, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()}
             for path in sorted(result.iterdir()) if path.name not in {worker["path"] for worker in workers}]
receipt = {
    "schemaVersion": 1, "runtimeIdentifier": runtime_identifier, "version": lock["version"],
    "sourceSha256": lock["source"]["sha256"], "zlibSha256": lock["zlib"]["sha256"],
    "recipeSha256": hashlib.sha256(Path(recipe_path).read_bytes()).hexdigest(),
    "buildMethod": "cross compilation" if cross_prefix else "native compilation",
    "workers": workers, "materials": materials,
    "staticExternalDependencies": [{"component": "zlib", "version": lock["zlib"]["version"], "sourceSha256": lock["zlib"]["sha256"]}],
    "scope": "The disabled-autodetect build selects native FFmpeg implementations and explicit static zlib only; no optional BtbN codec or hardware libraries. Compiler runtime/OS dependencies retain their own terms. Actual execution and dependency checks are recorded by acquisition."
}
(result / "build-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
PYTHON
printf 'Built %s workers at %s\n' "$runtime_identifier" "$result_directory"
