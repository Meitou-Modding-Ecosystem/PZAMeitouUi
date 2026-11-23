// PZA's Meitou UI: the shared fuzz kit. Author: PZA. Licence: GPLv3 or later. Compiled into every PZA Meitou mod's OFFLINE fuzz harness
// (tests\fuzz\fuzz.csproj imports ..\..\..\PZAMeitouUi\fuzz\fuzz.props), never into a mod.
//
// What it gives a harness:
// - seeded, repeatable input: every case of every target has its own generator, seeded from (run seed, target name, case number), so
//   "--only Target --case 1234" replays exactly the case a report names, on any machine;
// - caps and guards: a case count and a time budget per target, a watchdog that ends the run as HANG when one case runs longer than
//   --hang seconds (a hang is a failure, never a silent stall), at most 20 failure lines kept per target;
// - nasty input: numbers (NaN, infinities, -0, denormals, huge, the edges of a range), strings (control characters, lone surrogates, BOMs,
//   right-to-left marks, CJK, emoji, very long), and mutated text / bytes (flips, cuts, repeats, insertions of nasty tokens);
// - a report: RESULT: PASS / FAIL on the console and fuzz-report.json + fuzz-report.txt in --report (the central runner and the release
//   gate read the JSON: mod, commit, seed, cases and failures per target).
// An exception escaping a target is a failure (the game would see it as a fault in a frame). A target that ran no case is a failure too.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PZA.MeitouUi.Fuzz;

public sealed class PzaFuzz
{
    // ---- the generator: SplitMix64 (same numbers on every .NET version, unlike System.Random's unseeded paths)
    ulong _s;
    public ulong NextU64()
    {
        ulong z = _s += 0x9E3779B97F4A7C15UL;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
    public static ulong Hash(string s, ulong h = 1469598103934665603UL)
    {
        foreach (char c in s) { h ^= c; h *= 1099511628211UL; }
        return h;
    }

    /// <summary>0 .. n-1 (n &lt;= 0 gives 0).</summary>
    public int Below(int n) => n <= 0 ? 0 : (int)(NextU64() % (ulong)n);
    /// <summary>lo .. hi inclusive.</summary>
    public int Int(int lo, int hi) => hi <= lo ? lo : lo + (int)(NextU64() % (ulong)((long)hi - lo + 1));
    public bool Chance(double p) => Unit() < p;
    public bool Bool() => (NextU64() & 1) != 0;
    public double Unit() => (NextU64() >> 11) * (1.0 / (1UL << 53));
    public double Range(double lo, double hi) => lo + (hi - lo) * Unit();
    public T Pick<T>(IReadOnlyList<T> items) => items[Below(items.Count)];

    public static readonly double[] NastyDoubles =
    [
        double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, -0.0, double.Epsilon, -double.Epsilon, double.MaxValue,
        double.MinValue, 1e-300, 1e300, -1e300, 1, -1, 0.5, 1e9, -1e9, int.MaxValue, int.MinValue, 2147483648.0, -2147483649.0, 1e15,
    ];

    /// <summary>A number that is often nasty: NaN, infinities, huge, -0, or near / at / just past the edges of [lo, hi], else inside it.</summary>
    public double Double(double lo = -1e6, double hi = 1e6) => Below(8) switch
    {
        0 => Pick(NastyDoubles),
        1 => Bool() ? lo : hi,
        2 => (Bool() ? lo : hi) + (Bool() ? 1 : -1) * Pick(new[] { 1e-12, 1e-6, 0.001, 0.5, 1, 100 }),
        3 => Range(lo - (hi - lo), hi + (hi - lo)),
        _ => Range(lo, hi),
    };

    /// <summary>A float version of <see cref="Double"/>.</summary>
    public float Float(float lo = -1e6f, float hi = 1e6f) => (float)Double(lo, hi);

    public static readonly int[] NastyInts = [0, 1, -1, 2, int.MaxValue, int.MinValue, int.MaxValue - 1, int.MinValue + 1, 255, 256, 65535, 65536, -65536];
    public int IntNasty(int lo = -1000, int hi = 1000) => Below(5) switch { 0 => Pick(NastyInts), 1 => Bool() ? lo : hi, 2 => (Bool() ? lo - 1 : hi + 1), _ => Int(lo, hi) };

    static readonly string[] Pieces =
    [
        "", " ", "  ", "\t", "\n", "\r\n", "\r", "\0", "\u0001", "\u007f", "\u0085", "\u00a0", "\u200b", "\u200e", "\u202e", "\u2028",
        "\ufeff", "\ud800", "\udfff", "\ud83d\ude00", "\u65e5\u672c\u8a9e", "\u4e2d\u6587", "\u0440\u0443\u0441", "\ud55c\uad6d", "\u2026",
        "\u2014", "\u00ab\u00bb", "\u201c\u201d", "##", "#FF0000", "{", "}", "{1}", "{9}", "{0}", "{10}", "\\", "\\n", "\\\\", "\\=", "\\#",
        "\\x", "=", "==", "#", ";", "\"", "'", "<", ">", "&", "&amp;", "%s", "%n", "{{", "}}", "null", "true", "false", "NaN", "Infinity",
        "-0", "1e999", "0x10", "\u0661\u0662", "a", "abc", "Speed", "auto", "en", "ru", "zh", "ja",
    ];

    /// <summary>A short nasty string (mostly a few pieces glued together, sometimes very long: up to <paramref name="maxLen"/>).</summary>
    public string Text(int maxLen = 400)
    {
        var sb = new StringBuilder();
        int parts = Below(6) == 0 ? Int(20, 200) : Int(0, 8);
        for (int i = 0; i < parts && sb.Length < maxLen; i++)
        {
            switch (Below(5))
            {
                case 0: sb.Append((char)Int(0, 0xFFFF)); break;
                case 1: sb.Append((char)Int(0x20, 0x7E)); break;
                case 2: sb.Append(new string('x', Int(1, 60))); break;
                default: sb.Append(Pick(Pieces)); break;
            }
        }
        return sb.Length > maxLen ? sb.ToString(0, maxLen) : sb.ToString();
    }

    /// <summary>A plain word-ish string (letters, digits, '_', '.') for keys and ids.</summary>
    public string Word(int maxLen = 16)
    {
        const string a = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.";
        var sb = new StringBuilder();
        int n = Int(0, maxLen);
        for (int i = 0; i < n; i++) sb.Append(a[Below(a.Length)]);
        return sb.ToString();
    }

    /// <summary>Text with 1..8 random edits: a character changed, a span cut, repeated or swapped, a nasty piece inserted, the end cut off.</summary>
    public string Mutate(string s, int maxLen = 1 << 16)
    {
        var sb = new StringBuilder(s);
        int edits = Int(1, 8);
        for (int e = 0; e < edits; e++)
        {
            int n = sb.Length;
            int at = Below(n + 1);
            switch (Below(7))
            {
                case 0: if (n > 0) sb[Math.Min(at, n - 1)] = (char)(Bool() ? Int(0x20, 0x7E) : Int(0, 0xFFFF)); break;
                case 1: if (n > 0) { int len = Int(1, Math.Max(1, Math.Min(32, n - Math.Min(at, n - 1)))); sb.Remove(Math.Min(at, n - 1), Math.Min(len, n - Math.Min(at, n - 1))); } break;
                case 2: if (n > 0 && sb.Length < maxLen) { int a0 = Below(n); int len = Int(1, Math.Min(64, n - a0)); sb.Insert(at, sb.ToString(a0, len)); } break;
                case 3: sb.Insert(at, Pick(Pieces)); break;
                case 4: sb.Length = at; break;
                case 5: if (n > 1) { int i = Below(n), j = Below(n); (sb[i], sb[j]) = (sb[j], sb[i]); } break;
                default: sb.Insert(at, Text(40)); break;
            }
            if (sb.Length > maxLen) sb.Length = maxLen;
        }
        return sb.ToString();
    }

    /// <summary>Bytes with 1..16 random edits (bit flips, cut, repeats, set to 0x00 / 0xFF / 0x7F / 0x80, truncation, junk appended).</summary>
    public byte[] Mutate(byte[] b, int maxLen = 1 << 24)
    {
        var l = new List<byte>(b);
        int edits = Int(1, 16);
        for (int e = 0; e < edits; e++)
        {
            int n = l.Count, at = Below(n);
            switch (Below(7))
            {
                case 0: if (n > 0) l[at] ^= (byte)(1 << Below(8)); break;
                case 1: if (n > 0) l[at] = Pick(new byte[] { 0, 0xFF, 0x7F, 0x80, 1 }); break;
                case 2: if (n > 0) l[at] = (byte)Below(256); break;
                case 3: if (n > 0) { int len = Int(1, Math.Min(256, n - at)); l.RemoveRange(at, len); } break;
                case 4: if (n > 0 && l.Count < maxLen) { int len = Int(1, Math.Min(256, n - at)); l.InsertRange(Below(n + 1), l.GetRange(at, len)); } break;
                case 5: l.RemoveRange(at, n - at); break;
                default: for (int k = Int(1, 32); k > 0; k--) l.Insert(Below(l.Count + 1), (byte)Below(256)); break;
            }
        }
        return l.ToArray();
    }

    /// <summary>A JSON value of a random kind (often not the kind the reader expects).</summary>
    public string JsonValue(int depth = 0) => Below(depth > 3 ? 6 : 9) switch
    {
        0 => "null",
        1 => Bool() ? "true" : "false",
        2 => Double().ToString("R", CultureInfo.InvariantCulture).Replace("NaN", "\"NaN\"").Replace("∞", "1e999"),
        3 => JsonSerializer.Serialize(Text(80)),
        4 => JsonSerializer.Serialize(Double().ToString("R", CultureInfo.InvariantCulture)),
        5 => IntNasty().ToString(CultureInfo.InvariantCulture),
        6 => "[" + string.Join(",", Enumerable.Range(0, Int(0, 4)).Select(_ => JsonValue(depth + 1))) + "]",
        7 => "{" + string.Join(",", Enumerable.Range(0, Int(0, 4)).Select(_ => JsonSerializer.Serialize(Word()) + ":" + JsonValue(depth + 1))) + "}",
        _ => new string('[', Int(1, 400)),   // a nesting bomb (never closed)
    };

    // ---- the run
    public string Target { get; private set; } = "";
    public int Case { get; private set; }
    readonly List<string> _fails = [];
    int _failCount, _targetFails;
    readonly List<string> _notes = [];

    /// <summary>A failure when <paramref name="ok"/> is false. Returns ok (so a target can stop a case early).</summary>
    public bool Expect(bool ok, string what)
    {
        if (ok) return true;
        _failCount++;
        if (_targetFails++ < 20) _fails.Add($"{Target} case {Case}: {what}");
        return false;
    }

    /// <summary>A line for the report (e.g. a count of refusals: a rule that never fires is a failed test).</summary>
    public void Note(string line) { if (_notes.Count < 200) _notes.Add($"{Target}: {line}"); }

    /// <summary>Counters a target keeps across its cases (e.g. how many files were refused); shown in the report.</summary>
    public readonly Dictionary<string, long> Counts = new(StringComparer.Ordinal);
    public void Count(string what, long n = 1) => Counts[$"{Target}.{what}"] = Counts.GetValueOrDefault($"{Target}.{what}") + n;
    public long Counted(string what) => Counts.GetValueOrDefault($"{Target}.{what}");

    /// <summary>A scratch folder for this run (deleted at the end).</summary>
    public string Scratch { get; } = Path.Combine(Path.GetTempPath(), "pza-fuzz-" + Environment.ProcessId);

    public sealed record TargetDef(string Name, int Cases, Action<PzaFuzz> Body, Action<PzaFuzz>? After = null);

    sealed record Result(string Name, int Cases, int Failures, long Ms, List<string> Lines);

    /// <summary>Runs the targets and writes the report. Arguments: --seed N, --cases-scale X (0.1 for the mutation check), --only A,B, --case N,
    /// --report DIR, --commit SHA, --dirty true|false, --budget SECONDS (per target), --hang SECONDS. Returns the exit code.</summary>
    public static int Run(string mod, string[] args, params TargetDef[] targets)
    {
        ulong seed = 20261118; double scale = 1; HashSet<string>? only = null; int? oneCase = null;
        string report = Path.Combine(AppContext.BaseDirectory, "report"), commit = "", dirty = "";
        double budget = 60, hang = 30;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            string v = args[i + 1];
            switch (args[i])
            {
                case "--seed": seed = ulong.Parse(v, CultureInfo.InvariantCulture); i++; break;
                case "--cases-scale": scale = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
                case "--only": only = [.. v.Split(',', StringSplitOptions.RemoveEmptyEntries)]; i++; break;
                case "--case": oneCase = int.Parse(v, CultureInfo.InvariantCulture); i++; break;
                case "--report": report = v; i++; break;
                case "--commit": commit = v; i++; break;
                case "--dirty": dirty = v; i++; break;
                case "--budget": budget = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
                case "--hang": hang = double.Parse(v, CultureInfo.InvariantCulture); i++; break;
            }
        }
        try { Console.OutputEncoding = Encoding.UTF8; } catch (IOException) { }
        var fz = new PzaFuzz();
        Directory.CreateDirectory(fz.Scratch);
        var results = new List<Result>();
        var started = DateTime.Now;
        long caseStartTicks = Stopwatch.GetTimestamp();
        string where = "";
        var watchdog = new Thread(() =>
        {
            while (true)
            {
                Thread.Sleep(500);
                double s = (Stopwatch.GetTimestamp() - Volatile.Read(ref caseStartTicks)) / (double)Stopwatch.Frequency;
                if (s > hang)
                {
                    string line = $"HANG: {where} ran longer than {hang:0} s";
                    Console.WriteLine(line);
                    fz._fails.Add(line);
                    Write(report, mod, seed, commit, dirty, started, results.Append(new Result(fz.Target, fz.Case, 1, (long)(s * 1000), [line])).ToList(), fz, false);
                    Console.WriteLine("RESULT: FAIL (hang)");
                    Console.Out.Flush();
                    Environment.Exit(3);
                }
            }
        }) { IsBackground = true };
        watchdog.Start();

        foreach (var t in targets)
        {
            if (only is not null && !only.Contains(t.Name)) continue;
            fz.Target = t.Name;
            int before = fz._failCount, failLines = fz._fails.Count;
            fz._targetFails = 0;
            int n = Math.Max(1, (int)(t.Cases * scale)), ran = 0;
            var sw = Stopwatch.StartNew();
            for (int c = oneCase ?? 0; c < (oneCase is null ? n : oneCase.Value + 1); c++)
            {
                if (sw.Elapsed.TotalSeconds > budget) { fz.Note($"time budget {budget:0} s reached after {ran} of {n} cases"); break; }
                fz.Case = c;
                fz._s = seed ^ Hash(t.Name) ^ ((ulong)c * 0xD1B54A32D192ED03UL);
                where = $"{t.Name} case {c} (replay: --seed {seed} --only {t.Name} --case {c})";
                Volatile.Write(ref caseStartTicks, Stopwatch.GetTimestamp());
                try { t.Body(fz); }
                catch (Exception e) { fz.Expect(false, $"threw {e.GetType().Name}: {e.Message} at {FirstFrame(e)}"); }
                ran++;
            }
            fz.Case = -1;
            if (t.After is not null)
                try { t.After(fz); } catch (Exception e) { fz.Expect(false, $"after: threw {e.GetType().Name}: {e.Message}"); }
            if (ran == 0) fz.Expect(false, "ran no case");
            int failed = fz._failCount - before;
            results.Add(new Result(t.Name, ran, failed, sw.ElapsedMilliseconds, fz._fails.Skip(failLines).ToList()));
            Console.WriteLine($"{(failed == 0 ? "ok  " : "FAIL")} {t.Name}: {ran} cases, {failed} failure(s), {sw.ElapsedMilliseconds} ms");
            foreach (var l in fz._fails.Skip(failLines)) Console.WriteLine("     " + l);
        }
        Volatile.Write(ref caseStartTicks, long.MaxValue / 4);
        bool pass = results.Count > 0 && results.All(r => r.Failures == 0);
        if (results.Count == 0) Console.WriteLine("FAIL no target ran (--only names nothing?)");
        foreach (var l in fz._notes) Console.WriteLine("note " + l);
        foreach (var (k, v) in fz.Counts.OrderBy(k => k.Key)) Console.WriteLine($"count {k} = {v}");
        Write(report, mod, seed, commit, dirty, started, results, fz, pass);
        try { Directory.Delete(fz.Scratch, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Console.WriteLine(pass ? "RESULT: PASS" : $"RESULT: FAIL ({results.Sum(r => r.Failures)})");
        return pass ? 0 : 1;
    }

    static string FirstFrame(Exception e)
    {
        // the first frame in our own code (PZA's or the harness's), else the innermost one
        var frames = new StackTrace(e, true).GetFrames()?.Where(x => x.GetMethod() is not null).ToList() ?? [];
        var f = frames.FirstOrDefault(x => x.GetMethod()!.DeclaringType?.Namespace is not { } ns || !(ns.StartsWith("System", StringComparison.Ordinal)
            || ns.StartsWith("Microsoft", StringComparison.Ordinal) || ns.StartsWith("Meitou", StringComparison.Ordinal))) ?? frames.FirstOrDefault();
        return f is null ? "?" : $"{f.GetMethod()!.DeclaringType?.Name}.{f.GetMethod()!.Name}" + (f.GetFileLineNumber() > 0 ? $":{f.GetFileLineNumber()}" : "");
    }

    static void Write(string dir, string mod, ulong seed, string commit, string dirty, DateTime started, List<Result> results, PzaFuzz fz, bool pass)
    {
        Directory.CreateDirectory(dir);
        var json = new
        {
            mod, kit = "PZA's Meitou UI fuzz kit 1.1", result = pass ? "PASS" : "FAIL", seed, commit, dirty,
            started = started.ToString("s", CultureInfo.InvariantCulture), finished = DateTime.Now.ToString("s", CultureInfo.InvariantCulture),
            targets = results.Select(r => new { name = r.Name, cases = r.Cases, failures = r.Failures, ms = r.Ms, lines = r.Lines }),
            counts = fz.Counts, notes = fz._notes,
        };
        var lenient = new UTF8Encoding(false, false);   // a failure line may quote a lone surrogate
        File.WriteAllText(Path.Combine(dir, "fuzz-report.json"), JsonSerializer.Serialize(json, new JsonSerializerOptions { WriteIndented = true }), lenient);
        var sb = new StringBuilder();
        sb.AppendLine($"{mod} fuzz {(pass ? "PASS" : "FAIL")}  seed {seed}  commit {commit}{(dirty == "true" ? " (+ uncommitted changes)" : "")}  {started:s}");
        foreach (var r in results)
        {
            sb.AppendLine($"{(r.Failures == 0 ? "ok  " : "FAIL")} {r.Name,-28} {r.Cases,7} cases {r.Failures,4} failures {r.Ms,7} ms");
            foreach (var l in r.Lines) sb.AppendLine("     " + l);
        }
        foreach (var l in fz._notes) sb.AppendLine("note " + l);
        foreach (var (k, v) in fz.Counts.OrderBy(k => k.Key)) sb.AppendLine($"count {k} = {v}");
        File.WriteAllText(Path.Combine(dir, "fuzz-report.txt"), sb.ToString(), lenient);
    }
}
