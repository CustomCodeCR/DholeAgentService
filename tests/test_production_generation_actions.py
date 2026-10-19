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

    def test_subprocess_diagnostic_only_exposes_allowlisted_codes(self):
        self.assertEqual(
            actions.safe_failure_code(
                "", "GENERATION_EXISTING_INCOMPLETE: service=dhole-agent-api\\n"),
            "generation_existing_incomplete")
        self.assertEqual(
            actions.safe_failure_code(
                "GENERATION_HEALTH_DETAIL service=api state=exited\\n"
                "GENERATION_INFRASTRUCTURE_HEALTH_FAILED: prod2\\n", ""),
            "generation_infrastructure_health_failed")
        self.assertEqual(
            actions.safe_failure_code(
                "password=sensitive\\n", "TOKEN=secret\\n"),
            "guarded_command_failed")
        self.assertEqual(
            actions.safe_failure_code(
                "", "GENERATION_API_CONFIG_INCOMPLETE: verify options\\n"),
            "generation_api_config_incomplete")

    def test_workflow_uses_protected_jwt_material_and_persists_failure_code(self):
        workflow = (SCRIPT.parents[1] / ".github/workflows/production-generation-actions.yml").read_text()
        self.assertIn('python3 scripts/prepare-prod1-jwt-env.py', workflow)
        self.assertIn('EXECUTION_DIAGNOSTIC:', workflow)
        self.assertIn('status=Failed', workflow)
        self.assertIn('--diagnostic-code', workflow)

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
