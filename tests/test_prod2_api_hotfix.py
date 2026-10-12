"""Regression gates for the isolated prod2 API repair workflow."""
from pathlib import Path
import unittest

BASE = Path(__file__).resolve().parents[1]


class Prod2ApiHotfixTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.script = (BASE / "scripts/hotfix-prod2-api-only.sh").read_text()
        cls.workflow = (BASE / ".github/workflows/hotfix-prod2-api-only.yml").read_text()

    def test_only_stateless_prod2_api_is_replaced(self):
        self.assertIn("project=dhole-agent-prod2", self.script)
        self.assertIn("--force-recreate dhole-agent-api", self.script)
        self.assertNotIn("up -d --no-deps --no-build --pull never --force-recreate dhole-agent-workers", self.script)
        self.assertIn("prod2 Worker must not be active", self.script)
        self.assertIn('m.find_running_generation()[0]=="prod1"', self.script)
        self.assertIn('[[ "$(state "$prod1")" == running && -z "$legacy_running" ]]', self.script)

    def test_stable_image_digest_and_config_are_checked(self):
        self.assertIn('docker image inspect', self.script)
        self.assertIn('$(digest "$main_api")', self.script)
        self.assertIn('$(digest "$prod1_api")', self.script)
        self.assertIn("PROD2_API_CONFIG_MISMATCH", self.script)
        self.assertIn('dhole/agent-api:prod2-api-hotfix-$suffix', self.script)

    def test_preserve_sensitive_state_and_rollback(self):
        self.assertIn('agent-keys.tar.gz', self.script)
        self.assertIn('pg_dump', self.script)
        self.assertIn("PROD2_API_ROLLBACK_ATTEMPTED", self.script)
        self.assertIn('$(mount "$new_api" /data/agent-keys)', self.script)
        self.assertNotIn("docker volume rm", self.script)
        self.assertNotIn("docker compose down", self.script)
        self.assertNotIn("UPDATE agent.maersk_circuits", self.script)

    def test_production_is_explicitly_scoped(self):
        self.assertIn("branches: [master]", self.workflow)
        self.assertIn("DEPLOY_PROD2_API_ONLY", self.workflow)
        self.assertIn("group: dhole-agent-production", self.workflow)
        self.assertIn("bash -n scripts/hotfix-prod2-api-only.sh", self.workflow)


if __name__ == "__main__":
    unittest.main()
