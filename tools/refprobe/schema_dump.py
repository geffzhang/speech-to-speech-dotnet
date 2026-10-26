"""Dump the official OpenAI Realtime server-event schema as JSON.

The schema is the authority for the protocol layer: it is the model the
official clients parse responses with, and the upstream project's own
contract tests validate emitted events against it (`assert_openai_schema`
in tests/openai_realtime/realtime_contract.py).

Output shape:
  {
    "sdk": "openai 2.28.0",
    "events": {
      "<wire type>": {
        "class": "...",
        "required": ["..."],
        "optional": ["..."],
        "fields": {"<name>": "<annotation>"}
      },
      ...
    }
  }

Usage:
  python schema_dump.py            > schema.json
  python schema_dump.py --compare  # 与本实现的导出逐条比对
"""

from __future__ import annotations

import argparse
import json
import sys
import typing

import openai


def _union_members(alias: object) -> list[type]:
    """Flatten ``Annotated[Union[...], PropertyInfo(...)]`` into bare model classes."""
    args = typing.get_args(alias)
    if not args:
        return []
    head = args[0]
    # Annotated 的第一项才是真正的联合；第二项是 PropertyInfo 判别器元数据
    if typing.get_origin(head) is not None:
        return [m for m in typing.get_args(head) if isinstance(m, type) and hasattr(m, "model_fields")]
    if isinstance(head, type) and hasattr(head, "model_fields"):
        return [head]
    return []


def _server_event_types() -> dict[str, type]:
    """Return {wire type: model class} for every server event the SDK knows."""
    import openai.types.realtime as rt

    union = rt.RealtimeServerEvent
    out: dict[str, type] = {}
    for member in _union_members(union):
        # 联合类型里可能嵌着 Annotated / 嵌套 Union，只处理真正的模型类
        if not (isinstance(member, type) and hasattr(member, "model_fields")):
            continue
        literal = _wire_type(member)
        if literal is None:
            continue
        # 同一 type 有多态时，取字段最少的（最宽松）那个作为形状基准
        existing = out.get(literal)
        if existing is None or len(member.model_fields) < len(existing.model_fields):
            out[literal] = member
    return out


def _wire_type(model: type) -> str | None:
    """Extract the Literal value of the model's `type` field."""
    field = model.model_fields.get("type")
    if field is None:
        return None
    annotation = field.annotation
    args = typing.get_args(annotation)
    if args and isinstance(args[0], str):
        return args[0]
    if field.default and isinstance(field.default, str):
        return field.default
    return None


def _annotation_text(annotation: object) -> str:
    return str(annotation).replace("typing.", "").replace("openai.types.realtime.", "")


def build() -> dict:
    events: dict[str, dict] = {}
    for wire, model in sorted(_server_event_types().items()):
        required: list[str] = []
        optional: list[str] = []
        fields: dict[str, str] = {}
        for name, info in model.model_fields.items():
            fields[name] = _annotation_text(info.annotation)
            if info.is_required():
                required.append(name)
            else:
                optional.append(name)
        events[wire] = {
            "class": model.__name__,
            "required": sorted(required),
            "optional": sorted(optional),
            "fields": dict(sorted(fields.items())),
        }
    return {"sdk": f"openai {openai.__version__}", "events": events}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--compare", metavar="OURS", help="本实现导出的 schema JSON 路径")
    parser.add_argument("--indent", type=int, default=2)
    args = parser.parse_args()

    spec = build()
    if not args.compare:
        json.dump(spec, sys.stdout, ensure_ascii=False, indent=args.indent, sort_keys=True)
        sys.stdout.write("\n")
        return 0

    with open(args.compare, encoding="utf-8") as handle:
        ours = json.load(handle)

    theirs = spec["events"]
    mine = ours["events"]
    missing = sorted(set(theirs) - set(mine))
    extra = sorted(set(mine) - set(theirs))

    print(f"官方 {len(theirs)} 种 / 本实现 {len(mine)} 种")
    if missing:
        print(f"\n本实现缺少（{len(missing)}）:")
        for wire in missing:
            print(f"  - {wire}  ({theirs[wire]['class']})")
    if extra:
        print(f"\n本实现独有（{len(extra)}）:")
        for wire in extra:
            print(f"  - {wire}")

    drift = 0
    for wire in sorted(set(theirs) & set(mine)):
        req = set(theirs[wire]["required"])
        got = set(mine[wire].get("present", []))
        for name in sorted(req - got):
            print(f"  ! {wire}: 缺必填字段 {name} ({theirs[wire]['fields'][name]})")
            drift += 1
    print(f"\n字段缺口 {drift} 处")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
