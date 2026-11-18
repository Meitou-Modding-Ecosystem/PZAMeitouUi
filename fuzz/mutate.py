r"""mutate.py - the PZA Meitou fuzz kit's mutation check ("fuzz the fuzzers"). Author: PZA. Licence: GPLv3 or later.

Each mutant plants one known bug (a removed guard, a reverted fix, a loosened limit) in a COPY of the repo (and of PZAMeitouUi, which every
mod compiles in), runs that copy's fuzz harness with fewer cases, and expects it to FAIL. KILLED = the fuzzer noticed (good). SURVIVED = it
still passed: a blind spot to fix in the fuzzer. A baseline copy without a mutation must PASS first. Nothing in the real repos is touched.

Mutants live in <repo>\tests\fuzz\mutants.txt, verbatim (no escaping):
    @@ ID | path from the folder that holds the repos | fuzz targets (comma list, or *) | what it plants
    <<<
    the text to find (must occur exactly once)
    ===
    its replacement
    >>>

Usage: python -I mutate.py <repo folder> [--only ID,ID] [--scale 0.25]
Writes <repo>\tests\out\fuzz\mutation-report.txt and .json; exit 0 only when every mutant was KILLED.
"""
import json, os, re, shutil, stat, subprocess, sys, tempfile, time

KIT = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(KIT))   # the folder that holds PZAMeitouUi and the mods
SKIP = shutil.ignore_patterns("bin", "obj", "out", ".git", "*.pdb", "__pycache__", ".vs")


def rmtree_retry(path):
    """shutil.rmtree that waits for Windows: a run's DLLs (out\\checkrt\\check.dll) stay locked a moment after the process ends, and a
    read-only file refuses deletion. Each failing entry is made writable and retried (40 x 0.25 s); the whole tree up to 5 times (WASD
    session's report, 2026-11-18: WinError 5 right after "baseline PASS" recorded a false mutation FAIL)."""
    def again(func, p, _exc):
        for _ in range(40):
            try:
                os.chmod(p, stat.S_IWRITE)
                func(p)
                return
            except FileNotFoundError:
                return
            except OSError:
                time.sleep(0.25)
        func(p)   # the last try raises the real error
    for attempt in range(5):
        try:
            if sys.version_info >= (3, 12): shutil.rmtree(path, onexc=again)
            else: shutil.rmtree(path, onerror=lambda f, p, e: again(f, p, e))
            return
        except FileNotFoundError:
            return
        except OSError:
            if attempt == 4: raise
            time.sleep(1)


def parse(path):
    text = open(path, encoding="utf-8").read().replace("\r\n", "\n")
    out = []
    for m in re.finditer(r"^@@ (.+?)\n<<<\n(.*?)\n===\n(.*?)\n?>>>$", text, re.S | re.M):
        head = [h.strip() for h in m.group(1).split("|")]
        if len(head) != 4: raise SystemExit("bad mutant header: " + m.group(1))
        out.append(dict(id=head[0], file=head[1], targets=head[2], what=head[3], find=m.group(2), repl=m.group(3)))
    if not out: raise SystemExit("no mutants in " + path)
    return out


def meitou_dir(repo):
    r"""The Meitou release: the repo's mod\local.props, else the kit's own fuzz\local.props (both git-ignored), as run_fuzz.ps1."""
    for lp in (os.path.join(repo, "mod", "local.props"), os.path.join(KIT, "local.props")):
        if os.path.isfile(lp):
            m = re.search(r"<MeitouDir>(.*?)</MeitouDir>", open(lp, encoding="utf-8").read())
            if m: return m.group(1)
    raise SystemExit("no Meitou release: set MeitouDir in mod\\local.props or PZAMeitouUi\\fuzz\\local.props")


def copy_tree(tmp, repo_name):
    dst = os.path.join(tmp, "w")
    if os.path.isdir(dst): rmtree_retry(dst)
    os.makedirs(dst)
    shutil.copytree(os.path.join(ROOT, repo_name), os.path.join(dst, repo_name), ignore=SKIP)
    if repo_name != "PZAMeitouUi":
        shutil.copytree(os.path.join(ROOT, "PZAMeitouUi"), os.path.join(dst, "PZAMeitouUi"), ignore=SKIP)
    # the native libraries the harness loads (tests\fuzz\native.txt) live in build output folders the copy skips: bring the built ones
    nl = os.path.join(ROOT, repo_name, "tests", "fuzz", "native.txt")
    if os.path.isfile(nl):
        for line in open(nl, encoding="utf-8"):
            rel = line.strip()
            if not rel or rel.startswith("#"): continue
            src, to = os.path.join(ROOT, repo_name, rel), os.path.join(dst, repo_name, rel)
            if os.path.isfile(src):
                os.makedirs(os.path.dirname(to), exist_ok=True); shutil.copy2(src, to)
    return dst


def run(work, repo_name, md, out, scale, targets):
    own = os.path.join(work, repo_name, "tests", "fuzz", "run.ps1")
    if not os.path.isfile(os.path.join(work, repo_name, "tests", "fuzz", "fuzz.csproj")) and os.path.isfile(own):
        # a repo with its own fuzzer (WASD's tests\fuzz\run.ps1, "FUZZ: PASS"): fewer runs, a fixed seed, every target
        cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", own, "-Runs", str(max(1000, int(200000 * scale))), "-Seed", "20261118"]
        t0 = time.time()
        try:
            p = subprocess.run(cmd, capture_output=True, text=True, errors="replace", timeout=1800)
            text, rc = p.stdout + p.stderr, p.returncode
        except subprocess.TimeoutExpired:
            return "FAIL", "TIMEOUT (a hang counts as a kill)", time.time() - t0
        lines = [l for l in text.splitlines() if l.strip()]
        if "error CS" in text: return "BUILD-FAIL", next(l for l in lines if "error CS" in l)[:200], time.time() - t0
        ok = rc == 0 and "FUZZ: PASS" in text
        return ("PASS" if ok else "FAIL"), (next((l for l in lines if "FAIL" in l), lines[-1] if lines else ""))[:200], time.time() - t0
    ps = os.path.join(work, "PZAMeitouUi", "fuzz", "run_fuzz.ps1")
    cmd = ["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", ps, "-Repo", os.path.join(work, repo_name),
           "-Scale", str(scale), "-MeitouDir", md, "-Out", out]
    if targets and targets != "*": cmd += ["-Only", targets]
    t0 = time.time()
    try:
        p = subprocess.run(cmd, capture_output=True, text=True, errors="replace", timeout=900)
        text, rc = p.stdout + p.stderr, p.returncode
    except subprocess.TimeoutExpired:
        return "FAIL", "TIMEOUT (a hang counts as a kill)", time.time() - t0
    lines = [l for l in text.splitlines() if l.strip()]
    if "error CS" in text or "harness build failed" in text:
        return "BUILD-FAIL", next((l for l in lines if "error CS" in l), lines[-1] if lines else "")[:200], time.time() - t0
    first_fail = next((l.strip() for l in lines if l.lstrip().startswith(("FAIL", "HANG")) or " case " in l and "threw" in l), "")
    return ("PASS" if rc == 0 else "FAIL"), (first_fail or (lines[-1] if lines else ""))[:200], time.time() - t0


def main():
    if len(sys.argv) < 2: raise SystemExit(__doc__)
    repo = os.path.abspath(sys.argv[1]); name = os.path.basename(repo.rstrip("\\/"))
    only = set(sys.argv[sys.argv.index("--only") + 1].split(",")) if "--only" in sys.argv else None
    scale = float(sys.argv[sys.argv.index("--scale") + 1]) if "--scale" in sys.argv else 0.25
    ms = parse(os.path.join(repo, "tests", "fuzz", "mutants.txt"))
    md = meitou_dir(repo)
    tmp = tempfile.mkdtemp(prefix="pza_mmut_")
    out = os.path.join(tmp, "out")   # shared: the runtime copy is made once
    rows = []
    try:
        work = copy_tree(tmp, name)
        res, last, dt = run(work, name, md, out, scale, "*")
        print("baseline %-6s %5.0fs %s" % (res, dt, last), flush=True)
        if res != "PASS":
            rows.append(dict(id="baseline", what="no mutation", verdict="BASELINE-" + res, detail=last))
        else:
            for m in ms:
                if only and m["id"] not in only: continue
                work = copy_tree(tmp, name)
                p = os.path.join(work, m["file"])
                s = open(p, encoding="utf-8").read()
                n = s.replace("\r\n", "\n").count(m["find"])
                if n != 1:
                    rows.append(dict(id=m["id"], what=m["what"], verdict="NOT-APPLIED", detail="find text occurs %d times in %s" % (n, m["file"])))
                    print("%-14s NOT-APPLIED (%d times)" % (m["id"], n), flush=True); continue
                crlf = "\r\n" in s
                s = s.replace("\r\n", "\n").replace(m["find"], m["repl"])
                open(p, "w", encoding="utf-8", newline="\r\n" if crlf else "\n").write(s)
                res, last, dt = run(work, name, md, out, scale, m["targets"])
                verdict = {"FAIL": "KILLED", "PASS": "SURVIVED"}.get(res, res)
                rows.append(dict(id=m["id"], what=m["what"], verdict=verdict, detail=last, seconds=round(dt, 1)))
                print("%-14s %-10s %5.0fs %s | %s" % (m["id"], verdict, dt, m["what"], last), flush=True)
    finally:
        try: rmtree_retry(tmp)
        except OSError: shutil.rmtree(tmp, ignore_errors=True)
    killed = sum(1 for r in rows if r["verdict"] == "KILLED")
    total = sum(1 for r in ms if not only or r["id"] in only)
    ok = killed == total and all(r["verdict"] == "KILLED" for r in rows)
    commit = subprocess.run(["git", "-C", repo, "rev-parse", "HEAD"], capture_output=True, text=True).stdout.strip()
    dirty = bool(subprocess.run(["git", "-C", repo, "status", "--porcelain", "--untracked-files=no"], capture_output=True, text=True).stdout.strip())
    rd = os.path.join(repo, "tests", "out", "fuzz"); os.makedirs(rd, exist_ok=True)
    stamp = time.strftime("%Y-%m-%d %H:%M")
    with open(os.path.join(rd, "mutation-report.txt"), "w", encoding="utf-8") as f:
        f.write("%s mutation check (%s, commit %s%s): killed %d of %d -> %s\n\n" % (name, stamp, commit[:9], " + changes" if dirty else "", killed, total, "PASS" if ok else "FAIL"))
        for r in rows: f.write("%-14s %-11s %-70s %s\n" % (r["id"], r["verdict"], r["what"], r["detail"]))
    json.dump(dict(repo=name, result="PASS" if ok else "FAIL", killed=killed, total=total, commit=commit, dirty=dirty,
                   finished=time.strftime("%Y-%m-%dT%H:%M:%S"), rows=rows), open(os.path.join(rd, "mutation-report.json"), "w", encoding="utf-8"), indent=1)
    print("killed %d of %d -> %s; report %s" % (killed, total, "PASS" if ok else "FAIL", os.path.join(rd, "mutation-report.txt")))
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
