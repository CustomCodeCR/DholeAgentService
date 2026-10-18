"""Offline regression coverage for Actions-only, guard-preserving generation control."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "scripts/production-generation-actions.py"
SPEC = importlib.util.spec_from_file_location("generation_actions", SCRIPT)
actions = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(actions)


class GenerationActionsTests(unittest.TestCase):
    def test_numeric_successor_and_wrap(self):
        pool = ["prod3", "prod10", "prod1", "prod2"]
        self.assertEqual(actions.numbered_targets("prod1", pool),
                         ["prod2", "prod3", "prod10"])
        self.assertEqual(actions.numbered_targets("prod2", pool),
                         ["prod3", "prod10", "prod1"])
        self.assertEqual(actions.numbered_targets("prod10", pool),
                         ["prod1", "prod2", "prod3"])

    def test_invalid_destination_rejected(self):
        self.assertEqual(actions.numbered_targets("prod", [
            "prod2", "prod0", "prod2", "prod-3", "production", "PROD4", "prod4"
        ]), ["prod2", "prod4"])

    def test_readiness_requires_recent_independent_authorization(self):
        with tempfile.TemporaryDirectory() as temporary:
            marker = Path(temporary) / "prod2.json"
            record = {
                "generation": "prod2", "ready": True, "provider_authorized": True,
                "verified_by": "operator", "verified_at_utc":
                dt.datetime.now(dt.timezone.utc).isoformat(),
            }
            marker.write_text(json.dumps(record))
            self.assertTrue(actions.marker_verified(marker, "prod2", 3600))
            record["provider_authorized"] = False
            marker.write_text(json.dumps(record))
            self.assertFalse(actions.marker_verified(marker, "prod2", 3600))
            record["provider_authorized"] = True
            record["verified_at_utc"] = (
                dt.datetime.now(dt.timezone.utc) - dt.timedelta(hours=2)).isoformat()
            marker.write_text(json.dumps(record))
            self.assertFalse(actions.marker_verified(marker, "prod2", 3600))

    def test_symlink_readiness_does_not_count(self):
        with tempfile.TemporaryDirectory() as temporary:
            original = Path(temporary) / "marker.json"
            original.write_text("{}")
            alias = Path(temporary) / "prod2.json"
            alias.symlink_to(original)
            self.assertFalse(actions.marker_verified(alias, "prod2", 3600))

    def test_automatic_trigger_requires_real_docker_failure(self):
        class FakeDocker:
            @staticmethod
            def generation_services(active):
                return ("project", "api", "worker", "hermes")

            @staticmethod
            def inspect(project, service):
                return {"Id": service, "State": {
                    "Status": "running", "Health": {"Status": "healthy"}}}

        worker = {"Id": "worker", "State": {
            "Status": "running", "Health": {"Status": "healthy"}}}
        self.assertIsNone(actions.technical_component("prod2", worker, FakeDocker()))
        worker["State"]["Health"]["Status"] = "unhealthy"
        self.assertEqual(actions.technical_component("prod2", worker, FakeDocker()), "worker")


if __name__ == "__main__":
    unittest.main()
