using System.Text.Json;

namespace PaRappaPC;

internal enum GameAction { Triangle, Circle, Cross, Square, L, R, SyncEarlier, SyncLater, Back }

/// <summary>
/// Keyboard and controller bindings loaded from controls.json next to the executable.
/// The file is created with the defaults on first run; edit it and restart the game.
/// </summary>
internal sealed class ControlConfig
{
    public const string FileName = "controls.json";

    // XInput button bits.
    private static readonly Dictionary<string, ushort> PadButtons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DPadUp"] = 0x0001, ["DPadDown"] = 0x0002, ["DPadLeft"] = 0x0004, ["DPadRight"] = 0x0008,
        ["Start"] = 0x0010, ["Back"] = 0x0020, ["LeftStick"] = 0x0040, ["RightStick"] = 0x0080,
        ["LB"] = 0x0100, ["RB"] = 0x0200, ["A"] = 0x1000, ["B"] = 0x2000, ["X"] = 0x4000, ["Y"] = 0x8000,
    };

    private static readonly Dictionary<string, string[]> DefaultKeyboard = new()
    {
        ["Triangle"] = ["W", "Up"], ["Circle"] = ["D", "Right"], ["Cross"] = ["S", "Down"], ["Square"] = ["A", "Left"],
        ["L"] = ["Q"], ["R"] = ["E"],
        ["SyncEarlier"] = ["OemOpenBrackets"], ["SyncLater"] = ["OemCloseBrackets"], ["Back"] = ["Escape"],
    };

    private static readonly Dictionary<string, string[]> DefaultGamepad = new()
    {
        ["Triangle"] = ["Y", "DPadUp"], ["Circle"] = ["B", "DPadRight"], ["Cross"] = ["A", "DPadDown"], ["Square"] = ["X", "DPadLeft"],
        ["L"] = ["LB"], ["R"] = ["RB"], ["Back"] = ["Back"],
    };

    public Dictionary<Keys, GameAction> Keyboard { get; } = [];
    public List<(ushort Mask, GameAction Action)> Gamepad { get; } = [];
    public List<string> Problems { get; } = [];
    public string Path { get; private set; } = "";

    public static string Describe(GameAction action, ControlConfig config) =>
        string.Join("/", config.Keyboard.Where(k => k.Value == action).Select(k => KeyLabel(k.Key)));

    private static string KeyLabel(Keys key) => key switch
    {
        Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→",
        Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.Escape => "Esc",
        _ => key.ToString(),
    };

    public static ControlConfig Load()
    {
        var config = new ControlConfig { Path = System.IO.Path.Combine(AppContext.BaseDirectory, FileName) };
        Dictionary<string, string[]> keyboard = DefaultKeyboard, gamepad = DefaultGamepad;
        try
        {
            if (!File.Exists(config.Path)) WriteDefaults(config.Path);
            var file = JsonSerializer.Deserialize<FileFormat>(File.ReadAllText(config.Path), new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (file?.Keyboard is not null) keyboard = file.Keyboard;
            if (file?.Gamepad is not null) gamepad = file.Gamepad;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            config.Problems.Add($"{FileName}: {ex.Message} (using defaults)");
        }

        foreach (var (actionName, keys) in keyboard)
        {
            if (!Enum.TryParse(actionName, true, out GameAction action)) { config.Problems.Add($"unknown action '{actionName}'"); continue; }
            foreach (var name in keys)
            {
                if (Enum.TryParse(name, true, out Keys key) && key != Keys.None) config.Keyboard[key] = action;
                else config.Problems.Add($"unknown key '{name}'");
            }
        }
        foreach (var (actionName, buttons) in gamepad)
        {
            if (!Enum.TryParse(actionName, true, out GameAction action)) { config.Problems.Add($"unknown action '{actionName}'"); continue; }
            foreach (var name in buttons)
            {
                if (PadButtons.TryGetValue(name, out ushort mask)) config.Gamepad.Add((mask, action));
                else config.Problems.Add($"unknown controller button '{name}'");
            }
        }
        return config;
    }

    private static void WriteDefaults(string path)
    {
        var json = JsonSerializer.Serialize(new FileFormat { Keyboard = DefaultKeyboard, Gamepad = DefaultGamepad }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path,
            "// PaRappa PC controls. Edit and restart the game.\n" +
            "// Actions: Triangle, Circle, Cross, Square, L, R, SyncEarlier, SyncLater, Back\n" +
            "// Keyboard names are .NET Keys names: A-Z, D0-D9, Up, Down, Left, Right, Space, Enter, NumPad0-9,\n" +
            "//   OemOpenBrackets ([), OemCloseBrackets (]), Oemcomma, OemPeriod, OemSemicolon, ShiftKey, ControlKey ...\n" +
            "// Controller (XInput) names: A, B, X, Y, LB, RB, DPadUp, DPadDown, DPadLeft, DPadRight, Start, Back, LeftStick, RightStick\n" +
            json + "\n");
    }

    public static RapButton? ToRapButton(GameAction action) => action switch
    {
        GameAction.Triangle => RapButton.Triangle, GameAction.Circle => RapButton.Circle, GameAction.Cross => RapButton.Cross,
        GameAction.Square => RapButton.Square, GameAction.L => RapButton.L, GameAction.R => RapButton.R, _ => null,
    };

    private sealed class FileFormat
    {
        public Dictionary<string, string[]>? Keyboard { get; set; }
        public Dictionary<string, string[]>? Gamepad { get; set; }
    }
}
