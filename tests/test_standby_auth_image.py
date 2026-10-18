"""Regression contract: every Hub proof gets a trusted, local Inspector."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
AUTH_WORKFLOW = (ROOT / ".github/workflows/standby-authentication.yml").read_text()
AUTO_WORKFLOW = (ROOT / ".github/workflows/standby-hub-auto-inspect.yml").read_text()
BUILD_WORKFLOW = (ROOT / ".github/workflows/build-standby-inspector.yml").read_text()
SCRIPT = (ROOT / "scripts/ensure-standby-hub-probe.sh").read_text()
INSPECT = (ROOT / "scripts/standby-authentication.py").read_text()


class HubImageAvailabilityTests(unittest.TestCase):
    def test_web_inspect_and_finish_ensure_local_probe_first(self):
        probe = AUTH_WORKFLOW.index(
            "Ensure current Hub Inspector is available on this Docker host")
        inspect = AUTH_WORKFLOW.index(
            "Verify authenticated Maersk Hub before inspection or closure")
        self.assertLess(probe, inspect)
        self.assertIn(
            "inputs.operation == 'inspect' || inputs.operation == 'finish'",
            AUTH_WORKFLOW)
        self.assertIn("bash scripts/ensure-standby-hub-probe.sh", AUTH_WORKFLOW)
        self.assertIn("timeout-minutes: 25", AUTH_WORKFLOW)

    def test_scheduled_inspection_repairs_missing_image_too(self):
        image = AUTO_WORKFLOW.index("Ensure current Hub Inspector for unattended verification")
        inspect = AUTO_WORKFLOW.index(
            "Verify only current API-authorized Ready standby Hubs")
        self.assertLess(image, inspect)
        self.assertIn("packages: read", AUTO_WORKFLOW)
        self.assertIn("bash scripts/ensure-standby-hub-probe.sh", AUTO_WORKFLOW)

    def test_image_is_content_addressed_and_production_only(self):
        self.assertIn("GITHUB_REF:-", SCRIPT)
        self.assertIn("refs/heads/master", SCRIPT)
        self.assertIn("sha256sum Dockerfile NuGet.Config", SCRIPT)
        self.assertIn("find src docker -type f -print0", SCRIPT)
        self.assertIn('if docker image inspect "$image"', SCRIPT)
        self.assertIn('docker build --target worker-final', SCRIPT)
        self.assertIn('STANDBY_HUB_PROBE_IMAGE=%s', SCRIPT)
        self.assertIn('STANDBY_HUB_PROBE_IMAGE', INSPECT)

    def test_probing_never_modifies_running_containers_or_profiles(self):
        self.assertNotIn("docker stop", SCRIPT)
        self.assertNotIn("docker start", SCRIPT)
        self.assertNotIn("docker rm", SCRIPT)
        self.assertNotIn("docker volume", SCRIPT)
        self.assertNotIn("docker compose", SCRIPT)
        self.assertIn("--network none --read-only", SCRIPT)
        self.assertIn("--pull never", SCRIPT)

    def test_standalone_build_uses_same_ensured_image(self):
        self.assertIn("bash scripts/ensure-standby-hub-probe.sh", BUILD_WORKFLOW)
        self.assertIn("scripts/ensure-standby-hub-probe.sh", BUILD_WORKFLOW)


if __name__ == "__main__":
    unittest.main()
