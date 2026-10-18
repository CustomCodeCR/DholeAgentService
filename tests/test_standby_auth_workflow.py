"""The Hub-auth finish path must not claim Worker handoff readiness."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
WORKFLOW = ROOT / ".github/workflows/standby-authentication.yml"
EXECUTOR = ROOT / "scripts/production-generation-executor.py"

class HubFinishSeparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = WORKFLOW.read_text(encoding="utf-8")

    def test_hub_proof_is_saved_before_verified_and_browser_cleanup(self):
        text = self.workflow
        stages = (
            "Record verified Hub dashboard evidence in PostgreSQL",
            "Mark authenticated Hub independently verified",
            "Close only authenticated standby viewer; preserve browser profile",
        )
        self.assertTrue(all(stage in text for stage in stages))
        self.assertLess(text.index(stages[0]), text.index(stages[1]))
        self.assertLess(text.index(stages[1]), text.index(stages[2]))

    def test_hub_finish_does_not_perform_handoff_attestation(self):
        text = self.workflow
        # An open circuit or missing provisioned Worker must not erase a
        # genuine verified browser login. Readiness is checked at handoff.
        self.assertNotIn(
            "python3 scripts/operator-attest-generation-readiness.py", text)
        self.assertIn('if: ${{ inputs.operation == \'finish\' }}', text)
        self.assertIn('--status Verified', text)

    def test_guarded_worker_handoff_still_checks_live_circuit_and_jobs(self):
        text = EXECUTOR.read_text(encoding="utf-8")
        self.assertIn("database_clear(config_db)", text)
        self.assertIn("standby readiness is stale", text)
        self.assertIn("standby has no operator-verified readiness record", text)
        self.assertIn("standby Worker must be stopped before handoff", text)


if __name__ == "__main__":
    unittest.main()
