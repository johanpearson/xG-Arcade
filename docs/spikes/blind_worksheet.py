#!/usr/bin/env python3
"""Blind classification worksheet generator. Pure data extraction — no LLM calls,
no classification, no verdicts. Evidence only."""
import json, re, glob, random, os, sys

REPO = "/home/user/xG-Arcade"
REPORT_JSON = glob.glob(f"{REPO}/backend/src/XGArcade.Games.XGGrid/StrykerOutput_full/*/reports/mutation-report.json")[0]
SEED = 20260815
SAMPLE_SIZE = 20
OUT_PATH = f"{REPO}/docs/spikes/blind-worksheet.md"

CS_KEYWORDS = {
    "var", "new", "return", "if", "else", "for", "foreach", "while", "do", "switch", "case",
    "default", "break", "continue", "throw", "try", "catch", "finally", "using", "await",
    "async", "public", "private", "protected", "internal", "static", "readonly", "const",
    "sealed", "virtual", "override", "abstract", "class", "struct", "interface", "enum",
    "namespace", "true", "false", "null", "this", "base", "get", "set", "value", "in", "out",
    "ref", "params", "void", "int", "long", "double", "float", "decimal", "bool", "string",
    "object", "char", "byte", "short", "uint", "ulong", "ushort", "sbyte", "is", "as", "typeof",
    "nameof", "select", "where", "from", "orderby", "group", "into", "let", "yield",
    "cancellationToken", "CancellationToken", "Task", "List", "IReadOnlyList", "IEnumerable",
    "Guid", "DateTime", "DateTimeOffset", "string.Empty",
}


def mask_source(text):
    """Replace comment/string/char literal contents with spaces so brace-counting
    on the result only sees real code braces. Preserves length and newlines."""
    out = list(text)
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            j = i
            while j < n and text[j] != "\n":
                out[j] = " "
                j += 1
            i = j
        elif c == "/" and i + 1 < n and text[i + 1] == "*":
            j = i
            out[j] = " "; out[j + 1] = " "
            j += 2
            while j + 1 < n and not (text[j] == "*" and text[j + 1] == "/"):
                if text[j] != "\n":
                    out[j] = " "
                j += 1
            if j + 1 < n:
                out[j] = " "; out[j + 1] = " "
                j += 2
            i = j
        elif c == '"':
            j = i
            out[j] = " "
            j += 1
            while j < n and text[j] != '"':
                if text[j] == "\\" and j + 1 < n:
                    if text[j] != "\n": out[j] = " "
                    j += 1
                if j < n and text[j] != "\n":
                    out[j] = " "
                j += 1
            if j < n:
                out[j] = " "
                j += 1
            i = j
        elif c == "'":
            j = i
            out[j] = " "
            j += 1
            while j < n and text[j] != "'":
                if text[j] == "\\" and j + 1 < n:
                    if text[j] != "\n": out[j] = " "
                    j += 1
                if j < n and text[j] != "\n":
                    out[j] = " "
                j += 1
            if j < n:
                out[j] = " "
                j += 1
            i = j
        else:
            i += 1
    return "".join(out)


METHOD_SIG_RE = re.compile(
    r"^[ \t]*(?:\[[^\]]*\]\s*)*"
    r"(?:(?:public|private|protected|internal|static|async|virtual|override|sealed|readonly|abstract|new|partial)\s+)+"
    r"[\w<>\[\],\.\?]+(?:\s*\[\])?\s+(\w+)\s*(?:<[^>]*>)?\s*\(",
    re.MULTILINE,
)


def find_method_spans(text, masked):
    """Returns list of (start_idx, end_idx, name, sig_line_no) for every
    modifier-qualified method/constructor member found, brace-matched."""
    spans = []
    for m in METHOD_SIG_RE.finditer(text):
        sig_start = m.start()
        name = m.group(1)
        # find the closing paren of the parameter list on the masked text
        depth = 0
        k = m.end() - 1  # at '('
        while k < len(masked):
            if masked[k] == "(":
                depth += 1
            elif masked[k] == ")":
                depth -= 1
                if depth == 0:
                    break
            k += 1
        if k >= len(masked):
            continue
        # scan forward from k+1 for either '{' (block body) or ';'/'=>' (no block body to extract fully)
        p = k + 1
        while p < len(masked) and masked[p] in " \t\r\n":
            p += 1
        # skip base()/this() constructor initializer or where-clauses before the brace
        while p < len(masked) and masked[p] not in "{;":
            p += 1
        if p >= len(masked) or masked[p] != "{":
            continue  # expression-bodied or abstract/interface signature; skip
        open_brace = p
        depth = 0
        q = open_brace
        while q < len(masked):
            if masked[q] == "{":
                depth += 1
            elif masked[q] == "}":
                depth -= 1
                if depth == 0:
                    break
            q += 1
        if q >= len(masked):
            continue
        sig_line_no = text.count("\n", 0, sig_start) + 1
        spans.append((sig_start, q + 1, name, sig_line_no))
    return spans


def line_of(text, idx):
    return text.count("\n", 0, idx) + 1


def containing_method(text, masked, mutant_line):
    spans = find_method_spans(text, masked)
    best = None
    for start, end, name, sig_line in spans:
        s_line = line_of(text, start)
        e_line = line_of(text, end)
        if s_line <= mutant_line <= e_line:
            length = e_line - s_line
            if best is None or length < best[3]:
                best = (s_line, e_line, name, length)
    return best  # (start_line, end_line, name, length) or None


def leading_doc_comment(lines, sig_line_idx0):
    """Contiguous // or /// comment lines immediately above the signature."""
    out = []
    i = sig_line_idx0 - 1
    while i >= 0 and re.match(r"^\s*//", lines[i]):
        out.append(lines[i])
        i -= 1
    return list(reversed(out))


REQ_RE = re.compile(r"REQ-\d+")
ADR_RE = re.compile(r"ADR-\d+")


def extract_identifiers(line_text):
    ids = re.findall(r"\b[A-Za-z_]\w*\b", line_text)
    out = []
    for tok in ids:
        if tok in CS_KEYWORDS:
            continue
        if len(tok) <= 2:
            continue
        if tok[0].isupper() and tok.isupper():  # all-caps constants, keep but lower priority
            pass
        out.append(tok)
    # de-dupe preserving order
    seen = set()
    uniq = []
    for t in out:
        if t not in seen:
            seen.add(t)
            uniq.append(t)
    return uniq


def data_flow(text, lines, identifiers, max_vars=3):
    sections = []
    for ident in identifiers[:max_vars]:
        pattern = re.compile(rf"\b{re.escape(ident)}\b")
        occurrences = []
        for i, l in enumerate(lines):
            if pattern.search(l):
                occurrences.append((i + 1, l.rstrip()))
        if len(occurrences) >= 2:  # only worth reporting if it appears elsewhere too
            sections.append((ident, occurrences))
    return sections


def find_test_file(src_path):
    m = re.search(r"/backend/src/([^/]+)/([^/]+)\.cs$", src_path)
    if not m:
        return None
    module, cls = m.group(1), m.group(2)
    candidate = f"{REPO}/backend/tests/{module}.Tests/{cls}Tests.cs"
    if os.path.exists(candidate):
        return candidate
    test_dir = f"{REPO}/backend/tests/{module}.Tests"
    if os.path.isdir(test_dir):
        for f in glob.glob(f"{test_dir}/**/*.cs", recursive=True):
            if cls in os.path.basename(f):
                return f
    return None


def find_all_covering_tests(test_file, method_name):
    if not test_file or not method_name or not os.path.exists(test_file):
        return []
    src = open(test_file, encoding="utf-8").read()
    tests = []
    for m in re.finditer(r"\[Test(?:Case\([^)]*\))?\]\s*\n\s*public\s+(?:async\s+)?\S+\s+(\w+)\s*\(", src):
        body_start = src.find("{", m.end())
        if body_start == -1:
            continue
        depth = 0
        i = body_start
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
            full = src[m.start():i + 1]
            tests.append((m.group(1), full))
    return tests


def build_section(mutant):
    path = mutant["path"]
    rel_path = path.replace(REPO + "/", "")
    text = open(path, encoding="utf-8", errors="replace").read()
    lines = text.splitlines()
    masked = mask_source(text)

    m_start_line = mutant["location"]["start"]["line"]
    m_end_line = mutant["location"]["end"]["line"]
    orig_snippet = "\n".join(lines[m_start_line - 1:m_end_line])

    cm = containing_method(text, masked, m_start_line)
    if cm:
        s_line, e_line, method_name, length = cm
        method_lines = lines[s_line - 1:e_line]
        method_text = "\n".join(f"{s_line + i:5d}| {l}" for i, l in enumerate(method_lines))
        doc_comment = leading_doc_comment(lines, s_line - 1)
    else:
        method_name, s_line, e_line, length = None, None, None, None
        lo = max(0, m_start_line - 30)
        hi = min(len(lines), m_end_line + 10)
        method_lines = lines[lo:hi]
        method_text = "\n".join(f"{lo + i + 1:5d}| {l}" for i, l in enumerate(method_lines))
        doc_comment = []

    full_method_source = "\n".join(method_lines)
    reqs = sorted(set(REQ_RE.findall(full_method_source) + ADR_RE.findall(full_method_source)))
    inline_comments = [l.strip() for l in method_lines if re.match(r"^\s*//", l)]

    idents = extract_identifiers(orig_snippet)
    df = data_flow(text, lines, idents)

    test_file = find_test_file(path)
    covering = find_all_covering_tests(test_file, method_name) if method_name else []

    return {
        "id": mutant["id"],
        "rel_path": rel_path,
        "mutator": mutant["mutatorName"],
        "m_start_line": m_start_line,
        "m_end_line": m_end_line,
        "orig_snippet": orig_snippet,
        "replacement": mutant["replacement"],
        "method_name": method_name,
        "method_span": (s_line, e_line, length),
        "method_text": method_text,
        "doc_comment": doc_comment,
        "reqs": reqs,
        "inline_comments": inline_comments,
        "data_flow": df,
        "test_file": test_file.replace(REPO + "/", "") if test_file else None,
        "covering_tests": covering,
    }


def render_section(sec):
    out = []
    out.append(f"## Mutant {sec['id']}\n")
    out.append(f"**File:** `{sec['rel_path']}`  ")
    out.append(f"**Mutated line(s):** {sec['m_start_line']}-{sec['m_end_line']}  ")
    out.append(f"**Mutator:** {sec['mutator']}\n")
    out.append("**Original:**")
    out.append("```csharp")
    out.append(sec["orig_snippet"])
    out.append("```")
    out.append("**Mutated replacement:**")
    out.append("```csharp")
    out.append(sec["replacement"])
    out.append("```\n")

    if sec["method_name"]:
        s_line, e_line, length = sec["method_span"]
        out.append(f"### Containing method: `{sec['method_name']}` (lines {s_line}-{e_line}, {length+1} lines)")
        if length > 150:
            out.append(f"*(exceeds 150 lines — included in full anyway, per instructions)*")
        if sec["doc_comment"]:
            out.append("\n**Leading doc comment:**")
            out.append("```")
            out.append("\n".join(sec["doc_comment"]))
            out.append("```")
        out.append("\n```csharp")
        out.append(sec["method_text"])
        out.append("```\n")
    else:
        out.append("### Containing method: not resolved by brace-matching heuristic")
        out.append("*(fallback: fixed window shown below)*\n")
        out.append("```csharp")
        out.append(sec["method_text"])
        out.append("```\n")

    out.append("### Data flow")
    if sec["data_flow"]:
        for ident, occurrences in sec["data_flow"]:
            out.append(f"\n**`{ident}`** — every occurrence in `{sec['rel_path']}`:")
            out.append("```")
            for ln, txt in occurrences:
                marker = "  <-- mutation site" if sec["m_start_line"] <= ln <= sec["m_end_line"] else ""
                out.append(f"{ln:5d}| {txt}{marker}")
            out.append("```")
    else:
        out.append("\n*No identifier extracted from the mutated line recurs elsewhere in the file "
                    "(or none survived the keyword/short-token filter).*")
    out.append("")

    out.append("### Tests")
    if sec["test_file"] is None:
        out.append(f"\n*No sibling test file located for `{sec['rel_path']}`.*")
    elif not sec["covering_tests"]:
        out.append(f"\nno test matched by name (searched `{sec['test_file']}` for tests referencing "
                    f"`{sec['method_name']}`)")
    else:
        for name, body in sec["covering_tests"]:
            out.append(f"\n**`{name}`** (from `{sec['test_file']}`):")
            out.append("```csharp")
            out.append(body)
            out.append("```")
    out.append("")

    out.append("### REQ/ADR references and comments within the method")
    if sec["reqs"]:
        out.append(f"\nReferences found: {', '.join(sec['reqs'])}")
    else:
        out.append("\nNo REQ-xxx/ADR-xxx references found within the method body or its doc comment.")
    if sec["inline_comments"]:
        out.append("\nInline comments within the method:")
        out.append("```")
        out.append("\n".join(sec["inline_comments"]))
        out.append("```")
    out.append("")

    out.append("---")
    out.append("- Classification: [ real_gap | equivalent | noise ]")
    out.append("- Severity (real_gap only): [ high | medium | low ]")
    out.append("- Reason:")
    out.append("- Confidence: [ certain | unsure ]")
    out.append("\n---\n")
    return "\n".join(out)


def main():
    data = json.load(open(REPORT_JSON))
    survivors = []
    for path, info in data["files"].items():
        for m in info["mutants"]:
            if m["status"] == "Survived":
                survivors.append({"path": path, **m})

    all_ids_sorted = sorted(survivors, key=lambda m: int(m["id"]))
    rng = random.Random(SEED)
    sample = rng.sample(all_ids_sorted, SAMPLE_SIZE)
    sample_sorted = sorted(sample, key=lambda m: int(m["id"]))
    sample_ids = [m["id"] for m in sample_sorted]

    print(f"SEED: {SEED}", file=sys.stderr)
    print(f"SELECTED IDS ({len(sample_ids)}): {sample_ids}", file=sys.stderr)

    sections = [build_section(m) for m in sample_sorted]

    with open(OUT_PATH, "w") as f:
        f.write("# Blind Classification Worksheet\n\n")
        f.write(f"20 surviving mutants sampled at random (seed `{SEED}`) from the 94 survivors "
                f"in the `XGArcade.Games.XGGrid` Stryker.NET run "
                f"(`StrykerOutput_full/*/reports/mutation-report.json`).\n\n")
        f.write(f"**Selected mutant IDs (ascending):** {', '.join(sample_ids)}\n\n")
        f.write("Evidence only, mechanically extracted — no classification, severity, or hint of a "
                "verdict is included anywhere below. Fill in the four fields at the end of each "
                "section yourself.\n\n---\n\n")
        for sec in sections:
            f.write(render_section(sec))
            f.write("\n")

    print(f"OUT: {OUT_PATH}", file=sys.stderr)


if __name__ == "__main__":
    main()
