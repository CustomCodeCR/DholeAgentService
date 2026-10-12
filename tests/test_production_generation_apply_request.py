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

    def test_missing_operator_attestation_has_actionable_code(self):
        output = ("GENERATION_SWITCH_FAILED: GENERATION_SWITCH_REFUSED: "
                  "standby has no operator-verified readiness record")
        self.assertEqual(apply.preflight_failure_code(output),
                         "standby_readiness_missing")

    def test_readiness_expiry_has_distinct_code(self):
        output = ("GENERATION_SWITCH_FAILED: GENERATION_SWITCH_REFUSED: "
                  "standby readiness is stale")
        self.assertEqual(apply.preflight_failure_code(output),
                         "standby_readiness_expired")

    def test_circuit_and_live_jobs_are_never_bypassed(self):
        output = ("GENERATION_SWITCH_FAILED: GENERATION_SWITCH_REFUSED: "
                  "live circuit restrictions or active Agent executions prohibit handoff")
        self.assertEqual(apply.preflight_failure_code(output),
                         "provider_circuit_or_jobs_blocked")

    def test_raw_unknown_preflight_error_not_leaked(self):
        output = "private connection token=secret-token"
        self.assertEqual(apply.preflight_failure_code(output),
                         "standby_preflight_failed")


if __name__ == "__main__":
    unittest.main()
