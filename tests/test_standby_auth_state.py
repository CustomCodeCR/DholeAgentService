"""Guard standby-state reconciliation without mutating provider sessions."""
import importlib.util
import json
from pathlib import Path
from types import SimpleNamespace
import unittest
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location(
    "standby_auth_state", ROOT / "scripts/standby-auth-state.py")
state = importlib.util.module_from_spec(spec)
spec.loader.exec_module(state)

CREDENTIAL = "a" * 32


def live_viewer():
    return SimpleNamespace(returncode=0, stdout=json.dumps([{
        "Config": {
            "Labels": {
                "dhole.standby.auth.generation": "prod2",
                "dhole.standby.auth.credential": CREDENTIAL,
            },
            "Image": "dhole/agent-workers:standby-inspector-e4168dc0d896",
        },
        "State": {"Status": "running"},
    }]))


class StandbyAuthStateTests(unittest.TestCase):
    def test_healthy_failed_session_can_be_rearmed_without_alter_table(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", side_effect=["4", "Failed", "prod2"]) as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            state.restart_ready("unused.env", "prod2", CREDENTIAL)
            queries = [call.args[1] for call in sql.call_args_list]
            self.assertEqual(len(queries), 3)
            self.assertTrue(queries[0].lstrip().startswith("SELECT"))
            self.assertIn("status='Ready'", queries[-1])
            self.assertNotIn("ALTER TABLE", "\n".join(queries))
            self.assertIn("status IN ('Requested','Preparing','Ready','Failed')", queries[-1])
            self.assertIn("requested_at_utc > now() - interval '90 minutes'", queries[-1])
            self.assertNotIn("requested_at_utc=now()", queries[-1])

    def test_recent_requested_session_is_reconciled_after_healthy_viewer(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", side_effect=["4", "Requested", "prod2"]) as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            state.restart_ready("unused.env", "prod2", CREDENTIAL)
            self.assertEqual(sql.call_count, 3)
            self.assertIn("requested_at_utc >", sql.call_args.args[1])

    def test_expired_request_requires_new_api_authorization(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", side_effect=["4", "Expired"]) as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            with self.assertRaisesRegex(ValueError, "standby_auth_session_expired"):
                state.restart_ready("unused.env", "prod2", CREDENTIAL)
            self.assertEqual(sql.call_count, 2)

    def test_reconciliation_checks_request_age_in_database(self):
        with patch.object(state.apply, "sql", return_value="Expired") as sql:
            self.assertEqual(
                state.get_session_status({}, "prod2", CREDENTIAL), "Expired")
            query = sql.call_args.args[1]
            self.assertIn("requested_at_utc <= now() - interval '90 minutes'", query)

    def test_missing_authorized_request_does_not_mutate_state(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", side_effect=["4", "Missing"]) as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            with self.assertRaisesRegex(ValueError, "standby_auth_session_missing"):
                state.restart_ready("unused.env", "prod2", CREDENTIAL)
            self.assertEqual(sql.call_count, 2)

    def test_verified_session_cannot_be_rearmed_as_ready(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", side_effect=["4", "Verified"]) as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            # A previously verified identity must never be overwritten by
            # reconciliation. The refusal is now intentionally state-specific.
            with self.assertRaisesRegex(ValueError, "standby_auth_session_verified"):
                state.restart_ready("unused.env", "prod2", CREDENTIAL)
            self.assertEqual(sql.call_count, 2)

    def test_hub_proof_is_persisted_without_personal_identity_fields(self):
        import os
        import tempfile
        with tempfile.TemporaryDirectory() as root:
            proof_file = Path(root) / "proof.json"
            proof_file.write_text(json.dumps({"HubAuthenticated": True}))
            proof_file.chmod(0o600)
            with patch.object(state.apply, "parse_db", return_value={}), \
                 patch.object(state.apply, "sql", side_effect=["4", "Ready", "prod3"]) as sql:
                state.record_identity("unused.env", "prod3", CREDENTIAL, proof_file)
                query = sql.call_args.args[1]
                self.assertIn("account_verified_at_utc=now()", query)
                self.assertIn("account_name=NULL", query)
                self.assertIn("account_username=NULL", query)
                self.assertIn("account_customer_id=NULL", query)
                self.assertIn("generation='prod3'", query)

    def test_hub_proof_cannot_be_faked_with_url_or_false_value(self):
        import tempfile
        for invalid in ({"HubAuthenticated": False}, {"Url": "https://maersk.com/hub"},
                        {"HubAuthenticated": True, "Username": "fake"}):
            with tempfile.TemporaryDirectory() as root:
                proof_file = Path(root) / "proof.json"
                proof_file.write_text(json.dumps(invalid))
                proof_file.chmod(0o600)
                with self.assertRaisesRegex(ValueError, "invalid_hub_proof"):
                    state.record_identity("unused.env", "prod2", CREDENTIAL, proof_file)

    def test_identity_probe_failure_is_visible_without_revoking_ready_session(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", return_value="prod2") as sql:
            state.record_inspection_failure(
                "unused.env", "prod2", CREDENTIAL,
                "standby_hub_not_authenticated")
            query = sql.call_args.args[1]
            self.assertIn("diagnostic_code=", query)
            self.assertIn("status='Ready'", query)
            self.assertIn("account_verified_at_utc IS NULL", query)
            self.assertNotIn("status='Failed'", query)
            self.assertNotIn("status='Verified'", query)

    def test_identity_probe_diagnostic_rejects_untrusted_provider_text(self):
        with self.assertRaisesRegex(ValueError, "invalid_inspection_diagnostic"):
            state.record_inspection_failure(
                "unused.env", "prod2", CREDENTIAL,
                "name=someone@example.com")

    def test_missing_migration_is_reported_without_alter_table(self):
        with patch.object(state.apply, "parse_db", return_value={}), \
             patch.object(state.apply, "sql", return_value="2") as sql, \
             patch.object(state.subprocess, "run",
                          side_effect=[live_viewer(), SimpleNamespace(returncode=0)]):
            with self.assertRaisesRegex(ValueError, "standby_auth_schema_missing"):
                state.restart_ready("unused.env", "prod2", CREDENTIAL)
            self.assertEqual(sql.call_count, 1)
            self.assertNotIn("ALTER TABLE", sql.call_args.args[1])


if __name__ == "__main__":
    unittest.main()
