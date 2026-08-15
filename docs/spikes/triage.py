#!/usr/bin/env python3
"""LLM triage of surviving Stryker.NET mutants. Spike script — no framework, no CLI polish."""
import json, os, re, subprocess, sys, glob, time
from concurrent.futures import ThreadPoolExecutor, as_completed

REPO = "/home/user/xG-Arcade"
REPORT_JSON = glob.glob(f"{REPO}/backend/src/XGArcade.Games.XGGrid/StrykerOutput_full/*/reports/mutation-report.json")[0]
MODEL = "claude-opus-5"
CONTEXT_BEFORE = 45
CONTEXT_AFTER = 18

SYSTEM_PROMPT = (
    "You are a mutation-testing triage assistant reviewing surviving mutants from a "
    "Stryker.NET run on a C#/.NET codebase. For each mutant you are given: the source "
    "file, the mutated line(s), the original vs mutated code, surrounding source context "
    "(including doc comments), any REQ-xxx requirement references found nearby, and any "
    "test code that appears to exercise the containing method. Classify the mutant.\n\n"
    "classification values:\n"
    "- real_gap: a real bug at this mutation would ship undetected by the existing tests.\n"
    "- equivalent: the mutant is behaviorally identical to the original — unkillable by any test.\n"
    "- noise: logging, trivial guards, or other low-value mutation-operator artifacts.\n\n"
    "severity: high/medium/low, only meaningful for real_gap (equivalent/noise are usually low).\n\n"
    "Output ONLY a single-line JSON object, no markdown fences, no other text: "
    '{"classification": "real_gap|equivalent|noise", "severity": "high|medium|low", '
    '"rationale": "one or two sentences, referencing the SPECIFIC reason the existing tests fail to kill it"}'
)

SUBPROCESS_ENV_STRIP = [
    "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN",
    "CLAUDE_CODE_REMOTE_SESSION_ID", "CLAUDE_SESSION_INGRESS_TOKEN_FILE",
    "CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR", "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR",
]

REQ_RE = re.compile(r"REQ-\d+")
METHOD_SIG_RE = re.compile(
    r"^\s*(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|readonly|new)\s+)+"
    r"[\w<>\[\],\.\? ]+?\s+(\w+)\s*\("
)


def load_survivors():
    data = json.load(open(REPORT_JSON))
    survivors = []
    for path, info in data["files"].items():
        for m in info["mutants"]:
            if m["status"] == "Survived":
                survivors.append({"path": path, **m})
    return survivors, data


def guess_method_name(lines, mutant_line_idx):
    for i in range(mutant_line_idx, max(-1, mutant_line_idx - CONTEXT_BEFORE), -1):
        m = METHOD_SIG_RE.match(lines[i])
        if m:
            return m.group(1)
    return None


def find_test_file(src_path):
    # backend/src/<Module>/Foo.cs -> backend/tests/<Module>.Tests/FooTests.cs
    m = re.search(r"/backend/src/([^/]+)/([^/]+)\.cs$", src_path)
    if not m:
        return None
    module, cls = m.group(1), m.group(2)
    candidate = f"{REPO}/backend/tests/{module}.Tests/{cls}Tests.cs"
    if os.path.exists(candidate):
        return candidate
    # fallback: search the whole test project for the class name
    test_dir = f"{REPO}/backend/tests/{module}.Tests"
    if os.path.isdir(test_dir):
        for f in glob.glob(f"{test_dir}/**/*.cs", recursive=True):
            if cls in os.path.basename(f):
                return f
    return None


def extract_covering_tests(test_file, method_name, max_tests=3):
    if not test_file or not method_name or not os.path.exists(test_file):
        return []
    src = open(test_file, encoding="utf-8").read()
    # crude C# test-method splitter: [Test] ... public ... TestName(...) { ... } (brace-matched)
    tests = []
    for m in re.finditer(r"\[Test(?:Case\([^)]*\))?\]\s*\n\s*public\s+(?:async\s+)?\S+\s+(\w+)\s*\(", src):
        start = m.end()
        depth = 0
        body_start = src.find("{", start)
        if body_start == -1:
            continue
        i = body_start
        depth = 0
        while i < len(src):
            if src[i] == "{":
                depth += 1
            elif src[i] == "}":
                depth -= 1
                if depth == 0:
                    break
            i += 1
        body = src[body_start:i + 1]
        if re.search(rf"\b{re.escape(method_name)}\s*\(", body) or re.search(rf"\.{re.escape(method_name)}\b", body):
            tests.append((m.group(1), body[:1200]))
        if len(tests) >= max_tests:
            break
    return tests


def build_context(mutant):
    path = mutant["path"]
    if not os.path.exists(path):
        return None
    lines = open(path, encoding="utf-8", errors="replace").read().splitlines()
    line0 = mutant["location"]["start"]["line"] - 1
    lo = max(0, line0 - CONTEXT_BEFORE)
    hi = min(len(lines), mutant["location"]["end"]["line"] + CONTEXT_AFTER)
    window = lines[lo:hi]
    numbered = "\n".join(f"{lo + i + 1:5d}| {l}" for i, l in enumerate(window))

    method_name = guess_method_name(lines, line0)
    reqs = sorted(set(REQ_RE.findall("\n".join(window))))
    test_file = find_test_file(path)
    covering = extract_covering_tests(test_file, method_name) if method_name else []

    rel_path = path.replace(REPO + "/", "")
    orig_line_text = lines[line0] if line0 < len(lines) else ""

    parts = [
        f"FILE: {rel_path}",
        f"MUTATOR: {mutant['mutatorName']}",
        f"MUTATED LINE(S): {mutant['location']['start']['line']}-{mutant['location']['end']['line']}",
        f"ORIGINAL LINE TEXT: {orig_line_text.strip()}",
        f"MUTATED REPLACEMENT: {mutant['replacement']}",
        f"CONTAINING METHOD (best guess): {method_name or 'unknown'}",
        f"REQ REFERENCES NEARBY: {', '.join(reqs) if reqs else 'none found'}",
        "",
        f"SOURCE CONTEXT ({rel_path}, lines {lo+1}-{hi}):",
        numbered,
    ]
    if covering:
        parts.append("\nCOVERING TEST(S) (heuristically matched by method-name reference in test body):")
        for name, body in covering:
            parts.append(f"--- {name} ---\n{body}")
    else:
        parts.append("\nCOVERING TEST(S): none heuristically matched (method name not found in the sibling test file, or no test file located).")

    return "\n".join(parts)


def call_llm(prompt_text):
    env = dict(os.environ)
    for k in SUBPROCESS_ENV_STRIP:
        env.pop(k, None)
    cmd = [
        "claude", "-p", prompt_text,
        "--model", MODEL,
        "--output-format", "json",
        "--disallowedTools", "*",
        "--system-prompt", SYSTEM_PROMPT,
        "--strict-mcp-config",
        "--exclude-dynamic-system-prompt-sections",
    ]
    t0 = time.time()
    proc = subprocess.run(cmd, env=env, capture_output=True, text=True, timeout=180)
    wall = time.time() - t0
    if proc.returncode != 0:
        return {"error": f"exit {proc.returncode}: {proc.stderr[:500]}", "wall_s": wall}
    try:
        d = json.loads(proc.stdout)
    except json.JSONDecodeError:
        return {"error": f"bad json stdout: {proc.stdout[:500]} stderr: {proc.stderr[:300]}", "wall_s": wall}

    result_text = d.get("result", "")
    parsed = None
    m = re.search(r"\{.*\}", result_text, re.DOTALL)
    if m:
        try:
            parsed = json.loads(m.group(0))
        except json.JSONDecodeError:
            parsed = None

    usage = d.get("usage", {})
    return {
        "parsed": parsed,
        "raw_result": result_text,
        "model": MODEL,
        "input_tokens": usage.get("input_tokens", 0),
        "output_tokens": usage.get("output_tokens", 0),
        "cache_creation_input_tokens": usage.get("cache_creation_input_tokens", 0),
        "cache_read_input_tokens": usage.get("cache_read_input_tokens", 0),
        "cost_usd": d.get("total_cost_usd", 0),
        "wall_s": wall,
        "session_id": d.get("session_id"),
    }


def triage_one(mutant):
    ctx = build_context(mutant)
    if ctx is None:
        return {"mutant_id": mutant["id"], "path": mutant["path"], "error": "source file not found"}
    prompt = f"Classify this surviving mutant:\n\n{ctx}"
    result = call_llm(prompt)
    result["mutant_id"] = mutant["id"]
    result["path"] = mutant["path"].replace(REPO + "/", "")
    result["mutatorName"] = mutant["mutatorName"]
    result["location"] = mutant["location"]
    result["replacement"] = mutant["replacement"]
    return result


def main():
    which = sys.argv[1] if len(sys.argv) > 1 else "all"
    survivors, _ = load_survivors()
    print(f"Total survivors: {len(survivors)}", file=sys.stderr)

    if which == "sample15":
        ids_file = sys.argv[2]
        wanted_ids = set(json.load(open(ids_file)))
        survivors = [m for m in survivors if m["id"] in wanted_ids]
        print(f"Filtered to {len(survivors)} for eval-set run", file=sys.stderr)

    out_path = sys.argv[3] if len(sys.argv) > 3 else "/tmp/triage_results.jsonl"
    results = []
    with ThreadPoolExecutor(max_workers=6) as ex, open(out_path, "w") as outf:
        futs = {ex.submit(triage_one, m): m for m in survivors}
        done = 0
        for fut in as_completed(futs):
            r = fut.result()
            results.append(r)
            outf.write(json.dumps(r) + "\n")
            outf.flush()
            done += 1
            print(f"[{done}/{len(survivors)}] id={r.get('mutant_id')} -> "
                  f"{(r.get('parsed') or {}).get('classification', 'ERROR')} "
                  f"(${r.get('cost_usd', 0):.4f})", file=sys.stderr)

    total_cost = sum(r.get("cost_usd", 0) for r in results)
    total_in = sum(r.get("input_tokens", 0) + r.get("cache_creation_input_tokens", 0) + r.get("cache_read_input_tokens", 0) for r in results)
    total_out = sum(r.get("output_tokens", 0) for r in results)
    errors = sum(1 for r in results if r.get("error") or not r.get("parsed"))
    print(f"\nTOTALS: n={len(results)} cost=${total_cost:.4f} input_tok={total_in} output_tok={total_out} errors={errors}", file=sys.stderr)


if __name__ == "__main__":
    main()
