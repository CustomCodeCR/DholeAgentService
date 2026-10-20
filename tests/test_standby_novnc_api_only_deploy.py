"""API-only noVNC release must not touch the active prod2 Worker or browser profiles."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


class StandbyNoVncApiOnlyDeployTests(unittest.TestCase):
    def test_websocket_allowlist_contains_both_public_api_origins(self):
        source = (ROOT / "src/Dhole.Agent.Api/Endpoints/StandbyViewerGateway.cs").read_text()
        start = source.index("private static bool AllowedOrigin(")
        allowlist = source[start:]
        for host in ("api.logisticacastrofallas.com", "dhole-api.customcodecr.com"):
            self.assertIn('uri.Host == "' + host + '"', allowlist)
        self.assertIn('uri.Scheme == "https"', allowlist)
        self.assertIn('uri.Host == "sistema.logisticacastrofallas.com"', allowlist)

    def test_rollback_release_recreates_only_public_api(self):
        script = (ROOT / "scripts/deploy-standby-auth-api-only.sh").read_text()
        self.assertIn('find_running_generation()[0]=="prod2"', script)
        self.assertIn('worker=item("dhole-agent-prod2","dhole-agent-workers")', script)
        self.assertIn('worker-browser-volume', script)
        self.assertIn('worker["Image"]==open(state+"worker-image").read()', script)
        self.assertIn('compose up -d --no-deps --no-build --pull never --force-recreate dhole-agent-api', script)
        self.assertIn('http://dhole-dhole-agent-api-1:8080/health', script)
        self.assertNotIn('force-recreate dhole-agent-workers', script)
        self.assertNotIn('docker volume rm', script)
        self.assertNotIn('docker system prune', script)


if __name__ == "__main__":
    unittest.main()
