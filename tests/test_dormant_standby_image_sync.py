"""Fail-closed regression for dormant standby release-image synchronization."""
import importlib.util
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import Mock, patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "standby_sync", ROOT / "scripts/synchronize-dormant-generation-images.py")
sync = importlib.util.module_from_spec(spec)
spec.loader.exec_module(sync)


class StandbySyncTests(unittest.TestCase):
    def state(self, matching):
        return {
            "worker": {"Image": "sha256:old" if not matching else "sha256:new",
                       "State": {"Status": "created"}},
            "source_worker": {"Image": "sha256:new", "Id": "primary-worker"},
            "api": {"Image": "sha256:old" if not matching else "sha256:new"},
            "source_api": {"Image": "sha256:new", "Id": "primary-api"},
            "hermes": {"Id": "fixed-hermes"},
            "mounts": {"worker_browser": ("volume", "browser-prod1"),
                       "worker_keys": ("volume", "key-prod1"),
                       "api_keys": ("volume", "key-prod1"),
                       "hermes_state": ("volume", "hermes-prod1")},
            "project": "dhole-agent-prod1",
            "worker_service": "dhole-agent-workers-prod1",
            "api_service": "dhole-agent-api-prod1"
        }

    def args(self, tmp, mode, confirmation=""):
        root = Path(tmp)
        (root / "system" / "deploy").mkdir(parents=True, exist_ok=True)
        (root / "system" / "deploy" / "docker-compose.agent-prod1.yml").write_text(
            "services: {}\n")
        (root / "env").write_text("private-config")
        return types.SimpleNamespace(
            generation="prod1", mode=mode, confirmation=confirmation,
            system=str(root / "system"), env_file=str(root / "env"),
            state_dir=str(root / "state"))

    def test_check_mode_never_invalidates_or_modifies_standby(self):
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(os.environ, {"GITHUB_EVENT_NAME": "push",
                                     "GITHUB_REF": "refs/heads/master"}), \
             patch.object(sync, "load_helper", return_value=object()), \
             patch.object(sync, "inspect_state", return_value=self.state(False)), \
             patch.object(sync, "run", return_value="") as runner, \
             patch.object(sync, "invalidate_readiness") as invalidate:
            sync.sync(self.args(tmp, "check"))
            invalidate.assert_not_called()
            self.assertEqual(runner.call_count, 1)
            self.assertIn("config", runner.call_args.args[0])
            self.assertNotIn("up", runner.call_args.args[0])

    def test_rejected_unconfirmed_apply_does_not_mutate(self):
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(os.environ, {"GITHUB_EVENT_NAME": "push",
                                     "GITHUB_REF": "refs/heads/master"}), \
             patch.object(sync, "load_helper") as helper:
            with self.assertRaises(SystemExit):
                sync.sync(self.args(tmp, "apply", "WRONG"))
            helper.assert_not_called()

    def test_refresh_dormant_worker_without_start_or_data_delete(self):
        events = []
        old, new = self.state(False), self.state(True)
        helper = types.SimpleNamespace(
            inspect=lambda p, s: events.append(("inspect", s)) or new["worker"])
        with tempfile.TemporaryDirectory() as tmp, \
             patch.dict(os.environ, {"GITHUB_EVENT_NAME": "push",
                                     "GITHUB_REF": "refs/heads/master"}), \
             patch.object(sync, "load_helper", return_value=helper), \
             patch.object(sync, "inspect_state", side_effect=[old, new]), \
             patch.object(sync, "run", side_effect=lambda a, **k: events.append(("run", a)) or ""), \
             patch.object(sync, "invalidate_readiness",
                          side_effect=lambda *a: events.append(("invalidate", None))), \
             patch.object(sync, "verify_http", side_effect=lambda *a: events.append(("http", None))):
            sync.sync(self.args(tmp, "apply", sync.CONFIRMATION))
            self.assertEqual(events[0][0], "run")  # compose config -q
            self.assertEqual(events[1][0], "invalidate")
            commands = [args for name, args in events if name == "run"]
            self.assertEqual(len(commands), 3)
            self.assertIn("--no-start", commands[1])
            self.assertIn("--no-deps", commands[1])
            self.assertIn("--force-recreate", commands[1])
            self.assertIn("-d", commands[2])
            self.assertNotIn("dhole-agent-workers-prod1", commands[2][-1:])
            self.assertNotIn("down", " ".join(map(str, commands)))
            self.assertNotIn("-v", " ".join(map(str, commands)))
            self.assertEqual(events[-1][0], "http")

    def test_archive_original_attestation_before_database_invalidation(self):
        helper = types.SimpleNamespace(
            parse_db=Mock(return_value={"Database": "dhole_agent"}),
            sql=Mock(return_value="READINESS_INVALIDATED"))
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp) / "state"
            marker = base / "ready" / "prod1.json"
            marker.parent.mkdir(parents=True)
            marker.write_text('{"ready":true}')
            sync.invalidate_readiness(base, "prod1", "/dummy/protected/env", helper)
            self.assertFalse(marker.exists())
            archives = list((marker.parent / "superseded").glob("prod1-*.json"))
            self.assertEqual(len(archives), 1)
            self.assertEqual(archives[0].read_text(), '{"ready":true}')
            self.assertIn("provider_authorized=false", helper.sql.call_args.args[1])

    def test_symlinked_attestation_never_mutated(self):
        with tempfile.TemporaryDirectory() as tmp:
            base = Path(tmp) / "state"
            (base / "ready").mkdir(parents=True)
            destination = base / "keep"
            destination.write_text("do not touch")
            (base / "ready" / "prod1.json").symlink_to(destination)
            helper = types.SimpleNamespace(parse_db=Mock(), sql=Mock())
            with self.assertRaises(SystemExit):
                sync.invalidate_readiness(base, "prod1", "/dummy/env", helper)
            self.assertEqual(destination.read_text(), "do not touch")
            helper.sql.assert_not_called()


if __name__ == "__main__":
    unittest.main()
