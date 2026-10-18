"""Only API-authorized, currently Ready standby Hubs may be auto-attested."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "standby_hub_auto_inspect", ROOT / "scripts/standby-hub-auto-inspect.py")
auto = importlib.util.module_from_spec(spec)
spec.loader.exec_module(auto)


class AutoStandbyHubTests(unittest.TestCase):
    def test_uses_fresh_ready_authorizations_only(self):
        with patch.object(auto.apply, "sql", return_value=(
                "prod2|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"
                "prod3|bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\n"
                "prod1|aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n"
                "prod201|cccccccccccccccccccccccccccccccc\n"
                "prod4|not-a-credential")) as sql:
            candidates = auto.ready_candidates({})
            self.assertEqual(candidates, [
                ("prod2", "a"*32), ("prod3", "b"*32)])
            self.assertIn("status='Ready'", sql.call_args.args[1])
            self.assertIn("account_verified_at_utc IS NULL", sql.call_args.args[1])
            self.assertIn("requested_at_utc >", sql.call_args.args[1])
            self.assertNotIn("status='Verified'", sql.call_args.args[1])

    def test_inspection_failure_never_mutates_state(self):
        with patch.object(auto.apply, "parse_db", return_value={}), \
             patch.object(auto, "ready_candidates", return_value=[
                 ("prod2", "a"*32)]), \
             patch.object(auto, "inspect_once",
                          side_effect=RuntimeError("standby_hub_not_authenticated")):
            auto.run("private.env")

    def test_only_live_proof_records_database_state(self):
        with patch.object(auto.apply, "parse_db", return_value={}), \
             patch.object(auto, "ready_candidates", return_value=[
                 ("prod3", "b"*32)]), \
             patch.object(auto, "inspect_once") as inspect:
            auto.run("private.env")
            inspect.assert_called_once_with("private.env", "prod3", "b"*32)


if __name__ == "__main__":
    unittest.main()
