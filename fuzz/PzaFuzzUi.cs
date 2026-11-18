// PZA's Meitou UI: fuzz targets for the shared library, which every PZA Meitou mod compiles in. Author: PZA. Licence: GPLv3 or later.
// Each mod's harness adds these with its own settings class and string table, so the settings file and language files are fuzzed with the
// keys that mod really has:
//   settings.keep        <mod id>.settings.json (PzaSettings.Keep): random / mutated / hostile files; never throws, logs a line, and every
//                        setting afterwards is one the Settings page could hold (a slider finite and inside Min..Max, a list one of its options)
//   settings.roundtrip   random valid values set through PzaSettings.Set survive a restart exactly (Keep on a fresh settings object)
//   strings.load         language files (PzaStrings.Load): random lines, escapes, placeholders, bad UTF-8; a text used has no control character
//                        but '\n' and the same {1}..{9} set as English; T / TF / KeyOf never throw
//   fit.charwrap         PzaFit.CharWrap: no row longer than the width unless it is one word; the words survive in order
//   font.parse           the TrueType reader (PzaFontFile) on mutated copies of Kenshi's own fonts (read only): refuses or measures, never throws,
//                        never takes long, every measure finite
//   font.wrap            PzaFont.Wrap / Ellipsize in the four languages' real fonts: every wrapped line fits unless it is one character; the
//                        non-space characters survive in order; an ellipsized line fits
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Meitou.Modding;

namespace PZA.MeitouUi.Fuzz;

public static class PzaFuzzUi
{
    public static string KenshiDir { get; set; } = Environment.GetEnvironmentVariable("PZA_KENSHI") is { Length: > 0 } k ? k
        : @"C:\Program Files (x86)\Steam\steamapps\common\Kenshi";

    /// <summary>A Meitou settings group for <paramref name="settings"/>, as context.AddSettings makes it (its constructor is internal).</summary>
    public static ModSettingsGroup Group(string modId, object settings)
    {
        var ctor = typeof(ModSettingsGroup).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public)
            .First(c => c.GetParameters().Length == 3);
        try { return (ModSettingsGroup)ctor.Invoke([modId, modId, settings]); }
        catch (TargetInvocationException e) when (e.InnerException is not null) { throw e.InnerException; }
    }

    /// <summary>Whether a setting holds a value the Settings page could show.</summary>
    public static string? Invalid(ModSetting s)
    {
        object? v = s.Get();
        switch (s.Kind)
        {
            case ModSettingKind.Switch: return v is bool ? null : $"{s.Key} is not a bool ({v})";
            case ModSettingKind.Slider:
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                return double.IsFinite(d) && d >= s.Min - 1e-9 && d <= s.Max + 1e-9 ? null : $"{s.Key} = {d} outside {s.Min}..{s.Max}";
            default: return s.Options.Contains(s.Text) ? null : $"{s.Key} = '{s.Text}' is not one of its options";
        }
    }

    /// <summary>A value text a kept file might hold for <paramref name="s"/>: often valid, often hostile.</summary>
    static string ValueText(PzaFuzz fz, ModSetting s) => fz.Below(7) switch
    {
        6 => fz.Pick(new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity }).ToString(CultureInfo.InvariantCulture),
        0 => fz.Text(60),
        1 => fz.Double(s.Min, s.Max).ToString("R", CultureInfo.InvariantCulture),
        2 => fz.Double(s.Min, s.Max).ToString("R", CultureInfo.GetCultureInfo("de-DE")),   // a comma decimal
        3 => s.Kind == ModSettingKind.List && s.Options.Count > 0 ? fz.Pick(s.Options) : fz.Bool() ? "True" : "false",
        4 => s.Kind == ModSettingKind.List && s.Options.Count > 0 ? fz.Mutate(fz.Pick(s.Options)) : fz.Pick(new[] { "TRUE", " true", "1", "on", "" }),
        _ => s.Text,
    };

    /// <summary>The settings targets for one mod. <paramref name="make"/> makes the mod's settings object with its defaults.</summary>
    public static PzaFuzz.TargetDef[] Settings(string modId, Func<object> make, int cases = 3000) =>
    [
        new("settings.keep", cases, fz =>
        {
            string dir = Path.Combine(fz.Scratch, "keep"); Directory.CreateDirectory(dir);
            string path = PzaSettings.FileFor(dir, modId);
            var g = Group(modId, make());
            string body;
            switch (fz.Below(10))
            {
                case 0: body = fz.JsonValue(); break;
                case 1: body = fz.Text(2000); break;
                default:
                    // written by hand: values of the wrong JSON kind (numbers, arrays, nesting bombs) and duplicate keys get in too
                    var parts = new List<string>();
                    foreach (var s in g.Settings) if (fz.Chance(0.7)) parts.Add(Js(s.Key) + ":" + (fz.Chance(0.85) ? Js(ValueText(fz, s)) : fz.JsonValue()));
                    for (int i = fz.Int(0, 3); i > 0; i--) parts.Add(Js(fz.Chance(0.5) ? fz.Word() : fz.Text(20)) + ":" + Js(fz.Text(20)));
                    if (fz.Chance(0.1) && parts.Count > 0) parts.Add(fz.Pick(parts));
                    body = "{" + string.Join(fz.Pick(new[] { ",", ", ", ",\r\n  " }), parts) + "}";
                    if (fz.Chance(0.25)) body = fz.Mutate(body);
                    break;
            }
            byte[] bytes = Lenient.GetBytes(body);   // lone surrogates become U+FFFD, as any editor would save them
            if (fz.Chance(0.05)) bytes = fz.Mutate(bytes);
            File.WriteAllBytes(path, bytes);
            string written = Lenient.GetString(bytes);   // what Keep really reads
            var lines = PzaSettings.Keep(g, dir);
            fz.Expect(lines.Count > 0 && lines[0].StartsWith("PZA's Meitou UI:", StringComparison.Ordinal), "Keep logged no line");
            if (lines.Count > 0 && lines[0].Contains("could not read", StringComparison.Ordinal)) fz.Count("unreadable");
            else if (lines.Count > 0 && lines[0].Contains("ignored", StringComparison.Ordinal)) fz.Count("someIgnored");
            else fz.Count("read");
            foreach (var s in g.Settings) if (Invalid(s) is { } why) fz.Expect(false, "after Keep: " + why + " (file: " + Short(body) + ")");
            // a kept slider text that is not a finite number is refused by name, never handed to Meitou (whose own clamp is not ours to rely on)
            if (lines.Count > 0 && !lines[0].Contains("could not read", StringComparison.Ordinal))
                foreach (var s in g.Settings.Where(x => x.Kind == ModSettingKind.Slider))
                    if (KeptText(written, s.Key) is { } kt && double.TryParse(kt, NumberStyles.Float, CultureInfo.InvariantCulture, out double kd) && !double.IsFinite(kd))
                    {
                        fz.Count("nonFiniteSeen");
                        fz.Expect(lines[0].Contains("ignored", StringComparison.Ordinal) && lines[0].Contains(s.Key, StringComparison.Ordinal),
                            $"{s.Key} = '{kt}' (not a finite number) was not refused: {lines[0]}");
                    }
            File.Delete(path);
        }, fz =>
        {
            // a rule that never fires is a failed test: hostile files must have been refused, and plain ones read
            fz.Expect(fz.Counted("unreadable") > 0, "no file was ever refused as unreadable");
            fz.Expect(fz.Counted("someIgnored") > 0, "no value was ever ignored");
            fz.Expect(fz.Counted("read") > 0, "no file was ever read cleanly");
            if (Group(modId, make()).Settings.Any(x => x.Kind == ModSettingKind.Slider))
                fz.Expect(fz.Counted("nonFiniteSeen") > 0, "no NaN / infinity slider text was ever tried");
        }),
        new("settings.roundtrip", Math.Max(1, cases / 10), fz =>   // slow: every Set writes the file, as in the game
        {
            string dir = Path.Combine(fz.Scratch, "round"); Directory.CreateDirectory(dir);
            string path = PzaSettings.FileFor(dir, modId);
            if (File.Exists(path)) File.Delete(path);
            var t1 = make(); var g1 = Group(modId, t1);
            PzaSettings.Keep(g1, dir);
            foreach (var s in g1.Settings)
            {
                if (!fz.Chance(0.7)) continue;
                object v = s.Kind switch
                {
                    ModSettingKind.Switch => fz.Bool(),
                    ModSettingKind.Slider => s.Whole ? (object)(int)Math.Round(fz.Range(s.Min, s.Max)) : fz.Double(s.Min, s.Max),
                    _ => fz.Pick(s.Options),
                };
                if (v is double dv && !double.IsFinite(dv)) continue;   // the mod never sets those itself (Set would throw on NaN)
                PzaSettings.Set(g1, t1, s.Key, v);
            }
            var want = g1.Settings.ToDictionary(s => s.Key, s => s.Text);
            var g2 = Group(modId, make());
            var lines = PzaSettings.Keep(g2, dir);
            foreach (var s in g2.Settings)
                fz.Expect(s.Text == want[s.Key], $"{s.Key} came back as '{s.Text}', was '{want[s.Key]}' ({lines.FirstOrDefault()})");
        }),
    ];

    /// <summary>The language-file target for one mod's English table.</summary>
    public static PzaFuzz.TargetDef Strings((string Key, string En)[] table, int cases = 3000) => new("strings.load", cases, fz =>
    {
        var st = new PzaStrings(table);
        var sb = new StringBuilder();
        if (fz.Chance(0.1)) sb.Append('\ufeff');
        for (int n = fz.Int(0, 40); n > 0; n--)
        {
            var (key, en) = table.Length > 0 && fz.Chance(0.8) ? fz.Pick(table) : (fz.Word(), "");
            string val = fz.Below(4) switch { 0 => fz.Text(120), 1 => en, 2 => fz.Mutate(en.Length > 0 ? en : "x"), _ => en.Replace("\n", "\\n") };
            sb.Append(fz.Pick(new[] { "", " ", "\t", "#", ";" })).Append(key).Append(fz.Pick(new[] { "=", " = ", "\t=\t", "==", "" })).Append(val)
              .Append(fz.Pick(new[] { "\n", "\r\n", "\r", "" }));
        }
        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        if (fz.Chance(0.15)) data = fz.Mutate(data);
        var into = new Dictionary<string, string>(StringComparer.Ordinal);
        var (used, refused) = st.Load(data, into);
        fz.Count(used > 0 ? "someUsed" : "noneUsed");
        if (refused.Count > 0) fz.Count("someRefused");
        fz.Expect(used == into.Count, $"used {used} but {into.Count} texts kept");
        var en0 = table.ToDictionary(t => t.Key, t => t.En, StringComparer.Ordinal);
        foreach (var (k, v) in into)
        {
            fz.Expect(en0.ContainsKey(k), $"kept unknown key '{k}'");
            fz.Expect(!v.Any(c => c < 0x20 && c != '\n'), $"{k}: a control character got through: {Short(v)}");
            if (en0.TryGetValue(k, out var e)) fz.Expect(Placeholders(v) == Placeholders(e), $"{k}: placeholders {Placeholders(v)} differ from English {Placeholders(e)}");
        }
        foreach (var (k, _) in table.Take(8))
        {
            fz.Expect(st.T(k) is not null, $"T({k}) null");
            st.TF(k, fz.Text(10), fz.Text(10));
        }
        st.KeyOf(fz.Text(40));
    }, fz =>
    {
        fz.Expect(fz.Counted("someUsed") > 0, "no line was ever used");
        fz.Expect(fz.Counted("someRefused") > 0, "no line was ever refused");
    });

    static string Placeholders(string s) =>
        string.Concat(System.Text.RegularExpressions.Regex.Matches(s, @"\{([1-9])\}").Select(m => m.Groups[1].Value).Distinct().OrderBy(x => x));

    /// <summary>The shared library's own targets (text wrap, the font reader and the real fonts).</summary>
    public static PzaFuzz.TargetDef[] Library(int cases = 2000) =>
    [
        new("fit.charwrap", cases * 2, fz =>
        {
            // mostly words and spaces, with widths near the words' lengths (a wide row hides nothing)
            string text = fz.Chance(0.3) ? fz.Text(600) : string.Join(fz.Pick(new[] { " ", "  ", " ", "\n" }), Enumerable.Range(0, fz.Int(1, 40)).Select(_ => fz.Word(12)));
            int width = fz.Chance(0.05) ? fz.Int(-2, 0) : fz.Int(1, Math.Max(2, Math.Min(120, text.Length / 2)));
            var rows = PzaFit.CharWrap(text, width);
            fz.Expect(rows.Count >= 1, "no row");
            foreach (var r in rows)
                fz.Expect(r.Length <= Math.Max(width, 0) || !r.Contains(' '), $"row of {r.Length} > width {width} with a space in it");
            static IEnumerable<string> Words(string t) => t.Replace("\\n", "\n").Replace("\r", "").Split('\n').SelectMany(p => p.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            fz.Expect(Words(text).SequenceEqual(rows.SelectMany(r => r.Split(' ', StringSplitOptions.RemoveEmptyEntries))), "words lost or reordered");
        }),
        new("font.parse", cases, fz =>
        {
            var seeds = FontSeeds();
            if (!fz.Expect(seeds.Count > 0, $"no Kenshi font found under {KenshiDir} (set PZA_KENSHI)")) return;
            var (name, good, spec) = fz.Pick(seeds);
            byte[] bad = fz.Mutate(good, good.Length + 4096);
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var file = PzaFontFile.Parse(bad, name);
            fz.Count(file is null ? "refused" : "parsed");
            fz.Expect(t0.ElapsedMilliseconds < 3000, $"reading a broken {name} took {t0.ElapsedMilliseconds} ms");
            if (file is null) return;
            var font = MakeFont(file, spec, fz.Pick(new[] { 720, 1080, 1440, 2160 }));
            if (font is null) { fz.Count("fontRefused"); return; }
            string text = fz.Text(200);
            float w = font.Width(text.Replace("\n", " "));
            fz.Expect(float.IsFinite(w) && w >= 0, $"Width {w} on a broken {name}");
            fz.Expect(float.IsFinite(font.LineHeight) && font.LineHeight >= 0, $"LineHeight {font.LineHeight} on a broken {name}");
            font.Wrap(text, fz.Float(1, 800));
            font.Ellipsize(text.Replace("\n", " "), fz.Float(1, 800));
            fz.Expect(t0.ElapsedMilliseconds < 3000, $"measuring with a broken {name} took {t0.ElapsedMilliseconds} ms");
        }, fz =>
        {
            fz.Expect(fz.Counted("refused") > 0, "no broken font was ever refused");
            fz.Expect(fz.Counted("parsed") > 0, "no mutated font ever parsed (the mutations are too coarse to reach the measuring)");
        }),
        new("font.wrap", cases, fz =>
        {
            string lang = fz.Pick(PzaFitLanguages);
            var font = PzaFont.FromInstall(KenshiDir, lang, "Kenshi_StandardFont_Medium", fz.Pick(new[] { 720, 1080, 1440, 2160 }));
            if (!fz.Expect(font is not null, $"no {lang} font in {KenshiDir}")) return;
            string text = fz.Text(500);
            float width = fz.Chance(0.1) ? fz.Float(-5, 5) : fz.Float(10, 900);
            var lines = font!.Wrap(text, width);
            float lim = float.IsNaN(width) ? 0 : width;   // Wrap treats a NaN width as 0
            foreach (var l in lines)
                fz.Expect(new System.Globalization.StringInfo(l).LengthInTextElements <= 1 || font.Width(l) <= lim + 0.01f, $"[{lang}] line '{Short(l)}' {font.Width(l):0.0} px in {width:0.0} px");
            static string Solid(string t) => new(PzaFont.StripColours(t.Replace("\\n", "\n")).Where(c => !char.IsWhiteSpace(c)).ToArray());
            fz.Expect(Solid(string.Concat(lines)) == Solid(text) || text.Contains('#'), $"[{lang}] characters lost or reordered by Wrap");
            string one = text.Replace("\n", " ").Replace("\r", " ");
            string e = font.Ellipsize(one, width);
            fz.Expect(e == one || font.Width(e) <= lim + 0.01f || e == "...", $"[{lang}] Ellipsize gave {font.Width(e):0.0} px in {width:0.0} px");
        }),
    ];

    static readonly string[] PzaFitLanguages = ["en", "ru", "zh", "ja"];
    static List<(string Name, byte[] Bytes, object Spec)>? s_seeds;

    /// <summary>Kenshi's own fonts (read only, never changed), each with the MyGUI spec it is loaded with.</summary>
    static List<(string, byte[], object)> FontSeeds()
    {
        if (s_seeds is not null) return s_seeds;
        s_seeds = [];
        foreach (var lang in PzaFitLanguages)
        {
            var f = PzaFont.FromInstall(KenshiDir, lang, "Kenshi_StandardFont_Medium", 1080);
            if (f is null || s_seeds.Any(s => s.Name == Path.GetFileName(f.FilePath))) continue;
            var spec = typeof(PzaFont).GetField("_spec", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f)!;
            s_seeds.Add((Path.GetFileName(f.FilePath), File.ReadAllBytes(f.FilePath), spec));
        }
        return s_seeds;
    }

    static PzaFont? MakeFont(PzaFontFile file, object spec, int height)
    {
        var ctor = typeof(PzaFont).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).First(c => c.GetParameters().Length == 3);
        try { return (PzaFont)ctor.Invoke([file, spec, (float)height]); }
        catch (TargetInvocationException e) when (e.InnerException is InvalidDataException or ArgumentException or IndexOutOfRangeException)
        { return null; }   // Meitou's own size reader refusing a broken file the same way is fine; any other throw is a finding
        catch (TargetInvocationException e) when (e.InnerException is not null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(e.InnerException); throw; }
    }

    static readonly UTF8Encoding Lenient = new(false, false);

    /// <summary>The text a kept file holds for <paramref name="key"/> when the file is a flat object of strings (as Keep reads it); else null.</summary>
    static string? KeptText(string body, string key)
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(body)?.GetValueOrDefault(key); }
        catch (Exception e) when (e is JsonException or NotSupportedException or ArgumentException) { return null; }
    }
    static string Js(string s) => JsonSerializer.Serialize(s);

    public static string Short(string s) => (s.Length > 120 ? s[..120] + "..." : s).Replace("\n", "\\n").Replace("\r", "\\r");
}
