"""Validate complete data-only UI packs and optionally regenerate their exact-byte catalog."""

import argparse
from collections import Counter
import hashlib
import json
from pathlib import Path
import re
import xml.etree.ElementTree as ElementTree

PACK_VERSION = 1
MAX_PACK_BYTES = 256 * 1024
MAX_VALUE_CHARACTERS = 8192
LOCALES = ("ru", "de", "es", "fr", "pt-BR", "uk", "pl", "tr", "ja", "ko", "zh-Hans", "sv", "da", "cs")
PLACEHOLDERS = re.compile(r"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]*)?\}(?!\})")


def has_terminal_period(value):
    """Keep short UI copy free of final full stops while preserving intentional loading ellipses."""
    trimmed = value.rstrip()
    return trimmed.endswith((".", "。", "．")) and not trimmed.endswith("...")


def reject_duplicates(pairs):
    """Reject duplicate JSON properties rather than accepting an ambiguous last value."""
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON property: {key}")
        result[key] = value
    return result


def load_contract(repository):
    """Read the canonical English resource keys and format placeholders."""
    resource = repository / "src/MovieSift.Desktop/Localization/Strings.resx"
    entries = ElementTree.parse(resource).getroot().findall("data")
    messages = {entry.attrib["name"]: entry.findtext("value") or "" for entry in entries}
    if len(messages) != len(entries):
        raise ValueError("Duplicate English resource key")
    if any(has_terminal_period(value) for value in messages.values()):
        raise ValueError("English UI text contains a terminal period")
    return messages


def validate_pack(request):
    """Check one complete pack against its filename, schema, bounds and resource contract."""
    path, locale, contract = request
    payload = path.read_bytes()
    if b"\r" in payload:
        raise ValueError(f"{locale}: translation sources must use LF line endings before catalog hashing")
    if not payload or len(payload) > MAX_PACK_BYTES:
        raise ValueError(f"{locale}: pack byte bound exceeded")
    data = json.loads(payload.decode("utf-8"), object_pairs_hook=reject_duplicates)
    if set(data) != {"version", "locale", "messages"}:
        raise ValueError(f"{locale}: invalid pack properties")
    if type(data["version"]) is not int or data["version"] != PACK_VERSION or data["locale"] != locale:
        raise ValueError(f"{locale}: invalid version or locale")
    messages = data["messages"]
    if not isinstance(messages, dict) or set(messages) != set(contract):
        missing = sorted(set(contract) - set(messages)) if isinstance(messages, dict) else list(contract)
        extra = sorted(set(messages) - set(contract)) if isinstance(messages, dict) else []
        raise ValueError(f"{locale}: resource mismatch, missing={missing}, extra={extra}")
    for key, value in messages.items():
        if not isinstance(value, str) or not value.strip() or len(value) > MAX_VALUE_CHARACTERS:
            raise ValueError(f"{locale}/{key}: empty, invalid or excessive text")
        if has_terminal_period(value):
            raise ValueError(f"{locale}/{key}: terminal period in UI text")
        if Counter(PLACEHOLDERS.findall(value)) != Counter(PLACEHOLDERS.findall(contract[key])):
            raise ValueError(f"{locale}/{key}: format placeholder mismatch")
    return {"locale": locale, "file": path.name, "sha256": hashlib.sha256(payload).hexdigest(), "bytes": len(payload)}


def main():
    """Fail visibly on invalid packs, missing release languages or a stale hash catalog."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write-catalog", action="store_true", help="Validate every locale before replacing catalog.json")
    parser.add_argument("--allow-incomplete", action="store_true", help="Development only: validate existing packs without requiring all languages")
    options = parser.parse_args()
    if options.write_catalog and options.allow_incomplete:
        parser.error("A published catalog must contain all supported languages")
    directory = Path(__file__).resolve().parent
    contract = load_contract(directory.parent)
    records = []
    for locale in LOCALES:
        path = directory / f"{locale}.json"
        if options.allow_incomplete and not path.exists():
            continue
        records.append(validate_pack((path, locale, contract)))
    expected = {"version": PACK_VERSION, "packs": records}
    catalog = directory / "catalog.json"
    if options.write_catalog:
        catalog.write_bytes((json.dumps(expected, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
    if not options.allow_incomplete:
        actual = json.loads(catalog.read_text(encoding="utf-8"), object_pairs_hook=reject_duplicates)
        if actual != expected:
            raise ValueError("Translation catalog differs from validated pack hashes/sizes; regenerate it explicitly")
    print(f"Validated {len(records)} translation packs with {len(contract)} messages each.")


if __name__ == "__main__":
    main()
