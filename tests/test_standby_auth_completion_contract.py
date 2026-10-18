"""Contract checks for scope-protected, idempotent standby Hub finalization."""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]
ENDPOINT = ROOT / "src/Dhole.Agent.Api/Endpoints/StandbyAuthenticationEndpoints.cs"


class StandbyCompletionContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.code = ENDPOINT.read_text(encoding="utf-8")
        start = cls.code.index('group.MapPost("/{generation}/complete"')
        end = cls.code.index('private static bool ValidTarget(', start)
        cls.completion = cls.code[start:end]

    def test_fresh_live_hub_required_and_active_generation_excluded(self):
        body = self.completion
        self.assertIn("status='Finishing'", body)
        self.assertIn("status='Ready'", body)
        self.assertIn("requested_at_utc>now()-interval '90 minutes'", body)
        self.assertIn("account_verified_at_utc>now()-interval '5 minutes'", body)
        self.assertIn("owner.active_generation=session.generation", body)
        self.assertIn('request.Confirmation != Confirm', body)
        self.assertIn('RequireScope(AgentScopeNames.BrowserProfilesAuthenticate)', body)
        self.assertNotIn("session.actor_id=@actor", body)

    def test_repeat_clicks_do_not_dispatch_second_finish(self):
        body = self.completion
        self.assertIn('currentStatus == "Finishing"', body)
        self.assertIn('alreadyProcessing = true', body)
        self.assertIn('currentStatus == "Verified"', body)
        self.assertEqual(body.count('DispatchAsync(token, "finish"'), 1)

    def test_specific_safe_diagnostics_for_stale_or_missing_proof(self):
        body = self.completion
        for code in (
            "standby_auth_session_expired",
            "standby_hub_not_verified",
            "standby_hub_proof_expired",
            "standby_authentication_failed",
            "standby_completion_state_changed",
        ):
            self.assertIn(code, body)
        self.assertNotIn('return Results.Conflict(new { code = "standby_auth_session_not_ready_or_wrong_operator" })', body)


if __name__ == "__main__":
    unittest.main()
