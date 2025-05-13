// Program.cs  –  F3 hot-key helper for Obsidian Canvas
// Grabs the two newest clipboard-history items (question + answer)
// and injects them into the selected *.canvas* file.
//
// Build:  dotnet build -c Release
// Run  :  run EXE *as Administrator*

using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using WindowsInput;
using WindowsInput.Native;
using Windows.ApplicationModel.DataTransfer;

// two distinct Clipboards → give them clear aliases
using FormsClipboard = System.Windows.Forms.Clipboard;
using HistoryClipboard = Windows.ApplicationModel.DataTransfer.Clipboard;

internal class Program
{
    // ── Win32 hot-key plumbing ────────────────────────────────────────
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ID_F3 = 1;      // only F3

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    // ── helpers / constants ───────────────────────────────────────────
    private static readonly InputSimulator sim = new();
    private const int BOX_W = 800, BOX_H = 800;
    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    private static string ClipboardTextAfter(int ms)
    {
        Thread.Sleep(ms);
        return FormsClipboard.GetText();
    }

    private static bool TryGetQA(out string question, out string answer)
    {
        question = answer = "";

        if (!HistoryClipboard.IsHistoryEnabled())
        {
            Console.WriteLine("⚠️  Clipboard history is off (Settings ▸ System ▸ Clipboard).");
            return false;
        }

        var items = HistoryClipboard.GetHistoryItemsAsync().AsTask().Result.Items;
        if (items.Count < 2)
        {
            Console.WriteLine("⚠️  Need at least two items in clipboard history.");
            return false;
        }

        question = (items[1].Content.GetTextAsync().AsTask().Result ?? "").Trim();
        answer = (items[0].Content.GetTextAsync().AsTask().Result ?? "").Trim();

        if (question.Length == 0 || answer.Length == 0)
        {
            Console.WriteLine("⚠️  Last two history items are not plain text.");
            return false;
        }
        return true;
    }

    // ── F3 action ─────────────────────────────────────────────────────
    private static void DoF3()
    {
        if (!TryGetQA(out var question, out var answer))
            return;

        // Alt+P  → Obsidian copies current canvas path
        sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.MENU, VirtualKeyCode.VK_P);
        var path = ClipboardTextAfter(500).Trim(' ', '"');

        if (!path.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { Console.WriteLine("❌  Alt+P didn’t yield a *.canvas path."); return; }

        JObject doc;
        try { doc = JObject.Parse(File.ReadAllText(path)); }
        catch { doc = new JObject(); }        // brand-new file

        var nodes = (JArray?)(doc["nodes"] ??= new JArray());
        var edges = (JArray?)(doc["edges"] ??= new JArray());

        // locate first blank node + edge
        JObject? nTarget = null, eTarget = null;
        var blank = nodes.OfType<JObject>()
                         .Where(n => string.IsNullOrWhiteSpace((string?)n["text"]))
                         .ToDictionary(n => (string)n["id"]!);

        foreach (JObject e in edges)
        {
            var to = (string?)e["toNode"];
            if (to != null && blank.TryGetValue(to, out nTarget) &&
                string.IsNullOrWhiteSpace((string?)e["label"]))
            { eTarget = e; break; }
        }

        if (nTarget != null && eTarget != null)
        {
            nTarget["text"] = answer;
            nTarget["width"] = BOX_W;
            nTarget["height"] = BOX_H;
            eTarget["label"] = question;
            Console.WriteLine("✅  Filled existing blank node.");
        }
        else
        {
            // create new node + edge  (logic cloned from Python version)
            var anchorId = nodes.Count > 0
                         ? (string)((JObject)nodes[^1])["id"]!
                         : NewId();

            if (nodes.Count == 0)             // stub root node
                nodes.Add(new JObject
                {
                    ["id"] = anchorId,
                    ["x"] = 0,
                    ["y"] = 0,
                    ["width"] = 1,
                    ["height"] = 1,
                    ["type"] = "text",
                    ["text"] = ""
                });

            var yBase = nodes.OfType<JObject>().Select(n => (int?)n["y"] ?? 0).Max();
            var targetId = NewId();

            nodes.Add(new JObject
            {
                ["id"] = targetId,
                ["x"] = 0,
                ["y"] = yBase + 150,
                ["width"] = BOX_W,
                ["height"] = BOX_H,
                ["type"] = "text",
                ["text"] = answer
            });

            edges.Add(new JObject
            {
                ["id"] = NewId(),
                ["fromNode"] = anchorId,
                ["fromSide"] = "bottom",
                ["toNode"] = targetId,
                ["toSide"] = "top",
                ["label"] = question
            });
            Console.WriteLine("🆕  Added new 800×800 node + edge.");
        }

        // atomic save
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, doc.ToString());
        File.Move(tmp, path, overwrite: true);
        Console.WriteLine("💾  Canvas saved.");
    }

    // ── entry point ────────────────────────────────────────────────────
    [STAThread]                   // ← attribute on the *method*
    private static void Main()
    {
        if (!RegisterHotKey(IntPtr.Zero, ID_F3, MOD_NOREPEAT, 0x72))
        {
            Console.WriteLine("‼️  RegisterHotKey failed — run as Administrator.");
            return;
        }

        Console.WriteLine("Hot-key ready →  F3  (Ctrl+C quits)");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) != 0)
            if (msg.message == WM_HOTKEY && msg.wParam == (nuint)ID_F3)
                DoF3();

        UnregisterHotKey(IntPtr.Zero, ID_F3);
    }
}
