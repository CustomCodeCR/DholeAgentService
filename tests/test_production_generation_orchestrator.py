"""Regression tests for policy-only event requests (no Docker or provider calls)."""
import datetime as dt
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / "scripts/production-generation-orchestrator.py"
DEFAULT_CONFIG = ROOT / "config/production-generation-orchestrator.json"


class ProductionGenerationOrchestratorTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.directory = Path(self.tmp.name)
        self.state = self.directory / "state"
        self.config = self.directory / "rules.json"
        self.rules = json.loads(DEFAULT_CONFIG.read_text())
        self.rules["enabled"] = True
        self.save()

    def save(self):
        self.config.write_text(json.dumps(self.rules))

    def call(self, *args):
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--config", str(self.config),
             "--state-dir", str(self.state), "--event", "manual", *args],
            capture_output=True, text=True, check=False
        )

    def readiness(self):
        folder = self.state / "ready"
        folder.mkdir(parents=True)
        (folder / "prod1.json").write_text(json.dumps({
            "generation": "prod1",
            "ready": True,
            "verified_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        }))

    def test_disabled_policy_fails_closed(self):
        self.rules["enabled"] = False
        self.save()
        self.assertNotEqual(self.call("--mode", "plan").returncode, 0)

    def test_plan_does_not_mutate_state(self):
        proc = self.call("--mode", "plan")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        self.assertFalse(self.state.exists())

    def test_nonproduction_generation_rejected(self):
        self.assertNotEqual(self.call("--target", "other999").returncode, 0)

    def test_unbounded_numbered_generation_is_valid_in_plan(self):
        self.assertEqual(self.call("--target", "prod9999", "--mode", "plan").returncode, 0)

    def test_submit_requires_confirmation(self):
        self.readiness()
        self.assertNotEqual(self.call("--mode", "submit").returncode, 0)

    def test_submit_requires_readiness(self):
        proc = self.call("--mode", "submit",
                         "--confirmation", "AUTHORIZE_GENERATION_EVENT")
        self.assertNotEqual(proc.returncode, 0)

    def test_one_event_only_and_cooldown(self):
        self.readiness()
        proc = self.call("--mode", "submit",
                         "--confirmation", "AUTHORIZE_GENERATION_EVENT")
        self.assertEqual(proc.returncode, 0, proc.stderr)
        output = json.loads(proc.stdout)
        self.assertFalse(output["executed"])
        self.assertEqual(len(list((self.state / "pending").glob("*.json"))), 1)
        second = self.call("--mode", "submit",
                           "--confirmation", "AUTHORIZE_GENERATION_EVENT")
        self.assertNotEqual(second.returncode, 0)

    def test_failure_source_requires_real_unhealthy_evidence(self):
        self.rules["events"]["technical_failure"]["enabled"] = True
        self.save()
        self.assertNotEqual(self.call("--event", "technical_failure",
                                      "--component", "not-a-container").returncode, 0)

    def test_unsupported_provider_challenge_source_rejected(self):
        self.assertNotEqual(self.call("--event", "captcha").returncode, 0)


if __name__ == "__main__":
    unittest.main()
