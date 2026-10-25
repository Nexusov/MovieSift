"""Keep a complete, exact receipt for approved upstream omissions using tiny real files."""

import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest


SPECIFICATION = importlib.util.spec_from_file_location("tool_selection", Path(__file__).parents[1] / "select-tool-payload.py")
SELECTION = importlib.util.module_from_spec(SPECIFICATION)
SPECIFICATION.loader.exec_module(SELECTION)


class ToolPayloadSelectionTests(unittest.TestCase):
    """Do not turn an observed CLI closure into silent arbitrary file dropping."""

    def setUp(self):
        """Create one necessary library and one specifically approved unused asset."""
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "upstream"
        self.destination = self.root / "selected"
        self.destination.mkdir()
        self.policy = self.root / "selection.json"
        self.lock = self.root / "tools.json"
        rows = []
        for name in ("required.so.1", "unused.so.2"):
            path = self.source / "usr/lib" / name
            path.parent.mkdir(parents=True, exist_ok=True)
            data = name.encode()
            path.write_bytes(data)
            rows.append({"sourceMember": "usr/lib/" + name, "size": len(data), "sha256": hashlib.sha256(data).hexdigest()})
        rows[0]["path"] = "app-root/usr/lib/required.so.1"
        rows[1]["previousPackagePath"] = "app-root/usr/lib/unused.so.2"
        self.value = {"schemaVersion": 1, "version": "102.0", "linux-x64": {
            "artifactSha256": "1" * 64, "sourceDirectory": "usr/lib", "includedFiles": [rows[0]],
            "omittedFiles": [rows[1]], "reason": "Controlled approved omission",
        }}
        self.policy.write_text(json.dumps(self.value))
        self.lock.write_text(json.dumps({"version": "102.0", "linux-x64": {"sha256": "1" * 64}}))

    def select(self):
        """Call the same acquisition function with explicit controlled artifacts."""
        return SELECTION.select_payload(self.source, self.destination, "linux-x64", self.policy, self.lock)

    def test_unchanged_library_is_copied_and_omission_receipted(self):
        """The omitted asset remains in the source and is absent only from the new package."""
        receipt = self.select()
        self.assertEqual(b"required.so.1", (self.destination / "app-root/usr/lib/required.so.1").read_bytes())
        self.assertFalse((self.destination / "app-root/usr/lib/unused.so.2").exists())
        self.assertTrue((self.source / "usr/lib/unused.so.2").exists())
        self.assertEqual("usr/lib/unused.so.2", receipt["omittedFiles"][0]["sourceMember"])

    def test_unexpected_changed_or_missing_member_rejects_before_copy(self):
        """An updated upstream layout cannot be quietly reduced to a guessed closure."""
        (self.source / "usr/lib/new.so.3").write_bytes(b"unknown")
        with self.assertRaisesRegex(ValueError, "missing, added or changed"):
            self.select()
        self.assertFalse((self.destination / "app-root").exists())
        (self.source / "usr/lib/new.so.3").unlink()
        (self.source / "usr/lib/unused.so.2").unlink()
        with self.assertRaisesRegex(ValueError, "complete upstream directory"):
            self.select()

    def test_different_upstream_pin_or_escaping_path_is_rejected(self):
        """Do not reuse omission research for another archive or outside a selected root."""
        self.value["linux-x64"]["artifactSha256"] = "2" * 64
        self.policy.write_text(json.dumps(self.value))
        with self.assertRaisesRegex(ValueError, "different upstream artifact"):
            self.select()
        with self.assertRaisesRegex(ValueError, "Invalid tool selection path"):
            SELECTION.relative_path("../outside.so")


if __name__ == "__main__":
    unittest.main()
