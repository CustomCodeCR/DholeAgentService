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
             patch.object(auth.executor, "inspect", side_effect=[target, healthy, healthy]), \
             patch.object(auth.executor, "healthy", return_value=True), \
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
             patch.object(auth.executor, "inspect",side_effect=[stopped,ok,ok]), \
             patch.object(auth.executor, "healthy", return_value=True), \
             patch.object(auth.executor, "mount_name",return_value=("volume","same")):
            with self.assertRaisesRegex(RuntimeError,"standby_browser_volume_not_isolated"):
                auth.guard_standby("prod2")

    def test_active_target_is_refused(self):
        with patch.object(auth.executor, "find_running_generation",
                          return_value=("prod2",{"Image":"img"})):
            with self.assertRaisesRegex(RuntimeError,"target_already_active"):
                auth.guard_standby("prod2")

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
