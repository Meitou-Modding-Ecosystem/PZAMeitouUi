# PZA's Meitou UI

Author: PZA. Licence: GPLv3 or later. The shared window library for PZA's Meitou mods (MeitouClient 0.20.1): real Kenshi windows on Meitou's own
window engine (`MyGuiScreenHost`) and the PZA tiny HUD button, with everything the PZA UI rules ask for. Proven in game by PZA's UI Probe.
Version 0.3.0, early: expect changes.

**A source library.** Each mod compiles these files into its own assembly, so each mod has its own copy, its own state and its own patches;
two mods can never clash:

```xml
<Compile Include="..\..\PZAMeitouUi\src\*.cs" Link="PzaMeitouUi\%(Filename)%(Extension)" />
```

## Use

```csharp
public void Initialize(ModContext context)
{
    PzaUi.Log = s => context.Log(s);
    var group = context.AddSettings(settings);

    var window = new PzaWindow("pza.mymod", () => PzaLayout.Doc(PzaLayout.Window("Root", "MY MOD", 0.3, 0.2, 0.36, 0.5,
        PzaLayout.Tabs("Tabs", 0.03, 0.02, 0.94, 0.84,
            ("TabMain", [PzaLayout.Text("lbIntro", 0.03, 0.03, 0.94, 0.12, wrap: true),
                         PzaLayout.Slider("Speed", 0.03, 0.2, 0.94, 0.07, range: 11, pos: 8)]),
            PzaLayout.HelpPage("TabHelp", "lbHelp")),
        PzaLayout.Button("CloseButton", 0.76, 0.87, 0.21, 0.07, isDefault: true))))
    {
        Bind = b =>
        {
            PzaWindow.Enable(b, "CloseButton");                  // the game disables buttons it does not know
            PzaWindow.TabText(b, "Tabs", 0, strings.T("tab.main"));
            PzaWindow.Tooltip(b, "Speed", strings.T("speed.tip"));
            b.Text["lbIntro"] = strings.T("intro");
        },
        Event = e => { if (e.Action == "CloseButton") window.Close(); else if (e.Action == "scroll") speed = (int)e.Value; },
        LoadPlace = () => new Vector2((float)settings.WindowX, (float)settings.WindowY),
        SavePlace = p => { PzaSettings.Set(group, settings, "WindowX", (double)p.X); PzaSettings.Set(group, settings, "WindowY", (double)p.Y); },
    };
    PzaUi.Add(window);
    PzaUi.Add(new PzaTinyButton("mymod") { Caption = () => "MY MOD", Click = window.Toggle, OpenFull = window.Open });
    PzaUi.Install(context);
    // a hotkey: window.Toggle(); hotkeys stand down while PzaUi.Typing
}
```

## What is in it

| File | What |
|---|---|
| `PzaUi.cs` | `Install` (resolves the game's members by name; patches `PlayerInterface.Steer`, `ScreenRouter.BindFaithfulScreen`, and when tiny buttons are used the four pointer calls; registers the HUD), `Add`, `Frame`, `Typing`, `EachFrame`, `Guard` (a throw is logged and never reaches the game; 50 faults switch that window off for the session) |
| `PzaWindow.cs` | one MyGUI window: open / close / toggle, `Minimizable` (0.2.1: the "-" or a double click on the title closes it and calls `Minimized`; the tiny button takes over) (queued, applied on the next frame), `Bind`, `Event`, the place as a fraction of the screen (`LoadPlace` / `SavePlace`, saved after a drag and at close), `OpenChanged`; helpers `Enable`, `Tick`, `Tooltip`, `TabText`, `Props`, `Lines`; `PzaSettings.Set` |
| `PzaLayout.cs` | MyGUI layout XML with Kenshi's skins: `Window`, `WindowMin` (with Kenshi's minimize button), `Text`, `Button`, `Tick`, `Slider`, `Edit`, `List`, `Tabs`, `HelpPage`, `HelpList` and `TextList` (0.3.0, scrolling), `Portrait`, `Doc`; tab pages start below the tab strip (0.3.0: Meitou lays a page over the whole TabControl) |
| `PzaFont.cs` | (0.3.0) text measured from the font file itself, the way Meitou draws a MyGUI font resource in the player's language (Kenshi's locale fonts: Exo2, ume-pgs5, TTZhongYuanJ); equal to FreeType to the pixel in en / ru / zh / ja (PZA's UI Probe check); wraps at spaces and between CJK characters |
| `PzaFit.cs` | (0.3.0) text that fits: `PzaWindow.Paragraphs` (long text into a scrolling list, wrapped at its real width), `PzaFit.RowFits` (for a mod's own wrapping), punctuation a language's font lacks swapped for a plain one, and after every bind a one-line text still too wide wraps (room for two lines) or is cut with "..." with the whole text in its tooltip (logged once) |
| `PzaStrings.cs` | the four-language string table (same file format as the RE_Kenshi plugins', placeholders checked, never a format string) |
| `PzaPortrait.cs` | a character's own face in a window (`PzaLayout.Portrait` + `PzaPortrait.Face(b, name, characterId)`), see below |
| `PzaTinyButton.cs` | the tiny HUD button (drag, click on release, wheel opacity 0.35-1 that does not zoom the camera, optional "+") and `PzaHud` |

**Only in a game (0.2.3).** On the title screen (Meitou's main menu) and before a game is loaded, PZA windows and tiny buttons are not shown;
a window that was open (or remembered open) comes back once a game runs, without `OpenChanged` firing. `PzaUi.InGame` says which
(`PlayerInterface.TitleOpen` and `World.Player.Exists`; true if the game lacks them).

What the engine gives every window for free (Confirmed in game 2026-11-16): title-bar drag, raise, Escape, F7 with the HUD, keyboard focus and
typing (hotkeys stand down), tabs, lists (select, double-click accept), sliders, tick boxes, native Kenshi tooltips, no pause unless modal.

Rules learned the hard way (in PZA's UI Probe): tooltips and tick states go through the binding's `Properties` (a layout's
`UserString`s do not reach the control); tab captions through `Text["tabs@tabN"]` (binding `TabCaptions` turns the control into one-page
mode); Meitou fires a HUD button's action on the press (so the tiny button takes its click from the release); the pointer and wheel over the
interface go to `PlayerInterface.MouseDown / MouseMove / MouseUp / UiScroll`, never to the control tick's input.

## Character faces (0.2.0)

```csharp
PzaLayout.Portrait("Face0", 0.03, 0.03, 0.2, 0.3)          // in the layout: an ImageBox, keep it square on screen
PzaPortrait.Face(b, "Face0", characterId);                 // in Bind, every frame the face should show
```

The face is the **live character**: its own head, look and gear, recruits included, drawn by Meitou's own portrait renderer the way the squad
bar draws it (not a CHARACTER record's default look). The library adds the character to the frame's portrait requests (a prefix on
`GameHost.GatherPortraitKeys`, which both renderers call) in a free cell of Meitou's portrait atlas, and the ImageBox shows that cell.
Limits: a character not loaded in the world shows nothing; at most 12 faces at once per mod; the picture appears a frame or two after the
first bind. If Meitou lacks a member, `Install` logs "no character faces" and the windows work without them. `PzaUi.SelectedCharacters()`
gives the player's selection.

## Text fits its window (0.3.0)

The user, 2026-11-17: the text of every PZA Meitou window spilled out. Meitou draws text in the player's language's font, and the layouts were
made by eye in English. Now:

- **Long text scrolls.** `PzaLayout.HelpList("TabHelp", "lstHelp")` + `window.Paragraphs(b, "lstHelp", text)`: one help page however long (no
  "More" pages), wrapped at the list's real width in the real font of the player's language.
- **Tab pages start below the strip.** Every `TabItem` is placed under Kenshi_TabControl's strip (35 px at 1080 lines, scaled).
- **The check.** `check\PzaFitCheck.cs`, compiled into every mod's offline check tool (`<Compile Include="..\..\..\..\PZAMeitouUi\check\*.cs" />`):
  the window in en / ru / zh / ja at 1280x720, 1920x1080 and 2560x1440, every tab clicked, every text (labels, buttons, tab captions, the title,
  every line of every list) measured against its widget with the fit pass OFF, plus a glyph the font lacks and a page control under the tab
  strip. Each problem names the language, screen, tab, widget, text and string key. PZA's UI Probe's check plants a too-long label and expects it
  named in en and ja, and checks PzaFont against FreeType. With `PZA_FIT_DUMP=<folder>` it writes each page's layout for
  `checkit_pictures.py` (pictures in each language's Kenshi font).
- What could make it lie: a player's InterfaceScale above 1 or a bigger `font size2` setting (text larger than measured).

## Checked

By PZA's UI Probe 0.2.0 (`..\PZAMeitouUiProbe`, built on this library): its `mod\build.ps1 -Check` runs every part on a real `MyGuiScreenHost`
with Kenshi's own resources and on Meitou's real `UiHost` (no game started).
