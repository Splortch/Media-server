import importlib.util
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[1] / "refresh-nuget-locks.py"
spec = importlib.util.spec_from_file_location("refresh_nuget_locks", SCRIPT)
refresh = importlib.util.module_from_spec(spec)
spec.loader.exec_module(refresh)


class RefreshNuGetLocksTests(unittest.TestCase):
    def test_refresh_covers_consumers_tools_and_every_publish_runtime(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            for name in ("Api", "Services"):
                project = root / "backend/src" / name
                project.mkdir(parents=True)
                (project / (name + ".csproj")).write_text("<Project />", encoding="utf-8")
                (project / "packages.lock.json").write_text('{"version":1}', encoding="utf-8")
            tool = root / "backend/tools/PlaybackBenchmark/PlaybackBenchmark.csproj"
            tool.parent.mkdir(parents=True)
            tool.write_text("<Project />", encoding="utf-8")
            with patch.object(refresh, "ROOT", root), patch.object(refresh, "restore") as restore, patch.object(refresh.subprocess, "run") as run:
                refresh.refresh()
            calls = [call.args for call in restore.call_args_list]
            self.assertEqual(calls[0], ("MediaServer.sln",))
            self.assertIn((tool,), calls)
            for rid in refresh.RIDS:
                self.assertIn(("backend/src/Api/Api.csproj", "-p:RuntimeIdentifier=" + rid), calls)
                for name in ("Api", "Services"):
                    self.assertEqual(
                        (root / "backend/locks" / rid / name / "packages.lock.json").read_text(encoding="utf-8"),
                        '{"version":1}',
                    )
            self.assertEqual(run.call_args_list[0].args[0],
                             ["dotnet", "restore", "MediaServer.sln", "--locked-mode"])
            self.assertEqual(run.call_args_list[1].args[0],
                             ["python3", "scripts/check-release-locks.py"])

    def test_resolver_failures_stop_instead_of_producing_a_successful_bundle(self):
        with patch.object(refresh, "restore", side_effect=RuntimeError("incompatible dependency")), patch.object(refresh.subprocess, "run") as run:
            with self.assertRaisesRegex(RuntimeError, "incompatible"):
                refresh.refresh()
            run.assert_not_called()


if __name__ == "__main__":
    unittest.main()
