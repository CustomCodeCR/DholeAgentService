import importlib.util
import pathlib
import unittest
from unittest.mock import patch

MODULE_PATH = pathlib.Path(__file__).resolve().parents[1] / "scripts/retire-production-generation.py"
spec = importlib.util.spec_from_file_location("retire_generation", MODULE_PATH)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class RetirementSafetyTests(unittest.TestCase):
    def test_source_must_be_numbered(self):
        with self.assertRaises(module.RetirementRefused):
            module.generation_number("prod")
        with self.assertRaises(module.RetirementRefused):
            module.generation_number("prod2; rm")
        self.assertEqual(2, module.generation_number("prod2"))

    def test_allowlist_contains_only_retired_volumes(self):
        _, _, volumes = module.names("prod1")
        self.assertIn("dhole_agent_prod1_keys", volumes)
        self.assertNotIn("dhole_agent_prod2_keys", volumes)
        _, _, volumes2 = module.names("prod2")
        self.assertNotIn("dhole_agent_prod3_keys", volumes2)

    def test_volume_with_foreign_owner_refused(self):
        mocked = {"Mounts": [{"Type": "volume", "Name": "dhole_agent_prod1_keys"}]}
        with patch.object(module, "docker", return_value="other-container"), patch.object(module, "inspect", return_value=mocked):
            with self.assertRaises(module.RetirementRefused):
                module.confirm_no_other_owners({"dhole_agent_prod1_keys"}, [])

    def test_profile_cleanup_scoped_and_global_circuit_preserved(self):
        queries = []
        module.delete_generation_rows({}, "prod1", lambda db, query, **kw: queries.append(query))
        sql = queries[0]
        self.assertIn("DELETE FROM agent.\"BrowserProfiles\"", sql)
        self.assertIn("profile_key LIKE 'maersk-prod1-%'", sql)
        self.assertIn("generation='prod1'", sql)
        self.assertNotIn("DELETE FROM agent.maersk_circuits", sql)
        self.assertNotIn("DELETE FROM agent.\"AgentCredentials\"", sql)


if __name__ == "__main__":
    unittest.main()
