"""Standby authentication never starts queue Workers or imports active cookies."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "standby_authentication", ROOT / "scripts/standby-authentication.py")
auth = importlib.util.module_from_spec(spec)
spec.loader.exec_module(auth)


class StandbyAuthenticationTests(unittest.TestCase):
    def test_only_isolated_standby_generations_have_unique_loopback_ports(self):
        self.assertEqual(auth.viewer_port("prod2"), 6083)
        self.assertEqual(auth.viewer_port("prod3"), 6084)
        self.assertEqual(auth.viewer_port("prod200"), 6281)
        with self.assertRaises(RuntimeError):
            auth.viewer_port("prod1")
        for bad in ("prod", "prod0", "prod201", "prod2;rm", "prod02", "foo"):
            with self.subTest(bad=bad), self.assertRaises(RuntimeError):
                auth.viewer_port(bad)

    def test_requires_inactive_isolated_worker_with_independent_volume(self):
        active = {"Image": "worker-image"}
        target = {"Image": "worker-image", "State": {"Status": "exited"}}
        healthy = {"State": {"Status": "running"}}
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1", active)), \
             patch.object(auth.executor, "generation_services",
                          return_value=("dhole-agent-prod2", "dhole-agent-api", "dhole-agent-workers", "hermes-agent")), \
             patch.object(auth.executor, "inspect", side_effect=[healthy, healthy, target]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth, "standby_browser_volume", return_value="prod2"), \
             patch.object(auth.executor, "mount_name", side_effect=[("volume","prod1"),("volume","prod2")]):
            item, volume = auth.guard_standby("prod2")
            self.assertEqual(item, target)
            self.assertEqual(volume, "prod2")

    def test_standby_cannot_share_active_browser_volume(self):
        active = {"Image": "img"}
        stopped = {"Image": "img", "State": {"Status": "exited"}}
        ok = {"State": {"Status": "running"}}
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1",active)), \
             patch.object(auth.executor, "generation_services",
                          return_value=("dhole-agent-prod2", "api", "worker", "hermes")), \
             patch.object(auth.executor, "inspect",side_effect=[ok,ok,stopped]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth, "standby_browser_volume", return_value="same"), \
             patch.object(auth.executor, "mount_name",return_value=("volume","same")):
            with self.assertRaisesRegex(RuntimeError,"standby_browser_volume_not_isolated"):
                auth.guard_standby("prod2")

    def test_browser_only_workflow_does_not_create_or_start_worker(self):
        workflow = (ROOT / ".github/workflows/standby-authentication.yml").read_text()
        self.assertNotIn("scripts/provision-production-generation.sh", workflow)
        self.assertNotIn("docker compose up", workflow)
        self.assertNotIn("docker start dhole-agent-workers", workflow)
        self.assertLess(workflow.index("Claim requested standby authentication"),
                        workflow.index("Start isolated temporary Chromium"))

    def test_dormant_worker_may_be_absent_for_browser_only_authentication(self):
        active = {"Image": "img"}
        healthy = {"State": {"Status": "running"}}
        missing = RuntimeError("GENERATION_SWITCH_REFUSED: exactly one expected container is required: dhole-agent-prod2/dhole-agent-workers")
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1", active)), \
             patch.object(auth.executor, "generation_services",
                          return_value=("dhole-agent-prod2", "api", "worker", "hermes")), \
             patch.object(auth.executor, "inspect", side_effect=[healthy, healthy, missing]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth, "standby_browser_volume", return_value="prod2-browser"), \
             patch.object(auth.executor, "mount_name", return_value=("volume", "prod1-browser")):
            item, volume = auth.guard_standby("prod2")
            self.assertIsNone(item)
            self.assertEqual(volume, "prod2-browser")

    def test_volume_lookup_requires_existing_compose_owned_identity(self):
        import types
        import json
        result = types.SimpleNamespace(returncode=0, stdout="prod2-profile-volume\\n")
        identity = [{
            "Name": "prod2-profile-volume",
            "Labels": {
                "com.docker.compose.project": "dhole-agent-prod2",
                "com.docker.compose.volume": "browser-profiles"
            }
        }]
        with patch.object(auth.subprocess, "run", return_value=result) as ls, \
             patch.object(auth, "safe_run", return_value=json.dumps(identity)):
            self.assertEqual(auth.standby_browser_volume("dhole-agent-prod2"),
                             "prod2-profile-volume")
            self.assertEqual(ls.call_args.args[0][0:3], ["docker", "volume", "ls"])
        with patch.object(auth.subprocess, "run", return_value=types.SimpleNamespace(
                returncode=0, stdout="")):
            with self.assertRaisesRegex(RuntimeError, "volume_missing_or_ambiguous"):
                auth.standby_browser_volume("dhole-agent-prod2")
        identity[0]["Labels"]["com.docker.compose.project"] = "dhole-agent-prod1"
        with patch.object(auth.subprocess, "run", return_value=result), \
             patch.object(auth, "safe_run", return_value=json.dumps(identity)):
            with self.assertRaisesRegex(RuntimeError, "volume_identity_mismatch"):
                auth.standby_browser_volume("dhole-agent-prod2")

    def test_duplicate_prepare_keeps_healthy_live_viewer(self):
        import types
        with patch.object(auth, "guard_standby", return_value=(None, "volume")), \
             patch.object(auth.executor, "find_running_generation",
                          return_value=("prod1", {"Image": "img", "State": {"Status": "running"}})), \
             patch.object(auth, "find_viewer", return_value="viewer"), \
             patch.object(auth, "inspect_existing", return_value=("viewer", {
                 "State": {"Status": "running"}})), \
             patch.object(auth.subprocess, "run", return_value=types.SimpleNamespace(
                 returncode=0)), \
             patch.object(auth, "safe_run") as docker:
            auth.prepare("prod2", "a" * 32, ROOT / "docker/start-standby-auth.sh")
            docker.assert_not_called()

    def test_active_target_is_refused(self):
        with patch.object(auth.executor, "find_running_generation",
                          return_value=("prod2",{"Image":"img"})):
            with self.assertRaisesRegex(RuntimeError,"target_already_active"):
                auth.guard_standby("prod2")

    def test_browser_identity_inspection_only_uses_exact_standby_container(self):
        import tempfile
        import types
        import json
        identity = {"DisplayName": "Example Operator", "Username": "authorized01",
                    "CustomerId": "1234567890"}
        with tempfile.TemporaryDirectory() as root, \
             patch.object(auth, "guard_standby"), \
             patch.object(auth, "inspect_existing", return_value=(
                 "standby-viewer-id", {"State": {"Status": "running"}})), \
             patch.object(auth.subprocess, "run", return_value=types.SimpleNamespace(
                 returncode=0, stdout=json.dumps(identity), stderr="")) as execute:
            output = Path(root) / "verified-identity.json"
            auth.inspect_account("prod2", "a"*32, output)
            self.assertEqual(json.loads(output.read_text()), identity)
            self.assertEqual(output.stat().st_mode & 0o777, 0o600)
            self.assertEqual(execute.call_args.args[0][:3],
                             ["docker", "exec", "standby-viewer-id"])
            self.assertIn("--standby-account-inspect", execute.call_args.args[0])

    def test_identity_probe_rejects_unverified_account_without_writing(self):
        import tempfile
        import types
        with tempfile.TemporaryDirectory() as root, \
             patch.object(auth, "guard_standby"), \
             patch.object(auth, "inspect_existing", return_value=(
                 "standby-viewer-id", {"State": {"Status": "running"}})), \
             patch.object(auth.subprocess, "run", return_value=types.SimpleNamespace(
                 returncode=3, stdout="", stderr="safe_error")):
            output = Path(root) / "identity.json"
            with self.assertRaisesRegex(RuntimeError,
                                        "standby_account_not_visible_in_official_menu"):
                auth.inspect_account("prod2", "a"*32, output)
            self.assertFalse(output.exists())

    def test_reprepare_never_removes_persistent_browser_profile(self):
        source = (ROOT / "scripts/standby-authentication.py").read_text()
        self.assertIn('inspect_existing(generation, credential)', source)
        self.assertIn('["docker", "rm", existing]', source)
        self.assertNotIn('["docker", "stop", "--time", "25", existing]', source)
        self.assertNotIn('"docker", "volume", "rm"', source)

    def test_restarting_viewer_keeps_persistent_profile_volume(self):
        with patch.object(auth, "guard_standby"), \
             patch.object(auth, "inspect_existing", return_value=(
                 "ephemeral-only", {"State": {"Status": "running"}})), \
             patch.object(auth, "safe_run") as docker, \
             patch.object(auth, "prepare") as prepare:
            auth.restart_viewer("prod2", "b"*32, ROOT / "docker/start-standby-auth.sh")
            self.assertEqual(docker.call_args.args[0],
                             ["docker", "stop", "--time", "25", "ephemeral-only"])
            prepare.assert_called_once()
            self.assertNotIn("volume", str(docker.call_args_list))

    def test_finish_only_stops_ephemeral_viewer(self):
        container = {"State":{"Status":"running"}}
        with patch.object(auth, "guard_standby"), \
             patch.object(auth, "inspect_existing",return_value=("cid",container)), \
             patch.object(auth, "safe_run") as run:
            auth.finish("prod2","a"*32)
            commands=[call.args[0] for call in run.call_args_list]
            self.assertEqual(commands,[["docker","stop","--time","25","cid"],
                                       ["docker","rm","cid"]])
            self.assertFalse(any("volume" in part for c in commands for part in c))


if __name__=="__main__":
    unittest.main()
