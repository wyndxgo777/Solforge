#!/usr/bin/env python3
"""Documentation clean check for first-party C# (src/ and tests/).

Lists (1) files with no header comment before their first type and
(2) public/internal types and methods with no doc comment.
Doc comment = /** */ or /// or // block directly above the declaration
(attributes in between are skipped). Overrides, interface-impl-like
trivial one-liners and test methods ([Fact]/[Theory]) are not required
to have comments; test *types* and files still are.

Usage: python tools/doccheck.py [--list] [--root DIR] [--only PATH_PREFIX]
Exit code is 0; this is a report, not a gate.
"""
import os, re, sys

SKIP_DIRS = {"obj", "bin", ".git", "node_modules", "Fixtures"}
SKIP_FILE = re.compile(r"(\.g\.cs|\.g\.i\.cs|\.Designer\.cs|\.generated\.cs|AssemblyInfo\.cs|GlobalUsings\.cs)$")
TYPE_RE = re.compile(r"^\s*(?:\[[^\]]*\]\s*)*(?:(public|internal)\s+)(?:(?:static|sealed|abstract|partial|readonly|unsafe|ref|file)\s+)*(?:class|struct|interface|enum|record(?:\s+struct)?|delegate)\b")
METH_RE = re.compile(r"^\s*(public|internal|protected internal)\s+(?!(?:class|struct|interface|enum|record|delegate|event|const)\b)((?:(?:static|virtual|abstract|async|extern|unsafe|new|sealed|partial)\s+)*)([\w<>\[\],.?\s*()]+?)\s+(\w+)\s*(?:<[^>]*>)?\s*\(")
OVERRIDE_RE = re.compile(r"\boverride\b")

def is_doc_above(lines, i):
    j = i - 1
    while j >= 0:
        s = lines[j].strip()
        if s.startswith("[") and s.endswith("]"):
            j -= 1; continue
        break
    if j < 0: return False
    s = lines[j].strip()
    return s.endswith("*/") or s.startswith("//")

def scan(path, is_test):
    with open(path, encoding="utf-8-sig", errors="replace") as f:
        lines = f.read().split("\n")
    first_type = None
    types, methods = [], []
    depth_in_block_comment = False
    in_test_attr = False
    for i, ln in enumerate(lines):
        st = ln.strip()
        if depth_in_block_comment:
            if "*/" in st: depth_in_block_comment = False
            continue
        if st.startswith("/*") and "*/" not in st:
            depth_in_block_comment = True; continue
        if st.startswith("//"): continue
        if TYPE_RE.match(ln):
            if first_type is None: first_type = i
            m = re.search(r"(?:class|struct|interface|enum|record(?:\s+struct)?|delegate\s+\S+)\s+(\w+)", ln)
            if not is_doc_above(lines, i):
                types.append((i + 1, m.group(1) if m else "?"))
            continue
        m = METH_RE.match(ln)
        if m and "=>" not in ln.split("(")[0] and " new " not in " " + ln.split("(")[0] + " " and not OVERRIDE_RE.search(ln):
            name = m.group(4)
            if name in ("get", "set", "if", "for", "while", "switch", "return", "foreach", "using", "lock"): continue
            # skip properties/fields misparsed, and constructors (no return type group handled by regex)
            attrs = " ".join(lines[max(0, i - 3):i])
            if is_test and re.search(r"\[(Fact|Theory|TestMethod|Test|SkippableFact)", attrs): continue
            if not is_doc_above(lines, i):
                methods.append((i + 1, name))
    header = False
    limit = first_type if first_type is not None else len(lines)
    for k in range(min(limit + 1, len(lines))):
        s = lines[k].strip()
        if s.startswith("/**") or s.startswith("///") or s.startswith("//"):
            header = True; break
    return header, types, methods, len(lines)

def main():
    root = "."
    args = sys.argv[1:]
    if "--root" in args: root = args[args.index("--root") + 1]
    verbose = "--list" in args
    only = args[args.index("--only") + 1] if "--only" in args else None  # path prefix filter, e.g. src/SunriseStudio.Editor
    nfiles = nohdr = ntypes = nmeth = 0
    nohdr_l, types_l, meth_l = [], [], []
    for top in ("src", "tests"):
        for dp, dn, fn in os.walk(os.path.join(root, top)):
            dn[:] = [d for d in dn if d not in SKIP_DIRS]
            for f in fn:
                if not f.endswith(".cs") or SKIP_FILE.search(f): continue
                p = os.path.join(dp, f)
                rel = os.path.relpath(p, root).replace("\\", "/")
                h, t, m, n = scan(p, top == "tests")
                if only and not rel.startswith(only): continue
                nfiles += 1
                if not h: nohdr += 1; nohdr_l.append(rel)
                for ln, nm in t: types_l.append(f"{rel}:{ln} {nm}")
                for ln, nm in m: meth_l.append(f"{rel}:{ln} {nm}")
    print(f"files scanned:            {nfiles}")
    print(f"files without header:     {nohdr}")
    print(f"types without doc:        {len(types_l)}")
    print(f"methods without doc:      {len(meth_l)}")
    # project folders lacking README and not named in ARCHITECTURE.md
    arch = ""
    ap = os.path.join(root, "ARCHITECTURE.md")
    if os.path.exists(ap): arch = open(ap, encoding="utf-8", errors="replace").read()
    miss = []
    for top in ("src", "tests"):
        tp = os.path.join(root, top)
        for d in sorted(os.listdir(tp)):
            full = os.path.join(tp, d)
            if not os.path.isdir(full) or not any(x.endswith(".csproj") for x in os.listdir(full)): continue
            if os.path.exists(os.path.join(full, "README.md")): continue
            short = d.replace("SunriseStudio.", "", 1)
            if d in arch or re.search(r"^\|\s*" + re.escape(short) + r"\s*\|", arch, re.M): continue
            if top == "tests" and re.search(r"(?<![\w.])" + re.escape(short) + r"(?!\w)", arch): continue
            miss.append(f"{top}/{d}")
    print(f"projects w/o README or ARCHITECTURE mention: {len(miss)}")
    if verbose:
        for title, l in (("NO HEADER", nohdr_l), ("UNDOC TYPES", types_l), ("UNDOC METHODS", meth_l), ("UNDOCUMENTED PROJECTS", miss)):
            print(f"\n== {title} ==")
            print("\n".join(l))

if __name__ == "__main__":
    main()
