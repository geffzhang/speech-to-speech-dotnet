"""Integration tests using real curl and a local, range-aware HTTP server."""

import hashlib
import http.server
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import unittest


ROOT = Path(__file__).resolve().parents[2]
PAYLOAD = b"model fixture\n" * 8192
SHA256 = hashlib.sha256(PAYLOAD).hexdigest()


class ModelHandler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        self.server.requests.append((self.path, self.headers.get("Range")))
        if self.path == "/missing":
            self.send_error(404)
            return
        start = 0
        if self.headers.get("Range") and self.path != "/no-range":
            start = int(self.headers["Range"].removeprefix("bytes=").removesuffix("-"))
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start}-{len(PAYLOAD)-1}/{len(PAYLOAD)}")
        else:
            self.send_response(200)
        self.send_header("Content-Length", str(len(PAYLOAD) - start))
        self.end_headers()
        if self.path == "/interrupt" and len(self.server.requests) == 1:
            self.wfile.write(PAYLOAD[:4096])
            self.wfile.flush()
            self.close_connection = True
        else:
            self.wfile.write(PAYLOAD[start:])

    def log_message(self, *_):
        pass


def runtimes():
    result = []
    powershell = shutil.which("pwsh") or shutil.which("powershell")
    if powershell:
        result.append(("powershell", [
            powershell, "-NoProfile", "-ExecutionPolicy", "Bypass",
            "-File", str(ROOT / "download-models.ps1"),
        ]))
    bash = (
        Path(os.environ.get("ProgramFiles", r"C:\Program Files")) / "Git" / "bin" / "bash.exe"
        if os.name == "nt" else None
    )
    bash = str(bash) if bash and bash.exists() else shutil.which("bash")
    if bash:
        result.append(("bash", [bash, (ROOT / "download-models.sh").as_posix()]))
    return result


class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="s2s models ")
        self.addCleanup(self.temp.cleanup)
        self.base = Path(self.temp.name)
        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), ModelHandler)
        self.server.requests = []
        thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(self.server.server_close)
        self.addCleanup(self.server.shutdown)
        self.url = f"http://127.0.0.1:{self.server.server_port}"

    def run_script(self, name, command, folder, rows=None, endpoint="/model", sha=SHA256, groups=None, env=None):
        manifest = self.base / f"{name}.tsv"
        if rows is None:
            rows = [f"{sha}\t{len(PAYLOAD)}\ttts/nested/model.onnx\t{self.url}{endpoint}"]
        manifest.write_text("# sha256\tbytes\tpath\turl\n" + "\n".join(rows) + "\n", encoding="ascii")
        if name == "powershell":
            args = ["-ModelsDir", str(folder), "-Manifest", str(manifest)]
        else:
            args = ["--models-dir", folder.as_posix(), "--manifest", manifest.as_posix()]
        if groups is not None:
            args.extend(["-Groups" if name == "powershell" else "--groups", groups])
        return subprocess.run(
            command + args, cwd=self.base, env=env, capture_output=True, text=True, timeout=40
        )

    def for_each_runtime(self, scenario):
        available = runtimes()
        self.assertTrue(available, "Install PowerShell or Bash to run downloader tests")
        for name, command in available:
            with self.subTest(runtime=name):
                self.server.requests.clear()
                folder = self.base / name / "model files"
                target = folder / "tts" / "nested" / "model.onnx"
                target.parent.mkdir(parents=True)
                scenario(name, command, folder, target)

    def assert_ok(self, result):
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_downloads_and_skips_only_verified_files(self):
        def scenario(name, command, folder, target):
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertFalse(target.with_suffix(".onnx.part").exists())
            self.assertEqual(1, len(self.server.requests))
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(1, len(self.server.requests), "Verified cache must not use the network")
        self.for_each_runtime(scenario)

    def test_resumes_partial_file(self):
        def scenario(name, command, folder, target):
            Path(str(target) + ".part").write_bytes(PAYLOAD[:4096])
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertEqual([("/model", "bytes=4096-")], self.server.requests)
        self.for_each_runtime(scenario)

    def test_promotes_completed_partial_without_network(self):
        def scenario(name, command, folder, target):
            Path(str(target) + ".part").write_bytes(PAYLOAD)
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertEqual([], self.server.requests)
        self.for_each_runtime(scenario)

    def test_restarts_if_server_ignores_range(self):
        def scenario(name, command, folder, target):
            Path(str(target) + ".part").write_bytes(PAYLOAD[:4096])
            self.assert_ok(self.run_script(name, command, folder, endpoint="/no-range"))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertIn(("/no-range", None), self.server.requests)
        self.for_each_runtime(scenario)

    def test_repairs_corrupt_final_file(self):
        def scenario(name, command, folder, target):
            target.write_bytes(b"x" * len(PAYLOAD))
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(PAYLOAD, target.read_bytes())
        self.for_each_runtime(scenario)

    def test_restarts_corrupt_complete_partial(self):
        def scenario(name, command, folder, target):
            Path(str(target) + ".part").write_bytes(b"x" * len(PAYLOAD))
            self.assert_ok(self.run_script(name, command, folder))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertEqual([("/model", None)], self.server.requests)
        self.for_each_runtime(scenario)

    def test_checksum_failure_never_publishes_and_discards_bad_partial(self):
        def scenario(name, command, folder, target):
            result = self.run_script(name, command, folder, sha="0" * 64)
            self.assertNotEqual(0, result.returncode)
            self.assertIn("SHA-256", result.stdout + result.stderr)
            self.assertFalse(target.exists())
            self.assertFalse(Path(str(target) + ".part").exists())
        self.for_each_runtime(scenario)

    def test_http_failure_preserves_existing_files(self):
        def scenario(name, command, folder, target):
            target.write_bytes(b"old")
            partial = Path(str(target) + ".part")
            partial.write_bytes(PAYLOAD[:4096])
            result = self.run_script(name, command, folder, endpoint="/missing")
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(b"old", target.read_bytes())
            self.assertEqual(PAYLOAD[:4096], partial.read_bytes())
        self.for_each_runtime(scenario)

    def test_interrupted_download_is_resumable_on_next_run(self):
        def scenario(name, command, folder, target):
            first = self.run_script(name, command, folder, endpoint="/interrupt")
            self.assertNotEqual(0, first.returncode)
            self.assertFalse(target.exists())
            partial = Path(str(target) + ".part")
            self.assertEqual(PAYLOAD[:4096], partial.read_bytes())
            self.assert_ok(self.run_script(name, command, folder, endpoint="/interrupt"))
            self.assertEqual(PAYLOAD, target.read_bytes())
            self.assertEqual(("/interrupt", "bytes=4096-"), self.server.requests[-1])
        self.for_each_runtime(scenario)

    def test_rejects_bad_manifest_before_any_download(self):
        def scenario(name, command, folder, target):
            valid = f"{SHA256}\t{len(PAYLOAD)}\tok.onnx\t{self.url}/model"
            bad_rows = [
                f"{SHA256}\t{len(PAYLOAD)}\t../escaped.onnx\t{self.url}/model",
                f"{SHA256}\t{len(PAYLOAD)}\t/root.onnx\t{self.url}/model",
                f"not-a-hash\t1\tbad.onnx\t{self.url}/model",
                f"{SHA256}\t0\tbad.onnx\t{self.url}/model",
                f"{SHA256}\t1\tbad.onnx\tfile:///tmp/model",
                valid,
                f"{SHA256}\t1\tok.onnx.part\t{self.url}/model",
                f"{SHA256}\t1\tok.onnx/child\t{self.url}/model",
                f"{SHA256}\t1\tOK.ONNX\t{self.url}/model",
                f"{SHA256}\t1\tbad.onnx\t{self.url}/model\textra",
            ]
            for bad in bad_rows:
                result = self.run_script(name, command, folder, rows=[valid, bad])
                self.assertNotEqual(0, result.returncode, bad)
                self.assertEqual([], self.server.requests, bad)
                self.assertFalse((folder / "ok.onnx").exists())
            result = self.run_script(name, command, folder, rows=[])
            self.assertNotEqual(0, result.returncode)
        self.for_each_runtime(scenario)

    def test_group_filter_downloads_only_selected_group(self):
        def scenario(name, command, folder, target):
            rows = [
                f"{SHA256}\t{len(PAYLOAD)}\t{group}/one.onnx\t{self.url}/model"
                for group in ("vad", "whisper", "tts")
            ]
            result = self.run_script(name, command, folder, rows=rows, groups="vad,tts")
            self.assert_ok(result)
            self.assertEqual(PAYLOAD, (folder / "vad" / "one.onnx").read_bytes())
            self.assertEqual(PAYLOAD, (folder / "tts" / "one.onnx").read_bytes())
            self.assertFalse((folder / "whisper" / "one.onnx").exists())
            self.assertEqual(["/model", "/model"], [path for path, _ in self.server.requests])
        self.for_each_runtime(scenario)

    def test_group_filter_rejects_empty_unknown_and_duplicate_groups(self):
        def scenario(name, command, folder, target):
            rows = [
                f"{SHA256}\t{len(PAYLOAD)}\t{group}/one.onnx\t{self.url}/model"
                for group in ("vad", "whisper")
            ]
            for groups in ("", "unknown", "vad,vad"):
                self.server.requests.clear()
                result = self.run_script(name, command, folder, rows=rows, groups=groups)
                self.assertNotEqual(0, result.returncode, groups)
                self.assertIn("invalid group filter", (result.stdout + result.stderr).lower(), groups)
                self.assertEqual([], self.server.requests, groups)
                self.assertFalse((folder / "vad" / "one.onnx").exists(), groups)
                self.assertFalse((folder / "whisper" / "one.onnx").exists(), groups)
        self.for_each_runtime(scenario)

    @unittest.skipUnless(os.name == "nt", "PowerShell executable resolution is Windows-specific")
    def test_powershell_downloads_when_multiple_curl_executables_are_on_path(self):
        powershell = next((command for name, command in runtimes() if name == "powershell"), None)
        self.assertIsNotNone(powershell, "PowerShell is required for this Windows test")
        curl = shutil.which("curl.exe")
        self.assertIsNotNone(curl, "curl.exe is required")
        duplicate_dir = self.base / "second-curl"
        duplicate_dir.mkdir()
        shutil.copy2(curl, duplicate_dir / "curl.exe")
        env = os.environ.copy()
        env["PATH"] = os.pathsep.join([str(duplicate_dir), os.path.dirname(curl), env["PATH"]])
        probe = subprocess.run(
            [powershell[0], "-NoProfile", "-Command", "(Get-Command curl.exe -CommandType Application).Count"],
            cwd=self.base, env=env, capture_output=True, text=True, timeout=20,
        )
        self.assertEqual(0, probe.returncode, probe.stdout + probe.stderr)
        self.assertGreaterEqual(int(probe.stdout.strip()), 2)

        model = self.base / "powershell-multiple-curl" / "vad" / "model.onnx"
        folder = model.parents[1]
        rows = [f"{SHA256}\t{len(PAYLOAD)}\tvad/model.onnx\t{self.url}/model"]
        result = self.run_script("powershell", powershell, folder, rows=rows, env=env)
        self.assert_ok(result)
        self.assertEqual(PAYLOAD, model.read_bytes())


class ManifestTests(unittest.TestCase):
    def test_default_manifest_covers_complete_pipeline_and_pins_urls(self):
        manifest = ROOT / "tools" / "models" / "models.tsv"
        self.assertTrue(manifest.is_file(), "The default URL/checksum inventory must be shipped")
        entries = [
            line.split("\t") for line in manifest.read_text(encoding="ascii").splitlines()
            if line and not line.startswith("#")
        ]
        files = {}
        for sha, size, path, url in entries:
            self.assertRegex(sha, r"^[a-f0-9]{64}$")
            self.assertGreater(int(size), 0)
            self.assertRegex(url, r"^https://[^ ]+/resolve/[a-f0-9]{40}/")
            self.assertNotIn(path.lower(), files)
            files[path.lower()] = (sha, int(size))
        required = {
            "vad/silero_vad.onnx",
            "whisper/conf.json", "whisper/encoder.int8.onnx", "whisper/decoder.int8.onnx",
            "llm/qwen3.5-0.8b-q4_k_m.gguf",
            "smart-turn/smart-turn-v3.2-cpu.onnx",
            "tts/talker_prefill.onnx", "tts/talker_prefill.onnx.data",
            "tts/talker_decode.onnx", "tts/talker_decode.onnx.data",
            "tts/code_predictor.onnx", "tts/vocoder.onnx", "tts/vocoder.onnx.data",
            "tts/tokenizer/vocab.json", "tts/tokenizer/merges.txt",
            "tts/embeddings/config.json", "tts/embeddings/speaker_ids.json",
            "tts/embeddings/codec_head_weight.npy",
            "tts/embeddings/talker_codec_embedding.npy", "tts/embeddings/text_embedding.npy",
            "tts/embeddings/text_projection_fc1_weight.npy",
            "tts/embeddings/text_projection_fc1_bias.npy",
            "tts/embeddings/text_projection_fc2_weight.npy",
            "tts/embeddings/text_projection_fc2_bias.npy",
        } | {f"tts/embeddings/cp_codec_embedding_{i}.npy" for i in range(15)}
        self.assertEqual(required, files.keys())


if __name__ == "__main__":
    unittest.main()
