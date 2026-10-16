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

    def test_pre_auth_uses_own_volume_even_if_dormant_worker_is_pruned(self):
        import json
        active = {"Image": "worker-image"}
        healthy = {"State": {"Status": "running"}}
        volume = "dhole-agent-prod2_browser-profiles"
        metadata = [{"Name": volume, "Labels": {
            "com.docker.compose.project": "dhole-agent-prod2",
            "com.docker.compose.volume": "browser-profiles"}}]
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1", active)), \
             patch.object(auth.executor, "generation_services", return_value=(
                 "dhole-agent-prod2", "dhole-agent-api", "dhole-agent-workers", "hermes-agent")), \
             patch.object(auth.executor, "inspect", side_effect=[healthy, healthy, RuntimeError("pruned")]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth.executor, "mount_name", return_value=("volume", "prod1_profiles")), \
             patch.object(auth.subprocess, "run", return_value=type("RunResult", (), {"stdout": ""})()), \
             patch.object(auth, "safe_run", return_value=json.dumps(metadata)):
            stopped_worker, result_volume = auth.guard_standby("prod2")
            self.assertIsNone(stopped_worker)
            self.assertEqual(result_volume, volume)

    def test_pre_auth_rejects_running_extra_worker(self):
        import json
        active = {"Image": "worker-image"}
        healthy = {"State": {"Status": "running"}}
        running_worker = {"State": {"Status": "running"}}
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1", active)), \
             patch.object(auth.executor, "generation_services", return_value=(
                 "dhole-agent-prod2", "dhole-agent-api", "dhole-agent-workers", "hermes-agent")), \
             patch.object(auth.executor, "inspect", side_effect=[healthy, healthy]), \
             patch.object(auth.subprocess, "run", return_value=type("RunResult", (), {"stdout": "worker-id\\n"})()), \
             patch.object(auth, "safe_run", return_value=json.dumps([running_worker])), \
             patch.object(auth.executor, "healthy", return_value=True):
            with self.assertRaisesRegex(RuntimeError, "standby_worker_must_remain_stopped"):
                auth.guard_standby("prod2")

    def test_pre_auth_rejects_untrusted_browser_volume(self):
        import json
        active = {"Image": "worker-image"}
        healthy = {"State": {"Status": "running"}}
        volume = "dhole-agent-prod2_browser-profiles"
        metadata = [{"Name": volume, "Labels": {
            "com.docker.compose.project": "another-project",
            "com.docker.compose.volume": "browser-profiles"}}]
        with patch.object(auth.executor, "find_running_generation", return_value=("prod1", active)), \
             patch.object(auth.executor, "generation_services", return_value=(
                 "dhole-agent-prod2", "dhole-agent-api", "dhole-agent-workers", "hermes-agent")), \
             patch.object(auth.executor, "inspect", side_effect=[healthy, healthy, RuntimeError("pruned")]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth.executor, "mount_name", return_value=("volume", "prod1_profiles")), \
             patch.object(auth.subprocess, "run", return_value=type("RunResult", (), {"stdout": ""})()), \
             patch.object(auth, "safe_run", return_value=json.dumps(metadata)):
            with self.assertRaisesRegex(RuntimeError, "standby_browser_volume_not_owned_by_generation"):
                auth.guard_standby("prod2")

    def test_workflow_repairs_only_dormant_worker_before_starting_viewer(self):
        workflow = (ROOT / ".github/workflows/standby-authentication.yml").read_text()
        repair = "Safely repair missing dormant standby Worker before browser preparation"
        claim = "Claim requested standby authentication"
        viewer = "Start isolated temporary Chromium and loopback-only noVNC"
        self.assertLess(workflow.index(repair), workflow.index(claim))
        self.assertLess(workflow.index(repair), workflow.index(viewer))
        step = workflow.split("- name: " + repair, 1)[1].split("- name: " + claim, 1)[0]
        self.assertIn("inputs.operation == 'prepare'", step)
        self.assertIn("scripts/provision-production-generation.sh", step)
        self.assertIn('"$GENERATION" apply .dhole-system "$GENERATION_PRIVATE_ENV"', step)
        self.assertNotIn("docker compose up", step)
        self.assertNotIn("docker start", step)
        self.assertNotIn("docker rm", step)

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

    def test_reprepare_existing_viewer_cannot_delete_profile(self):
        # A timed-out 30-minute auth may leave a viewer behind; the next
        # authorized prepare only stops/removes that exact ephemeral viewer.
        source = (ROOT / "scripts/standby-authentication.py").read_text()
        self.assertIn('inspect_existing(generation, credential)', source)
        self.assertIn('["docker", "stop", "--time", "25", existing]', source)
        self.assertIn('["docker", "rm", existing]', source)
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
