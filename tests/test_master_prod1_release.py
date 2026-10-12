"""Safety checks for a master-native active prod1 release."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]

class MasterProd1ReleaseTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.workflow = (ROOT / ".github/workflows/deploy-master-prod1.yml").read_text()
        cls.script = (ROOT / "scripts/deploy-active-prod1-npgsql-hotfix.sh").read_text()

    def test_exact_master_revision_not_staging_artifacts(self):
        self.assertIn("ref: ${{ github.sha }}", self.workflow)
        self.assertIn('test "$(git rev-parse HEAD)" = "$GITHUB_SHA"', self.workflow)
        self.assertIn('master-$tag', self.workflow)
        self.assertIn('master-$short', self.script)
        self.assertNotIn('staging-$short', self.script)
        self.assertIn('[[ "${GITHUB_SHA:-}" == "$sha" ]]', self.script)

    def test_previous_worker_remains_stopped(self):
        self.assertIn('[[ "$(status "$main_worker")" != running', self.script)
        self.assertNotIn('up -d --no-deps --force-recreate --pull never dhole-agent-workers\n', self.script)
        self.assertIn('dhole-agent-workers-prod1', self.script)
        self.assertIn('m.find_running_generation()[0]=="prod1"', self.script)

    def test_verified_backups_precede_image_promotion(self):
        self.assertLess(self.script.index('bash scripts/maersk-phase8-backup.sh prod1'),
                        self.script.index('docker image tag "dhole/agent-api:master-$short"'))
        self.assertIn("PROD1_HOTFIX_PROTECTED_BACKUPS_VERIFIED", self.script)
        self.assertIn("PROD1_HOTFIX_ROLLBACK_ATTEMPTED", self.script)
        self.assertIn('old_browser', self.script)

    def test_protected_trigger_and_confirmation(self):
        self.assertIn('branches: [master]', self.workflow)
        self.assertIn('.github/workflows/deploy-master-prod1.yml', self.workflow)
        self.assertIn('workflow_dispatch:', self.workflow)
        self.assertIn('DEPLOY_MASTER_ACTIVE_PROD1', self.workflow)
        self.assertIn('group: dhole-agent-production', self.workflow)

    def test_no_destructive_volume_cleanup(self):
        self.assertNotIn('docker volume prune', self.script)
        self.assertNotIn('docker compose down -v', self.script)
        self.assertNotIn('docker system prune --volumes', self.workflow)

if __name__ == "__main__":
    unittest.main()
