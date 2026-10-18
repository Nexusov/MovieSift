#!/usr/bin/env python3
"""Generate original deterministic synthetic media with Python's standard library and pinned MKVToolNix."""

import argparse
import base64
import hashlib
import json
import math
import pathlib
import struct
import subprocess
import sys
import wave

FRAME_COUNT = 20
CANCELLATION_FRAME_COUNT = 60000
FRAME_RATE = 10
SAMPLE_RATE = 48000
DURATION_SECONDS = 2
AMPLITUDE = 6000
# Original 16x16 solid-color VP8 keyframes, encoded for this project using Pillow 12.0.0/libwebp 1.6.0.
# Repeating independent keyframes needs no additional encoder, FFmpeg, or downloaded media.
VP8_RED = "MAIAnQEqEAAQAADAEiWgAnS6AfgB+AAEaAAA/vohl/93mrDTd/Wt//Wjn66J/rRz/1lYAA=="
VP8_BLUE = "8AEAnQEqEAAQAADAEiWgAnS6AfgABEwAAP7+ZqH/+tHP10T/Wjn/rRz/5SqLVNuKAAA="


def write_video(request):
    """Write a valid IVF stream containing independently decodable VP8 keyframes."""
    path, encoded_frame, frame_count = request
    payload = base64.b64decode(encoded_frame)
    header = struct.pack("<4sHH4sHHIIII", b"DKIF", 0, 32, b"VP80", 16, 16, FRAME_RATE, 1, frame_count, 0)
    with path.open("wb") as output:
        output.write(header)
        for frame_index in range(frame_count):
            output.write(struct.pack("<IQ", len(payload), frame_index))
            output.write(payload)


def write_audio(request):
    """Write distinguishable mono PCM audio without an external codec dependency."""
    path, frequency = request
    with wave.open(str(path), "wb") as output:
        output.setnchannels(1)
        output.setsampwidth(2)
        output.setframerate(SAMPLE_RATE)
        samples = (round(AMPLITUDE * math.sin(2 * math.pi * frequency * sample / SAMPLE_RATE))
                   for sample in range(SAMPLE_RATE * DURATION_SECONDS))
        output.writeframes(b"".join(struct.pack("<h", sample) for sample in samples))


def run(arguments):
    """Require successful pinned real-tool execution and retain actual diagnostics on failure."""
    result = subprocess.run(arguments, capture_output=True, text=True, encoding="utf-8", check=False)
    if result.returncode != 0:
        raise RuntimeError(f"Tool exit {result.returncode}: {result.stdout}\n{result.stderr}")
    return result.stdout


def main():
    """Generate small fixtures with explicit track UIDs, flags, chapters, tags, and an attachment."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tool-directory", required=True, type=pathlib.Path)
    parser.add_argument("--destination", required=True, type=pathlib.Path)
    arguments = parser.parse_args()
    destination = arguments.destination.resolve()
    destination.mkdir(parents=True, exist_ok=True)
    executable = arguments.tool_directory.resolve() / ("mkvmerge.exe" if sys.platform == "win32" else "mkvmerge")
    version = run([str(executable), "--version"]).strip()
    if not version.startswith("mkvmerge v102.0 "):
        raise RuntimeError(f"Expected pinned MKVToolNix 102.0, received {version}")
    write_video((destination / "red.ivf", VP8_RED, FRAME_COUNT))
    write_video((destination / "blue.ivf", VP8_BLUE, FRAME_COUNT))
    write_audio((destination / "low.wav", 440))
    write_audio((destination / "high.wav", 880))
    (destination / "captions.srt").write_text("1\n00:00:00,000 --> 00:00:01,700\nOriginal synthetic subtitle.\n", encoding="utf-8")
    (destination / "attachment.txt").write_text("Original TrackTrim fixture attachment.\n", encoding="utf-8")
    (destination / "chapters.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?><Chapters><EditionEntry><EditionUID>3001</EditionUID>'
        '<EditionFlagHidden>0</EditionFlagHidden><EditionFlagDefault>1</EditionFlagDefault><EditionFlagOrdered>0</EditionFlagOrdered>'
        '<ChapterAtom><ChapterUID>4001</ChapterUID><ChapterTimeStart>00:00:00.000000000</ChapterTimeStart>'
        '<ChapterTimeEnd>00:00:01.000000000</ChapterTimeEnd><ChapterFlagHidden>0</ChapterFlagHidden>'
        '<ChapterFlagEnabled>1</ChapterFlagEnabled><ChapterDisplay><ChapterString>Opening / Начало</ChapterString>'
        '<ChapterLanguage>eng</ChapterLanguage></ChapterDisplay></ChapterAtom>'
        '<ChapterAtom><ChapterUID>4002</ChapterUID><ChapterTimeStart>00:00:01.000000000</ChapterTimeStart>'
        '<ChapterFlagHidden>0</ChapterFlagHidden><ChapterFlagEnabled>1</ChapterFlagEnabled>'
        '<ChapterDisplay><ChapterString>Closing</ChapterString><ChapterLanguage>eng</ChapterLanguage>'
        '</ChapterDisplay></ChapterAtom></EditionEntry></Chapters>', encoding="utf-8")
    (destination / "tags.xml").write_text(
        '<?xml version="1.0" encoding="UTF-8"?><Tags>'
        '<Tag><Targets/><Simple><Name>COMMENT</Name><String>Original global fixture tag</String></Simple></Tag>'
        '<Tag><Targets><TrackUID>1002</TrackUID></Targets><Simple><Name>COMMENT</Name><String>Low tone</String></Simple></Tag>'
        '<Tag><Targets><TrackUID>1003</TrackUID></Targets><Simple><Name>COMMENT</Name><String>High tone</String></Simple></Tag>'
        '</Tags>', encoding="utf-8")
    for multiple_video in (False, True):
        output = destination / ("multi-video.mkv" if multiple_video else "ordinary.mkv")
        command = [str(executable), "--deterministic", "TrackTrim-fixture-v1", "--disable-track-statistics-tags",
                   "--normalize-language-ietf", "off", "--output", str(output), "--title", "TrackTrim synthetic / Проверка",
                   "--chapters", str(destination / "chapters.xml"),
                   "--attachment-name", "../untrusted attachment.txt", "--attachment-mime-type", "text/plain",
                   "--attachment-description", "Original attachment description / Описание",
                   "--attach-file", str(destination / "attachment.txt"),
                   "--language", "0:und", str(destination / "red.ivf")]
        if multiple_video:
            command += ["--language", "0:und", str(destination / "blue.ivf")]
        command += ["--language", "0:eng", "--track-name", "0:Matching audio",
                    "--default-track-flag", "0:1", str(destination / "low.wav"),
                    "--language", "0:eng", "--track-name", "0:Matching audio",
                    "--default-track-flag", "0:0", "--commentary-flag", "0:1", str(destination / "high.wav"),
                    "--language", "0:rus", "--track-name", "0:Captions",
                    "--default-track-flag", "0:0", "--forced-display-flag", "0:1", str(destination / "captions.srt")]
        preliminary = output.with_suffix(".preliminary.mkv")
        command[command.index("--output") + 1] = str(preliminary)
        run(command)
        preliminary_identification = json.loads(run([str(executable), "-J", str(preliminary)]))
        audio_tracks = [track for track in preliminary_identification["tracks"] if track["type"] == "audio"]
        tags = (destination / "tags.xml").read_text(encoding="utf-8")
        for placeholder, track in zip((1002, 1003), audio_tracks):
            tags = tags.replace(f"<TrackUID>{placeholder}</TrackUID>", f'<TrackUID>{track["properties"]["uid"]}</TrackUID>')
        actual_tags = destination / f"{output.stem}.tags.xml"
        actual_tags.write_text(tags, encoding="utf-8")
        run([str(executable), "--deterministic", "TrackTrim-fixture-tags-v1", "--disable-track-statistics-tags",
             "--normalize-language-ietf", "off", "--output", str(output), "--global-tags", str(actual_tags), str(preliminary)])
        preliminary.unlink()
        identification = run([str(executable), "-J", str(output)])
        (destination / f"{output.stem}.identification.json").write_text(identification, encoding="utf-8")
    cancellation_video = destination / "cancellation.ivf"
    write_video((cancellation_video, VP8_RED, CANCELLATION_FRAME_COUNT))
    run([str(executable), "--deterministic", "TrackTrim-live-cancellation-v1", "--disable-track-statistics-tags",
         "--output", str(destination / "cancellation.mkv"), str(cancellation_video),
         str(destination / "low.wav"), str(destination / "high.wav"), str(destination / "captions.srt")])
    manifest = {"generator": "TrackTrim original synthetic fixture v1", "tool": version,
                "files": {path.name: hashlib.sha256(path.read_bytes()).hexdigest()
                          for path in sorted(destination.glob("*.mkv"))}}
    (destination / "provenance.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
