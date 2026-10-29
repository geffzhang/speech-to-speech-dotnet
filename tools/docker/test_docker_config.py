import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import yaml


ROOT = Path(__file__).resolve().parents[2]
ENTRYPOINT = ROOT / "docker-entrypoint.sh"
GIT_BASH = Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "bin" / "bash.exe"


class DockerEntrypointTests(unittest.TestCase):
    def run_entrypoint(self, download_exit=0, model_dir=None):
        with tempfile.TemporaryDirectory(prefix="s2s-docker-entrypoint-") as temp:
            temp_path = Path(temp)
            log = temp_path / "calls.log"
            downloader = temp_path / "downloader"
            downloader.write_text(
                "#!/usr/bin/env bash\n"
                f"printf 'download:%s\\n' \"$*\" >> '{log}'\n"
                f"exit {download_exit}\n",
                encoding="utf-8",
            )
            downloader.chmod(0o755)
            bindir = temp_path / "bin"
            bindir.mkdir()
            dotnet = bindir / "dotnet"
            dotnet.write_text(
                "#!/usr/bin/env bash\n"
                f"printf 'dotnet:%s\\n' \"$*\" >> '{log}'\n",
                encoding="utf-8",
            )
            dotnet.chmod(0o755)
            env = os.environ.copy()
            env["PATH"] = f"{bindir}{os.pathsep}{env['PATH']}"
            env["MODEL_DOWNLOADER"] = str(downloader)
            if model_dir is not None:
                env["MODEL_DIR"] = model_dir
            result = subprocess.run(
                [
                    str(GIT_BASH) if GIT_BASH.exists() else "bash",
                    ENTRYPOINT.as_posix(),
                    "serve",
                ],
                cwd=ROOT,
                env=env,
                capture_output=True,
                text=True,
                check=False,
            )
            calls = log.read_text(encoding="utf-8").splitlines() if log.exists() else []
            return result, calls

    def test_downloads_models_before_starting_application(self):
        result, calls = self.run_entrypoint()

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            [
                "download:--models-dir /models --manifest "
                "/usr/local/share/speech-to-speech/models.tsv",
                "dotnet:/app/speech-to-speech.dll serve",
            ],
            calls,
        )

    def test_downloader_failure_prevents_application_start(self):
        result, calls = self.run_entrypoint(download_exit=23)

        self.assertEqual(23, result.returncode)
        self.assertEqual(
            [
                "download:--models-dir /models --manifest "
                "/usr/local/share/speech-to-speech/models.tsv",
            ],
            calls,
        )

    def test_custom_model_directory_is_passed_to_downloader(self):
        result, calls = self.run_entrypoint(model_dir="/custom/models")

        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(
            "download:--models-dir /custom/models --manifest "
            "/usr/local/share/speech-to-speech/models.tsv",
            calls[0],
        )


class DockerComposeTests(unittest.TestCase):
    def test_pipeline_uses_persistent_volume_and_local_llm(self):
        config = yaml.safe_load((ROOT / "docker-compose.yml").read_text(encoding="utf-8"))
        pipeline = config["services"]["pipeline"]
        args = pipeline["command"]

        model_mount = next(mount for mount in pipeline["volumes"] if mount.endswith(":/models"))
        self.assertEqual("models:/models", model_mount)
        self.assertIn("models", config["volumes"])
        self.assertEqual("tensorsharp", args[args.index("--llm_backend") + 1])
        self.assertEqual(
            "/models/llm/Qwen3.5-0.8B-Q4_K_M.gguf",
            args[args.index("--llm_model_path") + 1],
        )
        self.assertNotIn("extra_hosts", pipeline)
        self.assertNotIn("host.docker.internal:8080", args)


if __name__ == "__main__":
    unittest.main()
