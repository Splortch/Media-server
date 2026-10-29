import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

spec = importlib.util.spec_from_file_location("evidence", Path(__file__).parents[1] / "playback-evidence.py")
evidence = importlib.util.module_from_spec(spec)
spec.loader.exec_module(evidence)


class EvidenceTests(unittest.TestCase):
    def check(self, report, hardware=True):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "report.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return evidence.summarize([path], hardware)[0]["passed"]

    def test_software_fallback_is_not_hardware_proof(self):
        self.assertFalse(self.check({"status": "passed", "requestedEncoder": "h264_nvenc",
                                    "actualEncoder": "libx264", "softwareFallback": True}))

    def test_claim_without_runtime_capability_is_not_hardware_proof(self):
        self.assertFalse(self.check({"status": "passed", "requestedEncoder": "h264_amf",
                                    "actualEncoder": "h264_amf", "softwareFallback": False}))

    def test_successful_hardware_probe_and_actual_encoder_are_required(self):
        self.assertTrue(self.check({"status": "passed", "requestedEncoder": "h264_qsv",
                                   "actualEncoder": "h264_qsv", "softwareFallback": False,
                                   "capabilities": [{"encoder": "h264_qsv", "available": True}]}))

    def test_missing_reports_cannot_pass(self):
        with self.assertRaises(ValueError):
            evidence.summarize([])

    def test_failed_conversion_cannot_pass(self):
        self.assertFalse(self.check({"status": "failed"}, False))

    def test_matrix_escapes_untrusted_cells(self):
        self.assertIn("Linux\\|test host", evidence.markdown([{
            "scenario": "sdr", "platform": "Linux|test\nhost", "requested": "libx264",
            "actual": "libx264", "seconds": 1, "passed": True}]))


if __name__ == "__main__":
    unittest.main()
