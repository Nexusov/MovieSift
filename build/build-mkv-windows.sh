#!/usr/bin/env bash
# Build the pinned CLI pair in an ephemeral Windows UCRT64 environment.
set -euo pipefail

if [[ "${GITHUB_ACTIONS:-}" != true || "${RUNNER_OS:-}" != Windows ]]; then
  printf '%s\n' 'This recipe requires the isolated UCRT64 Windows Actions builder.' >&2
  exit 1
fi

# --noprofile/--norc intentionally avoids changing other worker build environments.
export MSYSTEM=UCRT64
export MINGW_PREFIX=/ucrt64
export MSYSTEM_PREFIX=/ucrt64

if [[ "$#" != 3 ]]; then
  printf '%s\n' 'Usage: build-mkv-windows.sh source-directory destination material-directory' >&2
  exit 1
fi

source_directory=$(cygpath -u "$1")
destination=$(cygpath -u "$2")
material_directory=$(cygpath -u "$3")
cd "$source_directory"
export LC_ALL=C.UTF-8
export LANG=C.UTF-8
export PATH="/ucrt64/bin:/usr/bin:$PATH"
# Rake's build graph otherwise auto-selects the number of available CPUs.
export DRAKETHREADS=2

configure_arguments=(
  --prefix=/ucrt64
  --disable-gui
  --disable-dbus
  --disable-update-check
  --disable-mkvtoolnix
  --with-boost-filesystem=boost_filesystem-mt
  --with-qmake6=/ucrt64/bin/qmake6
  ac_cv_lib_gtest_main_main=no
)

{
  gcc --version
  g++ --version
  g++ -dumpmachine
  ruby --version
  rake --version
  qmake6 -query
  pacman -Q
} > "$material_directory/compiler-versions.txt"

# Keep the exact shell argument array independently of human-oriented logs.
printf '%s\n' "${configure_arguments[@]}" > "$material_directory/configure-arguments.txt"
CXXFLAGS='-O2 -Wno-ignored-attributes -Wno-template-id-cdtor' ./configure "${configure_arguments[@]}" \
  2>&1 | tee "$material_directory/configure-output.txt"

if ! grep -q '^#define HAVE_FLAC_FORMAT_H 1' config.h; then
  printf '%s\n' 'FLAC support is required; the candidate is not allowed to reduce MKV capabilities.' >&2
  exit 1
fi

# Explicit aliases avoid the GUI, development utilities, installer and default icon batch.
rake -j2 V=1 apps:mkvmerge apps:mkvextract 2>&1 | tee "$material_directory/build-output.txt"
cp src/mkvmerge.exe src/mkvextract.exe "$destination/"
cp config.h build-config config.log "$material_directory/"

"$destination/mkvmerge.exe" --version
"$destination/mkvextract.exe" --version
