"""Generate original DTS preservation evidence with no additional encoder required in CI."""

import base64
import hashlib
import json
from pathlib import Path
import subprocess
import sys

# Original six-channel sine tones, encoded once using the exact development tool below.
# Repeating two independent DTS core frames keeps this test fixture small and reproducible.
DTS_FRAMES_BASE64 = ('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')
DTS_ENCODING_PROVENANCE = {'origin': 'Original mathematical sine tones for MovieSift; no user media or third-party audio.',
 'channelsHz': [440, 550, 660, 770, 880, 990],
 'sampleRate': 48000,
 'durationSeconds': 2,
 'developmentToolVersionAndConfiguration': ['ffmpeg version 9.0.2-essentials_build-www.gyan.dev Copyright '
                                            '(c) 2000-2026 the FFmpeg developers',
                                            'built with gcc 16.2.0 (Rev3, Built by MSYS2 project)',
                                            'configuration: --enable-gpl --enable-version3 --enable-static '
                                            '--disable-w32threads --disable-autodetect --enable-cairo '
                                            '--enable-fontconfig --enable-iconv --enable-gnutls '
                                            '--enable-libxml2 --enable-gmp --enable-bzlib --enable-lzma '
                                            '--enable-zlib --enable-libsrt --enable-libssh --enable-libzmq '
                                            '--enable-avisynth --enable-sdl2 --enable-libwebp '
                                            '--enable-libx264 --enable-libx265 --enable-libxvid '
                                            '--enable-libaom --enable-libopenjpeg --enable-libvpx '
                                            '--enable-mediafoundation --enable-libass --enable-libfreetype '
                                            '--enable-libfribidi --enable-libharfbuzz --enable-libvidstab '
                                            '--enable-libvmaf --enable-libzimg --enable-amf '
                                            '--enable-cuda-llvm --enable-cuvid --enable-dxva2 '
                                            '--enable-d3d11va --enable-d3d12va --enable-ffnvcodec '
                                            '--enable-libvpl --enable-nvdec --enable-nvenc --enable-vaapi '
                                            '--enable-openal --enable-libgme --enable-libopenmpt '
                                            '--enable-libopencore-amrwb --enable-libmp3lame '
                                            '--enable-libtheora --enable-libvo-amrwbenc --enable-libgsm '
                                            '--enable-libopencore-amrnb --enable-libopus --enable-libspeex '
                                            '--enable-libvorbis --enable-librubberband',
                                            'libavutil      61.  1.102 / 61.  1.102',
                                            'libavcodec     63.  1.102 / 63.  1.102',
                                            'libavformat    63.  1.102 / 63.  1.102',
                                            'libavdevice    63.  1.102 / 63.  1.102',
                                            'libavfilter    12.  1.102 / 12.  1.102',
                                            'libswscale     10.  1.102 / 10.  1.102',
                                            'libswresample   7.  1.102 /  7.  1.102',
                                            '',
                                            'Exiting with exit code 0'],
 'developmentExecutableSha256': '3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec',
 'developmentArchiveUrl': 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip',
 'developmentArchiveSha256': '60f467265b1e312373dbcd92200c2618a74850f98d3d078e94296bb3fa2047ba',
 'command': ['<pinned-development-ffmpeg>',
             '-hide_banner',
             '-loglevel',
             'error',
             '-nostdin',
             '-f',
             'lavfi',
             '-i',
             'aevalsrc=0.1*sin(2*PI*440*t)|0.1*sin(2*PI*550*t)|0.1*sin(2*PI*660*t)|0.1*sin(2*PI*770*t)|0.1*sin(2*PI*880*t)|0.1*sin(2*PI*990*t):s=48000:d=2:c=5.1',
             '-map',
             '0:a:0',
             '-c:a',
             'dca',
             '-strict',
             '-2',
             '-b:a',
             '1536k',
             '-fflags',
             '+bitexact',
             '-flags:a',
             '+bitexact',
             '-f',
             'dts',
             'original-six-channel.dts'],
 'streamBytes': 385024,
 'streamSha256': 'd4a15a8c0c5e1a88d4635addcd8ec2ed0ad33b5a99ed6cfe6d37695e1d916d1f',
 'frames': [{'index': 0,
             'offset': 0,
             'length': 2048,
             'sha256': '3176b2b819f3e921b5c7faa6bb92904f3748725aa1a93997c5d5ce5804f4bcb0'},
            {'index': 1,
             'offset': 2048,
             'length': 2048,
             'sha256': '4828cdff28314d5f3d1f40e4dbe277f15d8e482ee873848ffd0c84de4e03eaeb'}]}
FRAME_REPETITIONS = 94
TRACKS_ID = 0x1654AE6B
SEGMENT_ID = 0x18538067
TRACK_ENTRY_ID = 0xAE
AUDIO_ID = 0xE1
CODEC_ID = 0x86
DEFAULT_DURATION_ID = 0x23E383
BIT_DEPTH_ID = 0x6264
CRC_ID = 0xBF
CLUSTER_ID = 0x1F43B675
SIMPLE_BLOCK_ID = 0xA3
ORIGINAL_DTS_TRACK_NUMBER = 3
OVERLAP_SHIFT_MILLISECONDS = 32


def _elements(request):
    """Walk only bounded EBML headers in the original fixture without searching encoded payload bytes."""
    payload, start, end = request
    while start < end:
        identifier_width = next((width for width in range(1, 5) if payload[start] & (0x80 >> (width - 1))), None)
        if identifier_width is None or start + identifier_width >= end:
            raise ValueError("Invalid synthetic fixture element identifier")
        identifier = int.from_bytes(payload[start:start + identifier_width], "big")
        size_start = start + identifier_width
        size_width = next((width for width in range(1, 9) if payload[size_start] & (0x80 >> (width - 1))), None)
        if size_width is None or size_start + size_width > end:
            raise ValueError("Invalid synthetic fixture element size")
        size = int.from_bytes(payload[size_start:size_start + size_width], "big") & ((1 << (7 * size_width)) - 1)
        content = size_start + size_width
        element_end = end if size == (1 << (7 * size_width)) - 1 else content + size
        if element_end > end:
            raise ValueError("Synthetic fixture element exceeds its parent")
        yield identifier, start, content, element_end
        start = element_end


def _void(request):
    """Replace a tiny optional element with equal-length Void, preserving every seek/cue offset."""
    payload, start, end = request
    count = end - start
    if not 2 <= count < 129:
        raise ValueError("Only tiny synthetic fixture properties may be replaced")
    payload[start:end] = bytes((0xEC, 0x80 | (count - 2))) + bytes(count - 2)


def _omit_optional_dts_fields(path):
    """Model legacy Matroska headers while keeping valid DTS payload and all non-DTS track headers intact."""
    payload = bytearray(path.read_bytes())
    removed = []
    for identifier, _, content, end in _elements((payload, 0, len(payload))):
        if identifier != SEGMENT_ID:
            continue
        for child, _, child_content, child_end in _elements((payload, content, end)):
            if child != TRACKS_ID:
                continue
            for entry, entry_start, entry_content, entry_end in _elements((payload, child_content, child_end)):
                if entry == CRC_ID:
                    _void((payload, entry_start, entry_end))
                if entry != TRACK_ENTRY_ID:
                    continue
                fields = list(_elements((payload, entry_content, entry_end)))
                dts = any(field == CODEC_ID and bytes(payload[value:limit]) == b"A_DTS" for field, _, value, limit in fields)
                if not dts:
                    continue
                for field, field_start, value, limit in fields:
                    if field in (DEFAULT_DURATION_ID, CRC_ID):
                        _void((payload, field_start, limit))
                        removed.append(hex(field))
                    if field != AUDIO_ID:
                        continue
                    for nested, nested_start, _, nested_end in list(_elements((payload, value, limit))):
                        if nested in (BIT_DEPTH_ID, CRC_ID):
                            _void((payload, nested_start, nested_end))
                            removed.append(hex(nested))
    if hex(DEFAULT_DURATION_ID) not in removed or hex(BIT_DEPTH_ID) not in removed:
        raise ValueError("The pinned muxer did not supply the expected original DTS fields")
    path.write_bytes(payload)
    return removed


def create_fixture(request):
    """Mux original VP8/DTS/PCM, then omit only optional DTS headers to exercise payload-derived metadata."""
    tool_directory, destination = (Path(value).resolve() for value in request)
    merge = tool_directory / ("mkvmerge.exe" if sys.platform == "win32" else "mkvmerge")
    frames = [base64.b64decode(value, validate=True) for value in DTS_FRAMES_BASE64]
    if len(frames) != len(DTS_ENCODING_PROVENANCE["frames"]):
        raise ValueError("Original encoded DTS fixture frame count changed")
    for frame, expected in zip(frames, DTS_ENCODING_PROVENANCE["frames"]):
        if hashlib.sha256(frame).hexdigest() != expected["sha256"]:
            raise ValueError("Original encoded DTS fixture frame changed")
    raw = destination / "original-derived-properties.dts"
    raw.write_bytes(b"".join(frames) * FRAME_REPETITIONS)
    outputs = [_mux_fixture((merge, raw, destination, laced)) for laced in (False, True)]
    _create_overlapping_lace_fixture(outputs[1])
    return outputs[0]


def _create_overlapping_lace_fixture(source):
    """Shift one original lace anchor without changing any media bytes, modeling overlapping codec-derived frame times."""
    payload = bytearray(source.read_bytes())
    blocks = 0
    changed = False
    for identifier, _, content, end in _elements((payload, 0, len(payload))):
        if identifier != SEGMENT_ID:
            continue
        for child, _, child_content, child_end in _elements((payload, content, end)):
            if child != CLUSTER_ID:
                continue
            for element, element_start, value, limit in _elements((payload, child_content, child_end)):
                if element == CRC_ID:
                    _void((payload, element_start, limit))
                if element != SIMPLE_BLOCK_ID or payload[value] != 0x80 | ORIGINAL_DTS_TRACK_NUMBER:
                    continue
                blocks += 1
                if blocks != 2:
                    continue
                if payload[value + 3] & 6 != 4 or payload[value + 4] < 1:
                    raise ValueError("The original overlap fixture requires a fixed DTS lace")
                relative = int.from_bytes(payload[value + 1:value + 3], "big", signed=True)
                shifted = relative - OVERLAP_SHIFT_MILLISECONDS
                if relative < OVERLAP_SHIFT_MILLISECONDS:
                    raise ValueError("The original second DTS lace must remain after the first block anchor")
                payload[value + 1:value + 3] = shifted.to_bytes(2, "big", signed=True)
                changed = True
    if not changed:
        raise ValueError("The original overlap fixture did not contain a second DTS SimpleBlock")
    output = source.with_name("dts-derived-properties-overlap.mkv")
    output.write_bytes(payload)
    provenance = json.loads(source.with_suffix(".provenance.json").read_text(encoding="utf-8"))
    provenance.update({"frameTimestamps": "Second DTS fixed-lace anchor shifted earlier by 32 ms; block anchors remain ordered while codec-derived frames overlap",
                       "overlapTransformation": "Only the second original DTS SimpleBlock signed relative timestamp changes; affected cluster CRC removed; encoded payloads unchanged",
                       "fixtureSha256": hashlib.sha256(payload).hexdigest(), "fixtureBytes": len(payload)})
    output.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")


def _mux_fixture(request):
    """Cover explicit frame timestamps and codec-derived timestamps within preserved Matroska laces."""
    merge, raw, destination, laced = request
    output = destination / ("dts-derived-properties-laced.mkv" if laced else "dts-derived-properties.mkv")
    lacing_arguments = [] if laced else ["--disable-lacing"]
    command = [str(merge), "--deterministic", "MovieSift-derived-DTS-v1", "--disable-track-statistics-tags", *lacing_arguments,
               "--track-order", "0:0,2:0,1:0", "-o", str(output), str(destination / "red.ivf"),
               "--language", "0:eng", "--track-name", "0:Original DTS sine tones", str(raw),
               "--language", "0:rus", str(destination / "high.wav")]
    completed = subprocess.run(command, capture_output=True, text=True, encoding="utf-8", check=False)
    if completed.returncode != 0:
        raise RuntimeError(f"Pinned synthetic DTS mux failed: {completed.stdout}\n{completed.stderr}")
    removed = _omit_optional_dts_fields(output)
    provenance = {"origin": "Original MovieSift VP8 keyframes, sine-tone DTS and PCM; no user media",
                  "encoding": DTS_ENCODING_PROVENANCE, "repetitions": FRAME_REPETITIONS,
                  "rawDtsBytes": raw.stat().st_size, "rawDtsSha256": hashlib.sha256(raw.read_bytes()).hexdigest(),
                  "trackOrder": "VP8, PCM, DTS; the portable workflow removes the first audio and must retain legacy DTS",
                  "frameTimestamps": "Preserved Matroska laces; codec-derived duration defines intra-lace frame timestamps" if laced else
                      "Lacing disabled so every original DTS frame has its own explicit timestamp after duration omission",
                  "omittedOptionalElements": removed,
                  "headerTransformation": "Equal-length EBML Void substitution; affected CRCs removed; payload and offsets unchanged",
                  "fixtureSha256": hashlib.sha256(output.read_bytes()).hexdigest(), "fixtureBytes": output.stat().st_size}
    output.with_suffix(".provenance.json").write_text(json.dumps(provenance, indent=2) + "\n", encoding="utf-8")
    return output
