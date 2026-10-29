import importlib.util
from pathlib import Path
import unittest

root = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location("container_bases", root / "scripts/check-container-bases.py")
policy = importlib.util.module_from_spec(spec)
spec.loader.exec_module(policy)


class ContainerBaseTests(unittest.TestCase):
    def setUp(self):
        self.text = (root / "docker/Dockerfile").read_text()

    def test_real_multistage_file_has_three_immutable_external_bases(self):
        self.assertEqual(3, len(policy.verify(self.text)))

    def test_removing_digest_fails(self):
        import re
        with self.assertRaises(ValueError):
            policy.verify(re.sub(r"@sha256:[a-f0-9]{64}", "", self.text, count=1))

    def test_unreviewed_registry_fails(self):
        with self.assertRaises(ValueError):
            policy.verify(self.text.replace("node:26-", "unreviewed.example/node:26-"))

    def test_missing_base_fails(self):
        with self.assertRaises(ValueError):
            policy.verify("FROM scratch\n")

    def test_latest_tag_fails_even_with_digest(self):
        with self.assertRaises(ValueError):
            policy.verify(self.text.replace("node:26-bookworm-slim", "node:latest"))

    def test_long_invalid_repository_is_rejected_without_ambiguous_matching(self):
        with self.assertRaises(ValueError):
            policy.verify(self.text.replace("node:26-bookworm-slim", ("x/" * 10000) + "node:bad!"))

    def test_malformed_digest_fails(self):
        with self.assertRaises(ValueError):
            policy.verify(self.text.replace("sha256:", "sha512:", 1))


if __name__ == "__main__":
    unittest.main()
