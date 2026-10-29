# Downloadable interface translations

English remains the complete built-in resource contract in `src/MovieSift.Desktop/Localization/Strings.resx`. Other languages are data-only UTF-8 JSON packs. Packs change application labels only; media titles, language metadata, diagnostics and preset names remain original.

The catalog contains 14 downloadable languages, including Swedish, Danish and Czech. English remains the only application translation shipped in portable binaries. Active and installed states are distinct; installed packs do not display download progress when selected. Existing caches are synchronized once per new application build using the same exact-byte validation. Network failure never silently replaces invalid user data with an empty successful catalog.

Each pack declares schema `version: 1`, its exact `locale` and a complete `messages` dictionary. Locale filenames are fixed. `catalog.json` binds the exact bytes of every supported downloadable pack through its size and SHA256. The downloader resolves `main` through GitHub's small `git/ref/heads/main` response (bounded to 8 KiB), then obtains both catalog and pack from that immutable commit. It does not request commit diffs. This is an integrity/compatibility boundary, not an independently signed authenticity claim.

When a validated `main` snapshot has a different resource-key contract or lacks a supported language, the client may use the exact 40-character source commit embedded in its own build instead. The host and repository stay fixed, and that snapshot's catalog, byte counts, SHA256 and full contract are validated under the same 30-second deadline. Invalid JSON, schema, placeholders, hashes and transport failures do not bypass validation. This permits an identified review build to use its matching committed texts before integration; uncommitted local strings cannot be fetched from GitHub.

A 404 for that exact official immutable `lang/catalog.json` path also permits the built-source lookup, since older repository revisions use the former source folder. Missing translation-pack files, API failures and other HTTP errors do not permit it. Portable caches intentionally remain in `data/translations` so existing installations retain their downloads.

After changing English keys, update every translation and regenerate the catalog explicitly:

```powershell
python lang/validate-packs.py --write-catalog
python lang/validate-packs.py
python -m unittest discover -s lang -p "test_*.py"
```

Generation fails if any supported locale is missing or invalid. Validation requires exact key coverage, nonblank bounded values and identical .NET format placeholders, including formatting and repeated occurrences. English technical names and media-format identifiers may intentionally remain original. Complete mechanical coverage does not replace native-speaker translation review.

Pack source files must use UTF-8 with LF line endings, matching the repository's Git attributes. The validator rejects CRLF before hashing, and catalog generation writes LF independently of the host OS. This keeps published GitHub bytes and Windows/Linux checkout hashes identical.

During development, `--allow-incomplete` checks existing packs without producing a releasable catalog. Do not use that option as a release gate. Packs and catalogs must never contain media paths, executable code, markup or workflow authority.
