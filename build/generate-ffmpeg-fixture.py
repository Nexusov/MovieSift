#!/usr/bin/env python3
"""Generate a small original FFmpeg-origin MKV without normalizing it through MKVToolNix."""

import argparse
import hashlib
import json
import pathlib
import subprocess
import tempfile

DURATION_SECONDS = 2
AUDIO_SAMPLE_RATE = 48000
AUDIO_BITRATE = "32k"
DEVELOPMENT_ARCHIVES = {
    "9.0.2": {
        "url": "https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip",
        "sha256": "60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba",
        "hashEvidence": "Matched the publisher-provided archive checksum.",
    },
    "7.1.1": {
        "url": "https://github.com/GyanD/codexffmpeg/releases/download/7.1.1/ffmpeg-7.1.1-essentials_build.zip",
        "sha256": "04861d3339c5ebe38b56c19a15cf2c0cc97f5de4fa8910e4d47e5e6404e4a2d4",
        "hashEvidence": "Computed from the HTTPS release asset; an independent publisher checksum was unavailable for this older asset.",
    },
}


def run(arguments):
    """Run an explicit development executable and retain its diagnostics on failure."""
    result = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8", check=False)
    if result.returncode != 0:
        raise RuntimeError(f"Development fixture tool exit {result.returncode}: {result.stdout}\n{result.stderr}")
    return result.stdout


def main():
    """Encode original color/tone/subtitle data with legacy German and French language metadata."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", required=True, type=pathlib.Path)
    parser.add_argument("--destination", required=True, type=pathlib.Path)
    parser.add_argument("--version", required=True, choices=DEVELOPMENT_ARCHIVES)
    arguments = parser.parse_args()
    executable = arguments.ffmpeg.resolve()
    destination = arguments.destination.resolve()
    destination.parent.mkdir(parents=True, exist_ok=True)
    version = run([str(executable), "-version"])
    if not version.startswith(f"ffmpeg version {arguments.version}"):
        raise RuntimeError(f"Expected development FFmpeg {arguments.version}, received {version.splitlines()[0]}")
    with tempfile.TemporaryDirectory(prefix="moviesift-original-ffmpeg-") as temporary_path:
        subtitle_path = pathlib.Path(temporary_path) / "original.srt"
        subtitle_path.write_text("1\n00:00:00,100 --> 00:00:01,700\nOriginal subtitle / Sous-titre / Untertitel.\n", encoding="utf-8")
        command = [str(executable), "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                   "-f", "lavfi", "-i", f"color=c=red:size=32x32:rate=25:duration={DURATION_SECONDS}",
                   "-f", "lavfi", "-i", f"sine=frequency=440:sample_rate={AUDIO_SAMPLE_RATE}:duration={DURATION_SECONDS}",
                   "-f", "lavfi", "-i", f"sine=frequency=880:sample_rate={AUDIO_SAMPLE_RATE}:duration={DURATION_SECONDS}",
                   "-f", "srt", "-i", str(subtitle_path),
                   "-map", "0:v:0", "-map", "1:a:0", "-map", "2:a:0", "-map", "3:s:0",
                   "-c:v", "libx264", "-preset", "medium", "-crf", "32", "-pix_fmt", "yuv420p", "-threads:v", "1",
                   "-c:a", "aac", "-profile:a", "aac_low", "-b:a", AUDIO_BITRATE, "-c:s", "srt",
                   "-map_metadata", "-1", "-metadata", "title=Original FFmpeg H264 AAC alias regression",
                   "-metadata:s:a:0", "language=ger", "-metadata:s:a:0", "title=German metadata; original 440 Hz tone",
                   "-metadata:s:a:1", "language=fre", "-metadata:s:a:1", "title=French metadata; original 880 Hz tone",
                   "-metadata:s:s:0", "language=fre", "-fflags", "+bitexact", "-flags:v", "+bitexact",
                   "-flags:a", "+bitexact", "-f", "matroska", str(destination)]
        run(command)
        recorded_command = [argument.replace(temporary_path, "<temporary-directory>") for argument in command]
        recorded_command[0] = "<pinned-development-ffmpeg>"
        recorded_command[-1] = destination.name
    provenance = {
        "origin": "Original synthetic color, two distinct sine tones, and subtitle text generated for MovieSift tests.",
        "normalization": "The checked-in input is the direct FFmpeg Matroska output. No mkvmerge remux or normalization was applied.",
        "developmentToolVersion": version.splitlines()[0],
        "developmentToolExecutableSha256": hashlib.sha256(executable.read_bytes()).hexdigest(),
        "developmentToolConfiguration": version.splitlines(),
        "sourceArchiveUrl": DEVELOPMENT_ARCHIVES[arguments.version]["url"],
        "sourceArchiveSha256": DEVELOPMENT_ARCHIVES[arguments.version]["sha256"],
        "sourceArchiveHashEvidence": DEVELOPMENT_ARCHIVES[arguments.version]["hashEvidence"],
        "fixtureSha256": hashlib.sha256(destination.read_bytes()).hexdigest(),
        "fixtureBytes": destination.stat().st_size,
        "command": recorded_command,
    }
    destination.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(provenance, indent=2))


if __name__ == "__main__":
    main()
