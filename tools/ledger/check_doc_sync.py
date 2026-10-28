#!/usr/bin/env python3
"""账本状态与文档措辞的一致性检查。

`porting/differences.jsonl` 是唯一事实来源，四本账是它的叙述层。状态翻成 resolved
之后，叙述层里「未移植 / 不可用 / 是缺口」这类话必须同步改掉——否则读文档的人会据此
判定某项能力不存在，而代码里其实已经有了。

这类漂移是静默的：72 条差异 × 4 本账，没人会每次手工对一遍。2026-10-27 那次一次性
查出 9 处（WebRTC 已移植却写着未移植、Dockerfile 已建却写着无镜像、两个流式 STT 后端
已实现 47 例测试却写着不可用……），说明靠人读不成立，所以固化成命令。

两个方向都查：
  正向  账本 resolved，文档仍写未完成  → 读者会低估实现
  反向  账本 accepted/gap，文档写已解决 → 读者会高估实现

豁免：行内出现 <!-- doc-sync: ignore --> 即跳过。用于历史叙述——「当时写成 X，
后来改掉」这类句子本来就要保留原话。

不检查 02-differences.md：它是 diff_register.py render 生成的，问题描述列写的
本来就是缺陷原貌（「仅 WebSocket 传输，无 WebRTC」配状态列「已解决」是正常形态）。

除了 porting/ 四本账，还扫仓库里各子目录的 README：第一次只扫 porting/ 时，
`corpus/protocol/README.md` 里的「事件级覆盖 25/26」就漏掉了（实际早已 26/26）。

2026-10-28 又补一类：**jsonl 条目自身的措辞**。C029/D008/D027 的教训是——accepted/resolved
条目的自述（title/what/decision）也会过时：title 里残留「（gap）」状态标注、decision 里说
「仍是缺口，单列 D0xx」而 D0xx 早已解决、test 里声称的用例数与实际不符。md 正文查不到这些，
因为它们只存在于账本本身。分两级：
  错误   title 带状态标注残留、条目 status=gap/accepted 而 decision 自称已解决/已补齐
  复核   条目正文指向其他条目「仍是缺口/尚未解决」而被指向条目已 resolved——多半要补
         「（现已 resolved）」时间限定；title 描述问题原貌（「未移植」配 resolved）是惯例，不报
"""

from __future__ import annotations

import argparse
import io
import json
import re
import sys
from pathlib import Path

# render 生成，问题描述列刻意保留缺陷原貌
SKIP_FILES = {"02-differences.md"}

# 未完成类措辞。「未」与「没」两族都要认——同义不同字，漏一族就等于漏一半
NEGATIVE = re.compile(
    r"未移植|未实现|未做|未接|未记|尚未|不支持|整个不发|是缺口|不可用|没移植|没实现|没做|没有实现"
)
# 已对齐类措辞（同句出现则不算矛盾）
POSITIVE = re.compile(r"已解决|已移植|已补|已发|已记|已接|已跑|已闭合|已入库|不再是|已实现|已对齐")
# 反向检查：已经写明「已接受 / 尚未 / 待」，就不是「谎称已解决」
NOT_CLAIMED = re.compile(r"accepted|已接受|尚未|待")

IGNORE_MARK = "<!-- doc-sync: ignore -->"

ID = re.compile(r"\b([A-Z]\d{3})\b")


def load_status(diff_path: Path) -> dict[str, str]:
    rows: dict[str, str] = {}
    for line in io.open(diff_path, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        rows[row["id"]] = row.get("status", "")
    return rows


def check_line(text: str, status: str) -> str | None:
    """返回问题类型，None 表示这句与账本状态不冲突。"""
    if status == "resolved":
        if NEGATIVE.search(text) and not POSITIVE.search(text):
            return "文档写未完成，账本已 resolved"
    else:
        if re.search(r"已解决|已移植|已入库", text) and not NOT_CLAIMED.search(text):
            return f"文档写已完成，账本仍为 {status}"
    return None


def scan_files(paths, rows: dict[str, str], root: Path) -> list[tuple[str, int, str, str, str, str]]:
    problems = []
    for path in paths:
        label = path.name if path.parent == root / "porting" else str(path.relative_to(root)).replace("\\", "/")
        for lineno, text in enumerate(
            io.open(path, encoding="utf-8").read().splitlines(), 1
        ):
            if IGNORE_MARK in text:
                continue
            for tid in sorted(set(ID.findall(text))):
                status = rows.get(tid)
                if status is None:
                    continue
                kind = check_line(text, status)
                if kind:
                    problems.append((label, lineno, tid, status, kind, text.strip()))
    return problems


def scan(doc_dir: Path, rows: dict[str, str], extras=()) -> list[tuple[str, int, str, str, str, str]]:
    root = doc_dir.parent
    paths = [p for p in sorted(doc_dir.glob("*.md")) if p.name not in SKIP_FILES]
    for pattern in extras:
        paths.extend(p for p in sorted(root.glob(pattern)) if p not in paths)
    return scan_files(paths, rows, root)


# ── jsonl 自身自查 ────────────────────────────────────────────────

# title 里不该出现的状态标注（状态在 status 字段里，title 再写一遍必然漂移）
TITLE_STATUS = re.compile(r"（gap）|\(gap\)|（resolved）|\(resolved\)|（已解决）|（未解决）")
# gap/accepted 条目的 decision 里不该自称已收口（那是翻 resolved 时才写的话）
DONE_WORDS = re.compile(r"已解决|已补齐|已实现|已归位|已接线|已闭合")
# 「指向其他条目说它仍是缺口」——被指向条目若已 resolved 就过时了
POINTING = re.compile(r"(缺口|尚未解决|还没解决|未解决)[^。；]*?(单列|另列|另计|见)\s*([A-Z]\d{3})")
POINTING2 = re.compile(r"(单列|另列|另计|见)\s*([A-Z]\d{3})[^。；]*?(仍是|尚未|还是)(缺口|gap|未解决)")


def scan_jsonl(diff_path: Path) -> tuple[list[tuple[str, str, str]], list[tuple[str, str, str]]]:
    """返回 (错误, 复核)，元素为 (条目id, 级别说明, 摘录)。"""
    errors: list[tuple[str, str, str]] = []
    reviews: list[tuple[str, str, str]] = []
    all_status = load_status(diff_path)
    for line in io.open(diff_path, encoding="utf-8"):
        line = line.strip()
        if not line:
            continue
        row = json.loads(line)
        rid, status = row["id"], row.get("status", "")
        title = row.get("title", "")

        if TITLE_STATUS.search(title):
            errors.append((rid, "title 残留状态标注，状态应以 status 字段为准", title))

        if status not in ("resolved",) and DONE_WORDS.search(row.get("decision", "")):
            snippet = row["decision"]
            cut = DONE_WORDS.search(snippet).start()
            errors.append((rid, f"status={status} 但 decision 自称已收口（翻状态了吗？）", "…" + snippet[max(0, cut - 20):cut + 40] + "…"))

        for field in ("what", "decision"):
            text = row.get(field, "")
            for pat in (POINTING, POINTING2):
                for m in pat.finditer(text):
                    ref = m.group(3) if pat is POINTING else m.group(2)
                    if all_status.get(ref) == "resolved":
                        s = max(0, m.start() - 10)
                        reviews.append((rid, f"{field} 说 {ref} 仍是缺口，但它已 resolved（补「现已 resolved」限定？）", "…" + text[s:m.end() + 20] + "…"))
    return errors, reviews


def main() -> int:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")

    parser = argparse.ArgumentParser(description="账本状态与文档措辞一致性检查")
    parser.add_argument("--dir", default="porting", help="台账目录（默认 porting）")
    parser.add_argument(
        "--extra",
        nargs="*",
        default=["README.md", "corpus/*/README.md", "tools/*/README.md", "src/*/README.md"],
        help="额外扫描的文档，按台账目录的上级（仓库根）做 glob",
    )
    parser.add_argument("--strict", action="store_true", help="发现问题即返回非零退出码")
    parser.add_argument("--verbose", action="store_true", help="打印命中行全文")
    args = parser.parse_args()

    doc_dir = Path(args.dir)
    diff_path = doc_dir / "differences.jsonl"
    if not diff_path.exists():
        print(f"找不到 {diff_path}", file=sys.stderr)
        return 2

    rows = load_status(diff_path)
    problems = scan(doc_dir, rows, args.extra)
    json_errors, json_reviews = scan_jsonl(diff_path)

    if not problems and not json_errors and not json_reviews:
        print(f"文档措辞与账本状态一致（{len(rows)} 条差异，含账本自述自查）。")
        return 0

    failed = bool(problems or json_errors)
    if problems:
        print(f"发现 {len(problems)} 处账本与文档不一致：")
        for name, lineno, tid, status, kind, text in problems:
            print(f"  {name}:{lineno}  [{tid}] {kind}")
            print(f"      {text if args.verbose else text[:110]}")
        print()
    for rid, kind, text in json_errors:
        print(f"  [错误] [{rid}] {kind}")
        print(f"      {text}")
    for rid, kind, text in json_reviews:
        print(f"  [复核] [{rid}] {kind}")
        print(f"      {text}")
    if problems or json_errors:
        print()
        print("改法：让文档跟上账本。若这句是历史叙述（描述当时的状态、现在仍要保留原话），")
        print(f"在该行末尾加 {IGNORE_MARK}")
    return 1 if args.strict and failed else 0


if __name__ == "__main__":
    sys.exit(main())
