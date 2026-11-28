r"""mutate_selftest.py - proves mutate.py mutates the checkout it is given, not the main one next to the kit. Author: PZA. Licence: GPLv3 or later.

Why (2026-11-28, found by the WASD session): run_all_fuzz.py --path <worktree> fuzzed the worktree, but mutate.py copied
<folder holding the kit>\<folder name>, i.e. the MAIN checkout; a gate could pass mutation for code it never mutated.

A  the kit itself as a "worktree": a copy of PZAMeitouUi in another folder WITH THE SAME NAME (the case that used to pick the main
   checkout silently), with one line of src\PzaFont.cs changed only in the copy. Its mutants.txt holds
     WT-only    find text that exists only in the copy      -> must be KILLED (applied there, and the fuzzer catches it)
     MAIN-only  find text that exists only in the main kit  -> must be NOT-APPLIED, and the whole check must FAIL
B  a mod "worktree" under another folder name (a copy of PZAMeitouUiProbe called PZAMeitouUiProbe-wt, run with --name): its kit
   mutant UP-font-pair must be applied to the kit copy and KILLED (bfe0370 had mapped kit files into the mod).

Usage: python -I mutate_selftest.py      exit 0 = both scenarios behave; the temp copies are removed. Nothing in the real repos changes.
"""
import json, os, shutil, subprocess, sys, tempfile

KIT = os.path.dirname(os.path.abspath(__file__))
UI = os.path.dirname(KIT)
ROOT = os.path.dirname(UI)
SKIP = shutil.ignore_patterns("bin", "obj", "out", ".git", "*.pdb", "__pycache__", ".vs")
FONT_MAIN = "i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]) ? char.ConvertToUtf32"
FONT_WT = "i + 1 < line.Length && /*wt-selftest*/ char.IsLowSurrogate(line[i + 1]) ? char.ConvertToUtf32"
MUTANTS_A = """# mutate_selftest.py scenario A (temp copy only)

@@ WT-only | PZAMeitouUi\\src\\PzaFont.cs | font.wrap | worktree-only text: a lone surrogate half throws
<<<
/*wt-selftest*/ char.IsLowSurrogate(line[i + 1]) ?
===
/*wt-selftest*/ true ?
>>>

@@ MAIN-only | PZAMeitouUi\\src\\PzaFont.cs | font.wrap | text only the main checkout has
<<<
&& char.IsLowSurrogate(line[i + 1]) ?
===
&& true ?
>>>
"""


def mutate(target, *extra):
    cmd = [sys.executable, "-I", os.path.join(KIT, "mutate.py"), target, *extra]
    p = subprocess.run(cmd, capture_output=True, text=True, errors="replace", timeout=3600)
    print("   " + "\n   ".join(l for l in (p.stdout + p.stderr).splitlines() if l.strip()), flush=True)
    rep = json.load(open(os.path.join(target, "tests", "out", "fuzz", "mutation-report.json"), encoding="utf-8"))
    return p.returncode, rep, {r["id"]: r["verdict"] for r in rep["rows"]}


def main():
    font = open(os.path.join(UI, "src", "PzaFont.cs"), encoding="utf-8").read()
    if font.count(FONT_MAIN) != 1: raise SystemExit("self-test out of date: the surrogate line in PzaFont.cs moved")
    tmp = tempfile.mkdtemp(prefix="pza_mutself_")
    bad = []
    try:
        print("A: the kit as a same-named copy elsewhere", flush=True)
        a = os.path.join(tmp, "a", "PZAMeitouUi")
        shutil.copytree(UI, a, ignore=SKIP)
        p = os.path.join(a, "src", "PzaFont.cs")
        text = open(p, encoding="utf-8", newline="").read()
        open(p, "w", encoding="utf-8", newline="").write(text.replace(FONT_MAIN, FONT_WT))
        open(os.path.join(a, "tests", "fuzz", "mutants.txt"), "w", encoding="utf-8").write(MUTANTS_A)
        rc, rep, v = mutate(a)
        if v.get("WT-only") != "KILLED": bad.append("A: the worktree-only mutant was %s, not KILLED" % v.get("WT-only"))
        if v.get("MAIN-only") != "NOT-APPLIED": bad.append("A: the main-only mutant was %s, not NOT-APPLIED" % v.get("MAIN-only"))
        if rc == 0 or rep["result"] != "FAIL": bad.append("A: a NOT-APPLIED mutant did not fail the check")
        if os.path.normcase(rep.get("checkout", "")) != os.path.normcase(a): bad.append("A: report names checkout %s" % rep.get("checkout"))

        print("B: a mod worktree under another folder name, kit mutant", flush=True)
        b = os.path.join(tmp, "b", "PZAMeitouUiProbe-wt")
        shutil.copytree(os.path.join(ROOT, "PZAMeitouUiProbe"), b, ignore=SKIP)
        rc, rep, v = mutate(b, "--name", "PZAMeitouUiProbe", "--only", "UP-font-pair")
        if v.get("UP-font-pair") != "KILLED" or rc != 0: bad.append("B: the kit mutant from a mod worktree was %s" % v.get("UP-font-pair"))
        if os.path.normcase(rep.get("checkout", "")) != os.path.normcase(b): bad.append("B: report names checkout %s" % rep.get("checkout"))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)
    for x in bad: print("FAIL " + x)
    print("mutate self-test: " + ("PASS" if not bad else "FAIL"))
    return 0 if not bad else 1


if __name__ == "__main__":
    sys.exit(main())
