"""Offline validation for GitHub-dispatched generation operations."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PATH = ROOT / "scripts/production-generation-apply-request.py"
spec = importlib.util.spec_from_file_location("prod_apply", PATH)
apply = importlib.util.module_from_spec(spec)
spec.loader.exec_module(apply)

class ApplyRequestTests(unittest.TestCase):
    def test_name_format(self):
        for valid in ("prod1", "prod10", "prod12345"):
            self.assertTrue(apply.GENERATION.fullmatch(valid))
        for invalid in ("prod0", "prod-1", "stage", "prod", "prod3;rm"):
            self.assertFalse(bool(apply.GENERATION.fullmatch(invalid)))

    def test_secret_configuration_required(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "env"
            path.write_text("AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Database=dhole_agent\n")
            with self.assertRaises(apply.GateError):
                apply.parse_db(path)

    def test_connection_parsing(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "env"
            path.write_text("AGENT_POSTGRES_CONNECTION_STRING=Host=postgres;Database=dhole_agent;Username=agent;Password=private\n")
            values = apply.parse_db(path)
            self.assertEqual(values["Database"], "dhole_agent")
            self.assertEqual(values["Password"], "private")

if __name__ == "__main__":
    unittest.main()
