"""Safety regression: readiness is never inferred by the Worker or Docker health."""
import importlib.util
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "operator_readiness", ROOT / "scripts/operator-attest-generation-readiness.py")
operator = importlib.util.module_from_spec(spec)
spec.loader.exec_module(operator)

ENV = {
    "GITHUB_EVENT_NAME": "workflow_dispatch",
    "GITHUB_REF": "refs/heads/master",
    "GITHUB_ACTOR": "MauLang18",
}


class OperatorReadinessTests(unittest.TestCase):
    def argv(self, state, confirmation):
        return ["operator-attest-generation-readiness.py",
                "--generation", "prod2",
                "--confirmation", confirmation,
                "--env-file", "/dummy/protected/env",
                "--state-dir", str(state)]

    def test_no_confirmation_never_checks_or_records_authorization(self):
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(operator.os.environ, ENV), \
             patch.object(operator.sys, "argv", self.argv(tmp, "YES")), \
             patch.object(operator, "check_infrastructure") as check, \
             patch.object(operator, "record_database_readiness") as record:
            with self.assertRaises(SystemExit):
                operator.main()
            check.assert_not_called()
            record.assert_not_called()
            self.assertFalse((Path(tmp) / "ready" / "prod2.json").exists())

    def test_non_manual_run_never_creates_authorization(self):
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(operator.os.environ, {**ENV, "GITHUB_EVENT_NAME": "push"}), \
             patch.object(operator.sys, "argv", self.argv(tmp, operator.CONFIRMATION)), \
             patch.object(operator, "check_infrastructure") as check:
            with self.assertRaises(SystemExit):
                operator.main()
            check.assert_not_called()
            self.assertFalse((Path(tmp) / "ready" / "prod2.json").exists())

    def test_failed_technical_gate_never_creates_authorization(self):
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(operator.os.environ, ENV), \
             patch.object(operator.sys, "argv", self.argv(tmp, operator.CONFIRMATION)), \
             patch.object(operator, "check_infrastructure",
                          side_effect=SystemExit(1)), \
             patch.object(operator, "record_database_readiness") as record:
            with self.assertRaises(SystemExit):
                operator.main()
            record.assert_not_called()
            self.assertFalse((Path(tmp) / "ready" / "prod2.json").exists())

    def test_operator_attestation_records_only_after_technical_gate(self):
        events = []
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(operator.os.environ, ENV), \
             patch.object(operator.sys, "argv", self.argv(tmp, operator.CONFIRMATION)), \
             patch.object(operator, "check_infrastructure",
                          side_effect=lambda *args: events.append("technical")), \
             patch.object(operator, "record_database_readiness",
                          side_effect=lambda *args: events.append("database")) as record:
            operator.main()
            marker = Path(tmp) / "ready" / "prod2.json"
            assert marker.is_file()
            data = json.loads(marker.read_text())
            self.assertEqual(data["generation"], "prod2")
            self.assertTrue(data["ready"])
            self.assertTrue(data["provider_authorized"])
            self.assertEqual(data["verified_by"], "MauLang18")
            self.assertEqual(events, ["technical", "database"])
            self.assertEqual(record.call_args.args[1], "prod2")


if __name__ == "__main__":
    unittest.main()
