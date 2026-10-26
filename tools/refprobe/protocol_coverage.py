"""Audit which Realtime server events each side can emit.

Upstream: reflected from the `ServerEvent` union in
`src/speech_to_speech/api/openai_realtime/service.py`.

Ours: parsed from the `EventNames` constants in
`src/SpeechToSpeech.Realtime/Protocol/ServerEvent.cs`. The class name in the
constant's declaration is not needed — a constant existing means the name is
known, but *emitting* it needs a factory, so we also look for a `ServerEvent.Xxx(`
call site or a `ServerEvent` factory declaration.

Run from the repository root:
  python tools/refprobe/protocol_coverage.py [--repo <upstream>]
"""

from __future__ import annotations

import argparse
import pathlib
import re
import subprocess
import sys

OUR_EVENT_NAMES = pathlib.Path("src/SpeechToSpeech.Realtime/Protocol/ServerEvent.cs")

# 上游自定义扩展（非 OpenAI 官方事件），单独标注
_EXTENSION_PREFIX = "speech_to_speech."


def upstream_events(repo: pathlib.Path, python: str) -> dict[str, str]:
    """Return {wire type: model class} for the upstream ServerEvent union."""
    script = """
import sys, json, typing
sys.path.insert(0, 'src')
from speech_to_speech.api.openai_realtime.service import ServerEvent
out = {}
for m in typing.get_args(ServerEvent):
    f = m.model_fields.get('type')
    lit = typing.get_args(f.annotation)
    if lit and isinstance(lit[0], str):
        out[lit[0]] = m.__name__
print(json.dumps(out))
"""
    result = subprocess.run(
        [python, "-c", script],
        cwd=repo, capture_output=True, text=True, encoding="utf-8",
    )
    if result.returncode != 0:
        raise SystemExit(f"反射上游 ServerEvent 失败：\n{result.stderr[-2000:]}")
    import json
    return json.loads(result.stdout)


def our_events(source: pathlib.Path) -> tuple[set[str], set[str]]:
    """Return (declared wire types, ones with a construction path)."""
    text = source.read_text(encoding="utf-8")
    declared = dict(re.findall(r'public const string (\w+) = "([^"]+)";', text))

    # 工厂可以定义在 ServerEvent.cs（`public static JsonObject Xxx(`），
    # 也可以只在别处被调用（`ServerEvent.Xxx(`）——两处都要找。
    roots = {source.parent.parent.parent.parent}
    corpus = text
    for root in roots:
        for file in root.rglob("*.cs"):
            if "obj" in file.parts or "bin" in file.parts:
                continue
            corpus += file.read_text(encoding="utf-8", errors="ignore")

    built = {
        wire
        for name, wire in declared.items()
        if re.search(rf"(public static JsonObject|ServerEvent\.){name}\s*\(", corpus)
    }
    return set(declared.values()), built


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--repo", default="C:/workshop/github/speech-to-speech")
    parser.add_argument(
        "--python",
        default="C:/Users/geffzhang/.workbuddy/binaries/python/envs/default/Scripts/python.exe",
        help="装了 openai / numpy / scipy 的解释器，用来反射上游模块",
    )
    args = parser.parse_args()

    theirs = upstream_events(pathlib.Path(args.repo), args.python)
    declared, built = our_events(OUR_EVENT_NAMES)

    missing = sorted(set(theirs) - built)
    extra = sorted(built - set(theirs))
    aligned = sorted(set(theirs) & built)

    print(f"上游可发 {len(theirs)} 种 / 本实现已声明 {len(declared)} 种、可构造 {len(built)} 种")
    print(f"\n已对齐（{len(aligned)}）")
    for wire in aligned:
        print(f"  ✓ {wire}")

    core_missing = [w for w in missing if not w.startswith(_EXTENSION_PREFIX)]
    ext_missing = [w for w in missing if w.startswith(_EXTENSION_PREFIX)]
    print(f"\n上游会发、本实现不发（{len(core_missing)}）—— 客户端渲染依赖这些")
    for wire in core_missing:
        print(f"  ✗ {wire}  ({theirs[wire]})")
    if ext_missing:
        print(f"\n扩展事件缺失（{len(ext_missing)}）")
        for wire in ext_missing:
            print(f"  ✗ {wire}")

    if extra:
        print(f"\n本实现独有（{len(extra)}）")
        for wire in extra:
            print(f"  + {wire}")

    print(f"\n覆盖 {len(aligned)}/{len(theirs)} = {len(aligned) * 100 // len(theirs)}%")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
