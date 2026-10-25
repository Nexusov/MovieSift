#!/usr/bin/env python3
"""Derive small original MP4 fixtures from the checked-in synthetic H.264/AAC source."""

import argparse
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import zlib
from mp4_chapter_fixtures import write_chapter_reference_fixture


SOURCE_SHA256 = "b0b5fc3c0e290fb435be3cc09a1952ccfc343d93c2d6d13e9e8332e55b791000"
FRAME_WIDTH = 16
FRAME_HEIGHT = 16


def run(arguments):
    """Run a development-only fixture command with explicit arguments and visible failures."""
    result = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8", check=False)
    if result.returncode:
        raise RuntimeError(f"Fixture command exited {result.returncode}: {result.stdout}\n{result.stderr}")
    return result.stdout


def digest(path):
    """Bind provenance to complete input/output bytes."""
    return hashlib.sha256(path.read_bytes()).hexdigest()


def png_chunk(kind, payload):
    """Write a minimal original PNG chunk without an image-generation runtime dependency."""
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", zlib.crc32(kind + payload))


def write_cover(path):
    """Create a tiny distinguishable RGB artwork fixture using the standard library only."""
    header = struct.pack(">IIBBBBB", FRAME_WIDTH, FRAME_HEIGHT, 8, 2, 0, 0, 0)
    pixels = b"".join(b"\0" + bytes([180, row * 8, 40]) * FRAME_WIDTH for row in range(FRAME_HEIGHT))
    path.write_bytes(b"\x89PNG\r\n\x1a\n" + png_chunk(b"IHDR", header)
                     + png_chunk(b"IDAT", zlib.compress(pixels)) + png_chunk(b"IEND", b""))


def main():
    """Produce reproducible baseline, reordered, multi-video and rejected fragmented inputs."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ffmpeg", required=True, type=Path)
    parser.add_argument("--destination", required=True, type=Path)
    arguments = parser.parse_args()
    executable = arguments.ffmpeg.resolve()
    destination = arguments.destination.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).resolve().parent.parent / "tests/MovieSift.Infrastructure.Tests/Fixtures/h264-aac-no-default-duration.mkv"
    source_hash = digest(source)
    if source_hash != SOURCE_SHA256:
        raise RuntimeError("The original fixture identity changed.")
    version = run([str(executable), "-version"]).splitlines()
    cover = destination / "original artwork.png"
    write_cover(cover)
    subtitles = destination / "original forced.srt"
    subtitles.write_text("1\n00:00:00,200 --> 00:00:01,500\nOriginal forced caption / Проверка.\n", encoding="utf-8")
    chapters = destination / "original chapters.ffmetadata"
    chapters.write_text(";FFMETADATA1\ntitle=MovieSift original MP4\nartist=MovieSift synthetic fixture\ncomment=Original test content\n"
                        "[CHAPTER]\nTIMEBASE=1/1000\nSTART=0\nEND=1000\ntitle=First chapter\n"
                        "[CHAPTER]\nTIMEBASE=1/1000\nSTART=1000\nEND=2000\ntitle=Вторая глава\n", encoding="utf-8")
    commands = []
    common = [str(executable), "-hide_banner", "-loglevel", "warning", "-nostdin", "-y"]
    ordinary = destination / "ordinary.mp4"
    # Existing encoded H.264/AAC is copied. Only SRT -> mov_text fixture preparation uses an encoder.
    command = common + ["-i", str(source), "-i", str(subtitles), "-f", "ffmetadata", "-i", str(chapters),
                        "-i", str(cover), "-map", "0:v:0", "-map", "0:a:0", "-map", "0:a:1", "-map", "0:a:0",
                        "-map", "0:s:0", "-map", "1:s:0", "-map", "3:v:0", "-c", "copy", "-c:s", "mov_text",
                        "-map_metadata", "2", "-map_chapters", "2",
                        "-metadata:s:v:0", "handler_name=Original AVC video",
                        "-metadata:s:v:0", "title=Original AVC video",
                        "-metadata:s:a:0", "language=eng", "-metadata:s:a:0", "handler_name=Main [cut].* audio",
                        "-metadata:s:a:0", "title=Main [cut].* audio",
                        "-metadata:s:a:1", "language=eng", "-metadata:s:a:1", "handler_name=Alternate audio",
                        "-metadata:s:a:1", "title=Alternate audio",
                        "-metadata:s:a:2", "language=eng", "-metadata:s:a:2", "handler_name=Commentary audio",
                        "-metadata:s:a:2", "title=Commentary audio",
                        "-metadata:s:s:0", "language=rus", "-metadata:s:s:0", "handler_name=Captions",
                        "-metadata:s:s:0", "title=Captions",
                        "-metadata:s:s:1", "language=rus", "-metadata:s:s:1", "handler_name=Captions",
                        "-metadata:s:s:1", "title=Captions",
                        "-disposition:a:0", "default", "-disposition:a:1", "0", "-disposition:a:2", "0",
                        "-disposition:s:0", "0", "-disposition:s:1", "forced",
                        "-disposition:v:1", "attached_pic", "-f", "mp4", str(ordinary)]
    run(command)
    commands.append(command)
    jpeg_cover = destination / "original artwork.jpg"
    command = common + ["-i", str(cover), "-frames:v", "1", "-c:v", "mjpeg", "-update", "1", str(jpeg_cover)]
    run(command)
    commands.append(command)
    jpeg_artwork = destination / "jpeg-artwork.mp4"
    command = common + ["-i", str(ordinary), "-i", str(jpeg_cover), "-map", "0:0", "-map", "0:1",
                        "-map", "0:2", "-map", "0:3", "-map", "0:4", "-map", "0:5", "-map", "1:v:0",
                        "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-disposition:6", "attached_pic",
                        "-f", "mp4", str(jpeg_artwork)]
    run(command)
    commands.append(command)
    reordered = destination / "reordered.mp4"
    command = common + ["-i", str(ordinary), "-map", "0:5", "-map", "0:2", "-map", "0:0", "-map", "0:3",
                        "-map", "0:1", "-map", "0:4", "-map", "0:v:1", "-map_metadata", "0", "-map_chapters", "0",
                        "-c", "copy", "-disposition:6", "attached_pic",
                        # The MOV muxer writes udta/name from title, not its demuxer's name key.
                        "-metadata:s:0", "title=Captions", "-metadata:s:1", "title=Alternate audio",
                        "-metadata:s:2", "title=Original AVC video", "-metadata:s:3", "title=Commentary audio",
                        "-metadata:s:4", "title=Main [cut].* audio", "-metadata:s:5", "title=Captions",
                        "-f", "mp4", str(reordered)]
    run(command)
    commands.append(command)
    multiple = destination / "multi-video.mp4"
    command = common + ["-i", str(ordinary), "-map", "0:0", "-map", "0:0", "-map", "0:1", "-map", "0:2",
                        "-map", "0:4", "-map_metadata", "0", "-map_chapters", "0", "-c", "copy", "-f", "mp4", str(multiple)]
    run(command)
    commands.append(command)
    priming = destination / "priming-bframes.mp4"
    command = common + ["-i", str(source), "-f", "lavfi", "-i", "sine=frequency=660:sample_rate=48000:duration=2",
                        "-map", "0:v:0", "-map", "1:a:0", "-map", "0:s:0", "-c:v", "copy", "-c:a", "aac",
                        "-b:a", "48k", "-c:s", "mov_text", "-map_metadata", "-1", "-map_chapters", "-1",
                        "-metadata:s:a:0", "language=eng", "-metadata:s:s:0", "language=rus", "-f", "mp4", str(priming)]
    run(command)
    commands.append(command)
    fragmented = destination / "fragmented.mp4"
    command = common + ["-i", str(ordinary), "-map", "0:0", "-map", "0:1", "-c", "copy", "-map_chapters", "-1",
                        "-movflags", "frag_keyframe+empty_moov", "-f", "mp4", str(fragmented)]
    run(command)
    commands.append(command)
    normalized = [[value.replace(str(destination), "<fixture-directory>").replace(str(source), "<original-h264-aac-mkv>")
                   .replace(str(executable), "<pinned-ffmpeg>") for value in command] for command in commands]
    multiple_chapter_tracks = destination / "multiple-qt-chapters.mp4"
    repeated_chapter_reference = destination / "repeated-qt-reference.mp4"
    chapter_variants = [write_chapter_reference_fixture(ordinary, multiple_chapter_tracks, distinct=True),
                        write_chapter_reference_fixture(ordinary, repeated_chapter_reference, distinct=False)]
    provenance = {
        "origin": "Original MovieSift synthetic H.264, distinguishable AAC tones, two captions, artwork and chapters.",
        "source": source.name, "sourceSha256": source_hash,
        "ffmpegVersionAndConfiguration": version,
        "ffmpegExecutableSha256": digest(executable),
        "commands": normalized,
        "chapterReferenceVariants": chapter_variants,
        "files": [{"path": path.name, "bytes": path.stat().st_size, "sha256": digest(path)}
                  for path in [ordinary, reordered, multiple, fragmented, priming, jpeg_artwork, cover, jpeg_cover, subtitles, chapters,
                               multiple_chapter_tracks, repeated_chapter_reference]],
        "scope": "Development-only encoding of mov_text and generated artwork; production always stream-copies.",
    }
    (destination / "mp4-provenance.json").write_text(json.dumps(provenance, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    # Console encodings on Windows runners may not support original Unicode chapter titles.
    print(json.dumps(provenance, ensure_ascii=True, indent=2))


if __name__ == "__main__":
    main()
