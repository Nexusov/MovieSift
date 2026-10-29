# Test fixture provenance

All media content is original synthetic project test data: color frames, distinct sine tones, subtitles, chapter text, tags and a text attachment. No external movie, soundtrack or subtitle content was downloaded. Test media and fixture generators are excluded from production archives. The pinned FFmpeg/ffprobe pair now ships for MP4 stream copying; fixture encoding remains a development-only operation.

## Generated MKVToolNix fixtures and captured text

The captured text fixtures came from MKVToolNix 102.0 against `ordinary.mkv` produced by `build/generate-fixtures.py`.

Only the identification JSON's `file_name` was replaced with `ordinary.mkv` and JSON indentation was normalized; track IDs, UIDs, metadata, and format version are the actual captured values. XML is the complete `mkvextract` output, including its commented DTD declaration. The application also ignores actual DTD declarations without resolving them.

Regenerate the media first, then run `mkvmerge -J ordinary.mkv` and `mkvextract ordinary.mkv chapters ordinary.chapters.xml tags ordinary.tags.xml`. The standard-library Python generator needs only the pinned MKVToolNix distribution. It creates ordinary/multiple-video VP8/PCM/SRT cases plus a longer-frame-count cancellation input and writes SHA256 provenance.

The integration suite repeats real extraction and compares retained encoded stream bytes, semantic XML, attachment bytes and source hashes. Retained SRT is compared directly, including the zero-audio case. The cancellation assertion requires an observed live worker at cancellation and awaits that exact retained process handle.

## Direct FFmpeg-origin H264/AAC/SRT inputs

These checked-in samples contain one H264 video, two distinguishable AAC-LC sine soundtracks with `ger`/`fre` bibliographic language metadata, and one SRT track. They are untouched direct FFmpeg Matroska output: no MKVToolNix normalization/remux was applied to the input. The positive 7.1.1 cases select either AAC soundtrack, retain SRT, compare complete extracted payload hashes, check original source hashes and run production structural/timing verification. The 9.0.2 cases are deliberately negative: a known final AAC duration changes, so verification must reject the candidate. A separate shifted-AAC candidate from the positive fixture must also fail timing verification.

| Input | Development encoder | Bytes | SHA256 | Regression trigger |
| --- | --- | --- | --- | --- |
| [h264-aac-aliases.mkv](h264-aac-aliases.mkv) | FFmpeg 9.0.2 | 20,273 | `1eb5398a1546a890fed34a30678475d161fecf40a2d58a3804ea7554a4774237` | Negative case: bibliographic aliases remain comparable, but a known 16 ms final AAC duration becomes about 21.333 ms and must fail verification. |
| [h264-aac-no-default-duration.mkv](h264-aac-no-default-duration.mkv) | FFmpeg 7.1.1 | 20,357 | `b0b5fc3c0e290fb435be3cc09a1952ccfc343d93c2d6d13e9e8332e55b791000` | Positive case: source AAC omits DefaultDuration; pinned 102.0 adds the equivalent 1024-sample duration, with only the matching unknown synthetic end-boundary exception. |

The adjacent [9.0.2 provenance](h264-aac-aliases.provenance.json) and [7.1.1 provenance](h264-aac-no-default-duration.provenance.json) contain executable/version/configuration, development archive URL/hash and exact command. The older 7.1.1 archive hash was computed from its HTTPS release asset; an independent publisher checksum was unavailable. This is recorded as a provenance limit, not an independently verified publisher checksum.

Separate generated real-tool chapter/tag fixtures keep `ger`/`fre` legacy fields while intentionally omitting IETF fields at creation. The ordinary pinned remux adds equivalent chapter `de`/`fr` IETF fields and leaves tag IETF fields absent; raw chapter/tag legacy fields are retained. Equivalent tag-field addition is checked in its own real metadata case, rather than assuming every remux emits it. Both use the same explicit alias table as track languages and are independent of the negative 9.0.2 timing fixture.

Use `build/generate-ffmpeg-fixture.py` with an explicitly provided matching encoder to regenerate a sample and its JSON provenance. These MKV regressions read the checked-in inputs. Exact output hashes may depend on the recorded encoder build; inspect any fixture/provenance change independently.

## Generated DTS fixtures

`build/generate-fixtures.py` also creates original VP8/DTS/PCM fixtures through `build/dts_fixture.py`. Two embedded original DTS core frames contain six synthetic sine tones; their encoder command, development tool/archive hashes and frame hashes are recorded in the helper. CI needs only Python and pinned MKVToolNix, not an additional encoder. Generated provenance records the exact repeated stream and container hashes.

The fixtures replace optional DTS `DefaultDuration` and `BitDepth` elements and affected CRCs with equal-length EBML Void; encoded payloads and offsets remain unchanged. One fixture disables lacing, preserving explicit frame times. The other keeps fixed lacing and exercises codec-derived times inside each real source block. Copy and replacement cases compare retained video/DTS bytes, metadata, source hashes and owned cleanup; a shifted-candidate negative control must still fail. No user media is included.

## Generated MP4 fixtures

`build/generate-mp4-fixtures.py` verifies the original 7.1.1 input hash above, copies its encoded H.264/AAC, and creates small original mov_text captions, two chapters and PNG/JPEG covers using the pinned bundled FFmpeg. A separate native AAC encoding exercises priming with B-frame video. Ordinary, reordered, multiple-video, priming and deliberately fragmented files are generated under `artifacts/fixtures/mp4`; `mp4-provenance.json` records every command, actual tool version/configuration and complete file SHA256. They are required CI inputs, not shipped application media.

Production Preview/Apply derives independent language/title/forced selections for the reordered files before approval and mixed MKV/MP4 queue execution. Real tests compare retained encoded packet payloads, exact rational packet timing/skip information, supported metadata/chapters/artwork, complete source hashes and owned cleanup. Controlled box/probe documents separately test rejected structures and malformed output; they are not real-media acceptance.
