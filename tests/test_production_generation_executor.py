"""Offline tests: verify a generation switch refuses unverified provider access."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
PATH = ROOT / "scripts/production-generation-executor.py"
spec = importlib.util.spec_from_file_location("generation_executor", PATH)
executor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(executor)


class ExecutorSafetyTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.state = Path(self.tmp.name)
        self.ready = self.state / "ready"
        self.ready.mkdir()
        self.config = {
            "enabled": True,
            "allowed_destinations": ["prod1"],
            "events": {"manual": {"enabled": True}},
            "readiness_max_age_seconds": 3600,
        }
        self.request = {
            "event": "manual", "target": "prod1", "status": "pending",
            "requires_executor_verification": True,
        }
        self.record = {
            "generation": "prod1", "ready": True,
            "verified_at_utc": dt.datetime.now(dt.timezone.utc).isoformat(),
        }

    def save(self):
        (self.ready / "prod1.json").write_text(json.dumps(self.record))

    def test_no_attestation_is_refused(self):
        self.save()
        with self.assertRaisesRegex(RuntimeError, "authorization"):
            executor.validate(self.state, self.request, self.config)

    def test_verified_and_fresh_is_accepted(self):
        self.record.update(provider_authorized=True, verified_by="operator")
        self.save()
        self.assertEqual(executor.validate(self.state, self.request, self.config), "prod1")

    def test_provider_attestation_expires(self):
        self.record.update(
            provider_authorized=True, verified_by="operator",
            verified_at_utc=(dt.datetime.now(dt.timezone.utc)
                             - dt.timedelta(days=1)).isoformat())
        self.save()
        with self.assertRaisesRegex(RuntimeError, "stale"):
            executor.validate(self.state, self.request, self.config)

    def test_disabled_events_are_refused(self):
        self.config["events"]["manual"]["enabled"] = False
        self.record.update(provider_authorized=True, verified_by="operator")
        self.save()
        with self.assertRaisesRegex(RuntimeError, "disabled"):
            executor.validate(self.state, self.request, self.config)

    def test_source_restriction_is_not_an_event_type(self):
        self.request["event"] = "captcha"
        self.record.update(provider_authorized=True, verified_by="operator")
        self.save()
        with self.assertRaisesRegex(RuntimeError, "unsupported event"):
            executor.validate(self.state, self.request, self.config)

    def test_persistent_volume_validation(self):
        source = {"Mounts": [{"Destination": "/data/browser-profiles",
                              "Type": "volume", "Name": "primary"}]}
        target = {"Mounts": [{"Destination": "/data/browser-profiles",
                              "Type": "volume", "Name": "standby"}]}
        self.assertNotEqual(executor.mount_name(source, "/data/browser-profiles"),
                            executor.mount_name(target, "/data/browser-profiles"))

    def test_health_check(self):
        self.assertTrue(executor.healthy({"State": {"Status": "running"}}))
        self.assertFalse(executor.healthy({"State": {"Status": "restarting"}}))
        self.assertFalse(executor.healthy({"State": {
            "Status": "running", "Health": {"Status": "unhealthy"}}}))


if __name__ == "__main__":
    unittest.main()
