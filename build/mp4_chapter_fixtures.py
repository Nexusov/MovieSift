"""Derive narrowly controlled QuickTime chapter-reference variants from the pinned original fixture."""

import hashlib
from pathlib import Path
import struct


def fixture_boxes(data, start, end):
    """Visit the original small fixture's finite 32-bit boxes; this is not a production container reader."""
    position = start
    while position < end:
        size, kind = struct.unpack_from(">I4s", data, position)
        if size < 8 or position + size > end:
            raise ValueError("The controlled fixture must use complete finite 32-bit boxes.")
        yield kind, position, position + size
        position += size


def child(data, parent, kind):
    """Require exactly the expected child in the pinned fixture recipe."""
    matches = [entry for entry in fixture_boxes(data, parent[1] + 8, parent[2]) if entry[0] == kind]
    if len(matches) != 1:
        raise ValueError(f"Expected one {kind!r} child in the controlled fixture.")
    return matches[0]


def boxed(kind, payload):
    """Recompute the size of one small modified fixture box."""
    return struct.pack(">I4s", len(payload) + 8, kind) + payload


def raw(data, entry):
    """Copy an unchanged fixture box byte-for-byte."""
    return data[entry[1]:entry[2]]


def write_chapter_reference_fixture(source: Path, destination: Path, distinct: bool):
    """Add a repeated or a distinct referenced two-entry QT chapter track, removing the Nero fallback."""
    original = source.read_bytes()
    roots = list(fixture_boxes(original, 0, len(original)))
    movie = next(entry for entry in roots if entry[0] == b"moov")
    if movie[2] != len(original):
        raise ValueError("The controlled source must have its moov last, after all original media.")
    movie_children = list(fixture_boxes(original, movie[1] + 8, movie[2]))
    tracks = [entry for entry in movie_children if entry[0] == b"trak"]
    chapter_tracks = []
    identifiers = []
    for track in tracks:
        header = child(original, track, b"tkhd")
        if original[header[1] + 8] != 0:
            raise ValueError("The controlled source must use version-zero track headers.")
        identifiers.append(struct.unpack_from(">I", original, header[1] + 20)[0])
        table = child(original, child(original, child(original, track, b"mdia"), b"minf"), b"stbl")
        description = child(original, table, b"stsd")
        if original[description[1] + 20:description[1] + 24] == b"text":
            chapter_tracks.append(track)
    if len(chapter_tracks) != 1:
        raise ValueError("The original fixture must have one QT text chapter track.")
    chapter_track = chapter_tracks[0]
    chapter_header = child(original, chapter_track, b"tkhd")
    original_identifier = struct.unpack_from(">I", original, chapter_header[1] + 20)[0]
    target_identifier = max(identifiers) + 1 if distinct else original_identifier

    referencing_track = next(track for track in reversed(tracks)
                             if any(entry[0] == b"tref" for entry in fixture_boxes(original, track[1] + 8, track[2])))
    references = child(original, referencing_track, b"tref")
    chapter_reference = child(original, references, b"chap")
    new_reference = boxed(b"chap", original[chapter_reference[1] + 8:chapter_reference[2]] + struct.pack(">I", target_identifier))
    new_references = boxed(b"tref", b"".join(new_reference if entry == chapter_reference else raw(original, entry)
                                               for entry in fixture_boxes(original, references[1] + 8, references[2])))
    new_referencing_track = boxed(b"trak", b"".join(new_references if entry == references else raw(original, entry)
                                              for entry in fixture_boxes(original, referencing_track[1] + 8, referencing_track[2])))
    movie_parts = []
    for entry in movie_children:
        if entry == referencing_track:
            movie_parts.append(new_referencing_track)
            continue
        if entry[0] == b"udta":
            movie_parts.append(boxed(b"udta", b"".join(raw(original, value)
                for value in fixture_boxes(original, entry[1] + 8, entry[2]) if value[0] != b"chpl")))
            continue
        if distinct and entry[0] == b"mvhd":
            header = bytearray(raw(original, entry))
            struct.pack_into(">I", header, len(header) - 4, target_identifier + 1)
            movie_parts.append(bytes(header))
            continue
        movie_parts.append(raw(original, entry))

    alternate_titles = ["Other chapter", "Another chapter variant"]
    original_titles = []
    appended_payload = b""
    if distinct:
        clone = bytearray(raw(original, chapter_track))
        clone_root = (b"trak", 0, len(clone))
        cloned_header = child(clone, clone_root, b"tkhd")
        struct.pack_into(">I", clone, cloned_header[1] + 20, target_identifier)
        table = child(clone, child(clone, child(clone, clone_root, b"mdia"), b"minf"), b"stbl")
        chunks = child(clone, table, b"stco")
        sizes = child(clone, table, b"stsz")
        if struct.unpack_from(">I", clone, chunks[1] + 12)[0] != 1 or struct.unpack_from(">II", clone, sizes[1] + 12) != (0, 2):
            raise ValueError("The controlled chapter track must have one chunk and two explicit sample sizes.")
        chunk_offset = struct.unpack_from(">I", clone, chunks[1] + 16)[0]
        sample_sizes = struct.unpack_from(">II", clone, sizes[1] + 20)
        payload = bytearray(original[chunk_offset:chunk_offset + sum(sample_sizes)])
        cursor = 0
        for sample_size, title in zip(sample_sizes, alternate_titles, strict=True):
            title_bytes = title.encode("utf-8")
            title_length = struct.unpack_from(">H", payload, cursor)[0]
            if title_length != len(title_bytes):
                raise ValueError("Alternate chapter text must preserve the original sample sizes.")
            original_titles.append(bytes(payload[cursor + 2:cursor + 2 + title_length]).decode("utf-8"))
            payload[cursor + 2:cursor + 2 + title_length] = title_bytes
            cursor += sample_size
        appended_payload = bytes(payload)
        # The original moov is last; unchanged sample offsets stay valid. Only the cloned chapter chunk is appended.
        new_movie_length = 8 + sum(len(part) for part in movie_parts) + len(clone)
        struct.pack_into(">I", clone, chunks[1] + 16, movie[1] + new_movie_length + 8)
        movie_parts.append(bytes(clone))

    result = original[:movie[1]] + boxed(b"moov", b"".join(movie_parts))
    if appended_payload:
        result += boxed(b"mdat", appended_payload)
    destination.write_bytes(result)
    return {
        "recipe": "Controlled QT chapter-reference variant; original media bytes unchanged; Nero chpl removed.",
        "source": source.name,
        "sourceSha256": hashlib.sha256(original).hexdigest(),
        "path": destination.name,
        "sha256": hashlib.sha256(result).hexdigest(),
        "chapterTrackIds": [original_identifier, target_identifier] if distinct else [original_identifier],
        "chaptersPerTrack": 2,
        "originalTitles": original_titles,
        "alternateTitles": alternate_titles if distinct else [],
        "nerochpl": False,
    }
