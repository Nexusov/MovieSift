# Downloadable interface translations

English remains the complete built-in resource contract in `src/MovieSift.Desktop/Localization/Strings.resx`. Other languages are data-only UTF-8 JSON packs. Packs change application labels only; media titles, language metadata, diagnostics and preset names remain original.

Each pack declares schema `version: 1`, its exact `locale` and a complete `messages` dictionary. Locale filenames are fixed. `catalog.json` binds the exact bytes of every supported downloadable pack through its size and SHA256. The downloader resolves `main` through GitHub's small `git/ref/heads/main` response (bounded to 8 KiB), then obtains both catalog and pack from that immutable commit. It does not request commit diffs. This is an integrity/compatibility boundary, not an independently signed authenticity claim.

After changing English keys, update every translation and regenerate the catalog explicitly:

```powershell
python translations/validate-packs.py --write-catalog
python translations/validate-packs.py
python -m unittest discover -s translations -p "test_*.py"
```

Generation fails if any supported locale is missing or invalid. Validation requires exact key coverage, nonblank bounded values and identical .NET format placeholders, including formatting and repeated occurrences. English technical names and media-format identifiers may intentionally remain original. Complete mechanical coverage does not replace native-speaker translation review.

During development, `--allow-incomplete` checks existing packs without producing a releasable catalog. Do not use that option as a release gate. Packs and catalogs must never contain media paths, executable code, markup or workflow authority.
