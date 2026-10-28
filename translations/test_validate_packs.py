"""Exercise the catalog generator's refusal to bless incomplete or ambiguous translations."""

import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

MODULE_PATH = Path(__file__).with_name("validate-packs.py")
MODULE_SPECIFICATION = importlib.util.spec_from_file_location("validate_packs", MODULE_PATH)
VALIDATOR = importlib.util.module_from_spec(MODULE_SPECIFICATION)
MODULE_SPECIFICATION.loader.exec_module(VALIDATOR)


class TranslationValidationTests(unittest.TestCase):
    """Use owned temporary text files; no application state or network is touched."""

    def setUp(self):
        """Create a two-message contract with a repeated formatted argument."""
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name) / "es.json"
        self.contract = {"Title": "Tracks", "Count": "{0:N0} tracks; {0:N0}"}
        self.pack = {"version": 1, "locale": "es", "messages": {"Title": "Pistas", "Count": "{0:N0} pistas; {0:N0}"}}

    def validate(self):
        """Serialize the proposed complete pack and validate its exact bytes."""
        self.path.write_text(json.dumps(self.pack, ensure_ascii=False), encoding="utf-8")
        return VALIDATOR.validate_pack((self.path, "es", self.contract))

    def test_complete_pack_binds_exact_bytes(self):
        """Include a stable lowercase digest and the actual serialized length."""
        record = self.validate()
        self.assertEqual(record["bytes"], self.path.stat().st_size)
        self.assertRegex(record["sha256"], r"^[0-9a-f]{64}$")

    def test_missing_key_is_rejected(self):
        """Never silently fill a supposedly complete downloaded pack with English."""
        del self.pack["messages"]["Title"]
        with self.assertRaisesRegex(ValueError, "resource mismatch"):
            self.validate()

    def test_placeholder_format_or_multiplicity_change_is_rejected(self):
        """Protect both .NET format specifiers and repeated argument occurrences."""
        for changed in ("{0} pistas; {0}", "{0:N0} pistas", "{1:N0} pistas; {1:N0}"):
            with self.subTest(changed=changed):
                self.pack["messages"]["Count"] = changed
                with self.assertRaisesRegex(ValueError, "placeholder mismatch"):
                    self.validate()

    def test_whitespace_only_translation_is_rejected(self):
        """A present key with unusable text is not complete coverage."""
        self.pack["messages"]["Title"] = "  "
        with self.assertRaisesRegex(ValueError, "empty, invalid or excessive"):
            self.validate()

    def test_duplicate_json_properties_are_rejected(self):
        """Reject duplicate keys rather than validating a lossy dictionary view."""
        self.path.write_text('{"version":1,"version":1}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate JSON property"):
            VALIDATOR.validate_pack((self.path, "es", self.contract))

    def test_unsupported_version_and_wrong_locale_are_rejected(self):
        """Bind each pack to its declared supported schema and locale filename."""
        for version, locale in ((2, "es"), (True, "es"), (1, "fr")):
            with self.subTest(version=version, locale=locale):
                self.pack["version"] = version
                self.pack["locale"] = locale
                with self.assertRaisesRegex(ValueError, "invalid version or locale"):
                    self.validate()


if __name__ == "__main__":
    unittest.main()
