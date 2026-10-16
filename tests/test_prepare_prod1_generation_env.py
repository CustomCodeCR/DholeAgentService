import importlib.util
import os
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts" / "prepare-prod1-generation-env.py"
spec = importlib.util.spec_from_file_location("generation_env", SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class PrepareProd1GenerationEnvTests(unittest.TestCase):
    def test_updates_stale_disabled_values_without_exposing_secret(self):
        token = "github_pat_SYNTHETIC_TEST_NOT_REAL_1234567890"
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / ".env.production"
            path.write_text(
                "POSTGRES_USER=testuser\n"
                "GenerationOrchestrator__Enabled=false\n"
                "GenerationOrchestrator__GitHubToken=outdated\n"
                "GenerationOrchestrator__ActionsRef=develop\n"
                "OTHER_SETTING=preserve-this\n"
            )
            os.chmod(path, 0o600)
            module.prepare(path, token)
            value = path.read_text()
            self.assertEqual(value.count("GenerationOrchestrator__Enabled="), 1)
            self.assertEqual(value.count("GenerationOrchestrator__GitHubToken="), 1)
            self.assertEqual(value.count("GenerationOrchestrator__ActionsRef="), 1)
            self.assertIn("GenerationOrchestrator__Enabled=true\n", value)
            self.assertIn("GenerationOrchestrator__GitHubToken=" + token + "\n", value)
            self.assertIn("GenerationOrchestrator__ActionsRef=master\n", value)
            self.assertIn("POSTGRES_USER=testuser\n", value)
            self.assertIn("OTHER_SETTING=preserve-this\n", value)
            self.assertEqual(path.stat().st_mode & 0o777, 0o600)

    def test_rejects_missing_malformed_token_without_mutation(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / ".env.production"
            path.write_text("MUST_STAY=untouched\n")
            os.chmod(path, 0o600)
            for token in ("", "short", "github_pat_fake\nINJECTION=true",
                          "github_pat_fake with space!", "github_pat_bad=equals"):
                with self.subTest(token=token):
                    with self.assertRaises(ValueError):
                        module.prepare(path, token)
                    self.assertEqual(path.read_text(), "MUST_STAY=untouched\n")

    def test_rejects_unprotected_file_and_symlink(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root) / ".env.production"
            path.write_text("KEEP=true\n")
            os.chmod(path, 0o644)
            with self.assertRaises(ValueError):
                module.prepare(path, "github_pat_SYNTHETIC_TEST_NOT_REAL_1234567890")
            os.chmod(path, 0o600)
            link = Path(root) / "alias"
            link.symlink_to(path)
            with self.assertRaises(ValueError):
                module.prepare(link, "github_pat_SYNTHETIC_TEST_NOT_REAL_1234567890")


if __name__ == "__main__":
    unittest.main()
