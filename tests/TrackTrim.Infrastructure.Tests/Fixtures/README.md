These text fixtures were captured from MKVToolNix 102.0 against `ordinary.mkv` produced by `build/generate-fixtures.py`. The synthetic media, chapter text, tags, and attachment are original project test data. No external media was downloaded.

Only the identification JSON's `file_name` was replaced with `ordinary.mkv` and JSON indentation was normalized; track IDs, UIDs, metadata, and format version are the actual captured values. XML is the complete `mkvextract` output, including its commented DTD declaration. The application also ignores actual DTD declarations without resolving them.

Regenerate the media first, then run `mkvmerge -J ordinary.mkv` and `mkvextract ordinary.mkv chapters ordinary.chapters.xml tags ordinary.tags.xml`. The integration suite repeats real extraction and compares retained encoded stream bytes, semantic XML, attachment bytes, and source hashes.
