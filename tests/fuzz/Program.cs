// PZA's Meitou UI: the library's own fuzz harness. Author: PZA. Licence: GPLv3 or later.
// The shared targets (PzaFuzzUi) on a sample settings class with every kind a mod uses (switch, double and int sliders, a string list, an enum)
// and a sample string table with placeholders and line breaks. Each mod's harness runs the same targets on its own settings and strings.
using Meitou.Modding;
using PZA.MeitouUi.Fuzz;

public enum SampleMode { Off, Low, High }

public sealed class SampleSettings
{
    [Setting("On")] public bool On { get; set; } = true;
    [Setting("Speed", Min = 0.10, Max = 0.60, Step = 0.05)] public double Speed { get; set; } = 0.5;
    [Setting("Place", Min = -1, Max = 1, Step = 0.001)] public double Place { get; set; }
    [Setting("Count", Min = 0, Max = 12, Step = 1)] public int Count { get; set; } = 3;
    [Setting("Language", Options = ["auto", "en", "ru", "zh", "ja"])] public string Language { get; set; } = "auto";
    [Setting("Mode")] public SampleMode Mode { get; set; } = SampleMode.Low;
}

static class Program
{
    static readonly (string, string)[] Table =
    [
        ("title", "PZA's Sample"), ("speed", "Speed {1}x"), ("two", "{1} of {2}"), ("help", "Line one\nLine two"), ("hash", "50% \\ # = ok"),
    ];

    static int Main(string[] a) => PzaFuzz.Run("PZA's Meitou UI", a,
        [.. PzaFuzzUi.Settings("pza.sample", () => new SampleSettings()), PzaFuzzUi.Strings(Table), .. PzaFuzzUi.Library()]);
}
