// PZA's Meitou UI: the text-fit check. Author: PZA. Licence: GPLv3 or later. Compiled into every PZA Meitou mod's OFFLINE check tool (never into a
// mod): <Compile Include="..\..\..\..\PZAMeitouUi\check\*.cs" />.
//
// Opens a window on a real MyGuiScreenHost with Kenshi's own skins, in English, Russian, Chinese and Japanese, at 1280x720, 1920x1080 and
// 2560x1440, clicks through every tab, and measures every text it shows (labels, buttons, tab captions, the title, every line of every list)
// in the font that language really uses (PzaFont: Kenshi's locale fonts; the larger of Meitou's two looks, 16 px at 1080 lines in game) against
// the place its widget gives it. The library's fit pass (PzaFit) is OFF here: a layout that only fits by being cut fails. Each overflow names
// the language, screen size, tab, widget, the text and its string key. Also reported per tab: how much of the page height the content uses.
//
// What would make it lie: InterfaceScale above 1 (a player's setting) or a bigger "font size2" make text larger than measured here; Meitou's
// own look kerns (a little narrower than measured). A clipped list row beyond the visible rows is measured from the list's lines, not drawn.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using Meitou.Data.Ui;
using Meitou.Game.Ui;

namespace PZA.MeitouUi;

public static class PzaFitCheck
{
    public sealed record Overflow(string Lang, string Screen, string Tab, string Widget, string Text, string? Key, string Why)
    {
        public override string ToString() =>
            $"[{Lang} {Screen}] tab '{Tab}' widget '{Widget}': {Why}: \"{Short(Text)}\"" + (Key is null ? "" : $" (string {Key})");
    }

    public static readonly string[] Languages = ["en", "ru", "zh", "ja"];
    public static readonly (int W, int H)[] Screens = [(1280, 720), (1920, 1080), (2560, 1440)];

    /// <summary>Runs the check on <paramref name="window"/>. <paramref name="bind"/> is the check's own binding for a screen id (the game's default
    /// then the library's ApplyBind); <paramref name="setLanguage"/> switches the mod's strings; <paramref name="keyOf"/> names a shown text's key.
    /// <paramref name="prepare"/> runs after each open (e.g. to pick a list line so a detail page shows). Returns every overflow; prints a summary
    /// line per language and screen, and the fill per tab at 1920x1080 in English.</summary>
    public static List<Overflow> Run(string kenshiDir, MyGuiResources res, PzaWindow window, Func<MyGuiScreenHost, string, UiBindings> bind,
        Action<string> setLanguage, Func<string, string?>? keyOf = null, Action<string>? say = null, IEnumerable<string>? languages = null,
        Action<MyGuiScreenHost>? prepare = null)
    {
        say ??= Console.WriteLine;
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (System.IO.IOException) { }
        var all = new List<Overflow>();
        bool wasEnabled = PzaFit.Enabled;
        string? wasDir = PzaFit.KenshiDir;
        PzaFit.Enabled = false;
        PzaFit.KenshiDir = kenshiDir;
        try
        {
            foreach (var lang in languages ?? Languages)
            {
                PzaFit.Lang = lang;
                setLanguage(lang);
                foreach (var (w, h) in Screens)
                {
                    var found = One(res, window, bind, lang, w, h, keyOf, prepare, lang == "en" && h == 1080 ? say : null, out int texts);
                    say($"  fit [{lang} {w}x{h}]: {texts} texts measured, {found.Count} overflow(s)");
                    all.AddRange(found);
                }
            }
        }
        finally
        {
            PzaFit.Enabled = wasEnabled;
            PzaFit.KenshiDir = wasDir;
            PzaFit.Lang = "en";
            setLanguage("en");
        }
        foreach (var o in all.Take(60)) say("  OVERFLOW " + o);
        if (all.Count > 60) say($"  ... and {all.Count - 60} more");
        return all;
    }

    static List<Overflow> One(MyGuiResources res, PzaWindow window, Func<MyGuiScreenHost, string, UiBindings> bind, string lang, int W, int H,
        Func<string, string?>? keyOf, Action<MyGuiScreenHost>? prepare, Action<string>? fill, out int texts)
    {
        var host = new MyGuiScreenHost(res);
        var found = new List<Overflow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        texts = 0;
        string screen = $"{W}x{H}";
        UiBindings? lastBind = null;
        IReadOnlyList<UiControl> Arrange() =>
            host.Arrange(W, H, id => { var b = bind(host, id); b.TemplateScale = H / 1080f; if (id == window.Id) lastBind = b; return b; }).First(f => f.Id == window.Id).Controls;

        PzaUi.Frame(host, W, H);
        window.Open();
        PzaUi.Frame(host, W, H);
        if (!host.IsOpen(window.Id)) { found.Add(new(lang, screen, "-", window.Id, "", null, "the window did not open")); return found; }
        Arrange(); PzaUi.Frame(host, W, H);
        prepare?.Invoke(host);
        var first = Arrange();
        Arrange();   // a second pass: list widths and paragraphs wrapped from the first layout

        var root = first.First(c => c.Type == "Window");
        if (root.Bounds.X < -0.5f || root.Bounds.Y < -0.5f || root.Bounds.X + root.Bounds.Width > W + 0.5f || root.Bounds.Y + root.Bounds.Height > H + 0.5f)
            found.Add(new(lang, screen, "-", root.Name, "", null, $"the window is off the screen ({root.Bounds.X:0},{root.Bounds.Y:0} {root.Bounds.Width:0}x{root.Bounds.Height:0})"));

        var tabs = first.Where(c => c.Name.Contains("@tab", StringComparison.Ordinal) && !c.Name.Contains("@row", StringComparison.Ordinal))
            .Select(c => c.Name).Distinct().ToList();
        var pages = tabs.Count == 0 ? new List<string?> { null } : tabs.Select(t => (string?)t).ToList();
        foreach (var tab in pages)
        {
            string tabName = "-";
            if (tab is not null)
            {
                var t = Arrange().FirstOrDefault(c => c.Name == tab);
                if (t is null) continue;
                tabName = t.Caption.Length > 0 ? t.Caption : tab;
                var at = new Vector2(t.Bounds.X + t.Bounds.Width / 2, t.Bounds.Y + t.Bounds.Height / 2);
                if (Environment.GetEnvironmentVariable("PZA_FIT_DEBUG") == "1")
                    Console.WriteLine($"    click {tab} at {at} hit={host.HitTest(at)?.Control.Name} captures={host.CapturesPointer} tab={t.Bounds} last={string.Join(";", host.LastFrame.First(f => f.Id == window.Id).Controls.Where(c => c.Name == tab || c.Name == "Toggle").Select(c => c.Name + c.Bounds))}");
                host.PointerDown(at); host.PointerUp();
                PzaUi.Frame(host, W, H); Arrange(); PzaUi.Frame(host, W, H);
            }
            var cs = Arrange();
            Arrange();
            cs = Arrange();
            int m = Measure(res, cs, lastBind, lang, screen, tabName, H, keyOf, found, seen);
            if (tab is not null) UnderStrip(cs, tab, lang, screen, tabName, found, seen);
            texts += m;
            if (Environment.GetEnvironmentVariable("PZA_FIT_DEBUG") == "1") Console.WriteLine($"    [{lang} {screen}] tab '{tabName}': {m} texts, {cs.Count} controls");
            if (fill is not null) Fill(cs, tabName, fill);
            Dump(cs, lastBind, window.Id, lang, W, H, tab is null ? 0 : int.Parse(tab[(tab.IndexOf("@tab", StringComparison.Ordinal) + 4)..], CultureInfo.InvariantCulture), tabName);
        }
        window.Close();
        PzaUi.Frame(host, W, H);
        return found;
    }

    static int Measure(MyGuiResources res, IReadOnlyList<UiControl> cs, UiBindings? b, string lang, string screen, string tab, int H,
        Func<string, string?>? keyOf, List<Overflow> found, HashSet<string> seen)
    {
        int n = 0;
        void Add(string widget, string text, string why)
        {
            if (seen.Add(widget + "\u0001" + text)) found.Add(new(lang, screen, tab, widget, text, keyOf?.Invoke(text), why));
        }
        foreach (var c in cs)
        {
            if (string.IsNullOrWhiteSpace(c.Caption) || c.Type == "EditBox" || c.Name.Contains("@row", StringComparison.Ordinal)) continue;
            if (PzaFit.TextRect(res, c, H) is not { } r || r.Width < 2) continue;
            if (PzaFit.Font(res, PzaFit.FontName(res, c), H) is not { } font) { Add(c.Name, c.Caption, $"font '{PzaFit.FontName(res, c)}' not found"); continue; }
            n++;
            string text = c.Caption.Replace("\\n", "\n");
            Glyphs(font, c.Name, text, Add);
            bool wrap = c.Properties.GetValueOrDefault("WordWrap") == "true";
            if (wrap)
            {
                var lines = font.Wrap(text, r.Width);
                float need = lines.Count * font.LineHeight;
                if (need > r.Height + 1)
                    Add(c.Name, text, $"{lines.Count} wrapped lines need {need:0} px, the box has {r.Height:0} px ({font.Source} {font.EmPixels:0.#} px)");
                foreach (var l in lines.Where(l => font.Width(l) > r.Width + 0.5f && l.Contains(' ')))
                    Add(c.Name, l, $"a wrapped line is {font.Width(l):0} px in {r.Width:0} px");
            }
            else
            {
                float wide = font.WidestLine(text);
                if (wide > r.Width + 0.5f) Add(c.Name, text, $"{wide:0} px wide in {r.Width:0} px ({font.Source} {font.EmPixels:0.#} px)");
                int lines = text.Split('\n').Length;
                if (lines > 1 && lines * font.LineHeight > r.Height + 1) Add(c.Name, text, $"{lines} lines need {lines * font.LineHeight:0} px, the box has {r.Height:0} px");
            }
        }
        // every line of every list shown on this page, against its rows' text width
        if (b is not null)
            foreach (var (list, items) in b.Lists)
            {
                var row = cs.FirstOrDefault(c => c.Name.StartsWith(list + "@row", StringComparison.Ordinal));
                if (row is null || PzaFit.TextRect(res, row, H) is not { } r) continue;
                if (PzaFit.Font(res, PzaFit.FontName(res, row), H) is not { } font) continue;
                foreach (var item in items)
                {
                    n++;
                    Glyphs(font, list, item.Caption, Add);
                    if (font.Width(item.Caption) > r.Width + 0.5f) Add(list, item.Caption, $"list line {font.Width(item.Caption):0} px wide in {r.Width:0} px");
                }
            }
        return n;
    }

    /// <summary>A page's control that starts above the bottom of its tab strip: it sits under the tabs (Meitou lays a TabItem over the whole
    /// TabControl, strip included).</summary>
    static void UnderStrip(IReadOnlyList<UiControl> cs, string tab, string lang, string screen, string tabName, List<Overflow> found, HashSet<string> seen)
    {
        string tabs = tab[..tab.IndexOf('@')];
        var strip = cs.Where(c => c.Name.StartsWith(tabs + "@tab", StringComparison.Ordinal)).ToList();
        var control = cs.FirstOrDefault(c => c.Name == tabs);
        if (strip.Count == 0 || control is null) return;
        float bottom = strip.Max(c => c.Bounds.Y + c.Bounds.Height);
        foreach (var c in cs)
        {
            if (c == control || c.Name.Contains('@') || c.Bounds.Height <= 0 || c.Type is "TabItem" or "TabControl" or "Window") continue;
            var b = c.Bounds;
            bool inside = b.X >= control.Bounds.X - 1 && b.X + b.Width <= control.Bounds.X + control.Bounds.Width + 1 && b.Y >= control.Bounds.Y - 1
                && b.Y + b.Height <= control.Bounds.Y + control.Bounds.Height + 1;
            if (inside && b.Y < bottom - 0.5f && seen.Add("strip\u0001" + c.Name))
                found.Add(new(lang, screen, tabName, c.Name, c.Caption, null, $"starts at y {b.Y:0}, under the tab strip (its bottom is {bottom:0})"));
        }
    }

    static void Glyphs(PzaFont font, string widget, string text, Action<string, string, string> add)
    {
        foreach (var rune in PzaFont.StripColours(text).EnumerateRunes())
            if (!font.Draws(rune.Value) && rune.Value > ' ')
            {
                add(widget, text, $"'{rune}' (U+{rune.Value:X4}) is not in {font.Source}: it shows as a box");
                return;
            }
    }

    /// <summary>With PZA_FIT_DUMP set to a folder: each page as laid out at 1920x1080 (controls, bounds, captions, skins) as JSON, for the
    /// pictures (check/fit_pictures.py draws them with Kenshi's own fonts).</summary>
    static void Dump(IReadOnlyList<UiControl> cs, UiBindings? b, string id, string lang, int W, int H, int tab, string tabName)
    {
        if (Environment.GetEnvironmentVariable("PZA_FIT_DUMP") is not { Length: > 0 } dir || H != 1080) return;
        System.IO.Directory.CreateDirectory(dir);
        var items = cs.Select(c => new
        {
            c.Name, c.Type, c.Skin, c.Caption, c.Enabled, X = c.Bounds.X, Y = c.Bounds.Y, Wd = c.Bounds.Width, Ht = c.Bounds.Height,
            Wrap = c.Properties.GetValueOrDefault("WordWrap") == "true", Align = c.Properties.GetValueOrDefault("TextAlign") ?? "",
            Checked = c.Properties.GetValueOrDefault("Checked") == "true",
        }).ToList();
        var doc = new { Window = id, Lang = lang, Width = W, Height = H, Tab = tab, TabName = tabName, Controls = items };
        string file = System.IO.Path.Combine(dir, $"{id}_{lang}_tab{tab}.json");
        System.IO.File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(doc));
    }

    static void Fill(IReadOnlyList<UiControl> cs, string tab, Action<string> say)
    {
        var root = cs.First(c => c.Type == "Window");
        var content = cs.Where(c => !string.IsNullOrWhiteSpace(c.Caption) || c.Type is "ListBox" or "ScrollBar" or "ImageBox" or "EditBox")
            .Where(c => c.Type != "Window" && c.Bounds.Y > root.Bounds.Y + 30 && c.Bounds.Height > 0 && !c.Name.Contains("@tab", StringComparison.Ordinal)).ToList();
        if (content.Count == 0) return;
        float bottom = content.Max(c => c.Bounds.Y + c.Bounds.Height) - root.Bounds.Y;
        say($"  fill [en 1920x1080] tab '{tab}': content reaches {100 * bottom / root.Bounds.Height:0}% of the window's height ({root.Bounds.Width:0}x{root.Bounds.Height:0})");
    }

    static string Short(string s)
    {
        s = s.Replace("\n", " / ");
        return s.Length <= 90 ? s : s[..87] + "...";
    }

    /// <summary>Kenshi's own MyGUI resources from the install (data\gui), as the mods' checks load them.</summary>
    public static MyGuiResources LoadResources(string kenshiDir)
    {
        var files = System.IO.Directory.EnumerateFiles(System.IO.Path.Combine(kenshiDir, "data", "gui"), "*", System.IO.SearchOption.AllDirectories)
            .GroupBy(f => System.IO.Path.GetFileName(f), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        return MyGuiResources.Load("kenshi_init.xml", n => files.GetValueOrDefault(System.IO.Path.GetFileName(n)), "kenshi_fonts.xml");
    }

    /// <summary>The game's binding for a screen it does not know (every Button without a TemplateEvent disabled), then the library's ApplyBind.</summary>
    public static UiBindings DefaultBind(MyGuiScreenHost host, string id)
    {
        var b = new UiBindings();
        foreach (var w in Walk(host.Layout(id).Widgets).Where(w => w.Type == "Button" && !w.Properties.ContainsKey("TemplateEvent"))) b.Disabled.Add(w.Name);
        PzaUi.Windows.FirstOrDefault(x => x.Id == id)?.ApplyBind(b);
        return b;
    }

    static IEnumerable<MyGuiWidget> Walk(IEnumerable<MyGuiWidget> ws)
    {
        foreach (var w in ws) { yield return w; foreach (var c in Walk(w.Children)) yield return c; }
    }

    /// <summary>Names the string a shown text came from: an exact match, then a key whose {n} placeholders match anything, then a key whose text
    /// is part of it (help pages joined from several keys).</summary>
    public static Func<string, string?> KeyFinder(IEnumerable<string> keys, Func<string, string> text)
    {
        var list = keys.ToList();
        var ph = new System.Text.RegularExpressions.Regex(@"\{[0-9]\}");
        return shown =>
        {
            foreach (var k in list) if (text(k) == shown) return k;
            foreach (var k in list)
            {
                string t = text(k);
                if (!ph.IsMatch(t) || ph.Replace(t, "").Trim().Length < 4) continue;   // "{1}{2}" would match anything
                string pattern = "^" + ph.Replace(System.Text.RegularExpressions.Regex.Escape(t).Replace("\\{", "{"), ".*") + "$";
                if (System.Text.RegularExpressions.Regex.IsMatch(shown, pattern, System.Text.RegularExpressions.RegexOptions.Singleline)) return k;
            }
            foreach (var k in list) { string t = text(k); if (t.Length > 12 && shown.Contains(t, StringComparison.Ordinal)) return k + " (part)"; }
            foreach (var k in list) { string t = text(k); if (shown.Length > 12 && t.Contains(shown, StringComparison.Ordinal)) return k + " (a line of it)"; }
            return null;
        };
    }

    /// <summary>The planted failure: a text far too long for a one-line label must be reported, with its widget and language (the check's own test).</summary>
    public static string PlantedText => "THIS PLANTED LABEL IS FAR TOO LONG FOR ITS WIDGET AND MUST BE REPORTED BY THE FIT CHECK, NAMED, IN EVERY LANGUAGE";

    static string F(float v) => v.ToString("0", CultureInfo.InvariantCulture);
}
