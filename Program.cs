// Program.cs  –  F1 / F2 / F3 canvas helper, 800×800 nodes
// Build :  dotnet build -c Release
// Run   :  right-click EXE → “Run as administrator”

using System.Runtime.InteropServices;
using System.Windows.Forms;
using WindowsInput;
using WindowsInput.Native;
using Newtonsoft.Json.Linq;

internal class Program
{
    // ── hot-key plumbing ────────────────────────────────────────────────
    private const uint WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int ID_F1 = 1, ID_F2 = 2, ID_F3 = 3;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsMod, uint vk);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    { public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public POINT pt; }
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    // ── state / helpers ────────────────────────────────────────────────
    private static readonly InputSimulator sim = new();
    private static string? question, answer;
    private const int BOX_W = 800, BOX_H = 800;
    private static string NewId() => Guid.NewGuid().ToString("N")[..16];

    private static string ClipboardAfter(int ms)
    {
        Thread.Sleep(ms);
        try { return Clipboard.GetText(); }
        catch { return ""; }
    }

    // ── F-key actions ──────────────────────────────────────────────────
    private static void DoF1()
    {
        sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.CONTROL, VirtualKeyCode.VK_C);
        question = ClipboardAfter(500);
        Console.WriteLine($"[F1] question = {question}");
    }

    private static void DoF2()
    {
        Console.WriteLine("[F2] click-capture …");
        sim.Mouse.LeftButtonClick();
        answer = ClipboardAfter(1000);
        Console.WriteLine($"[F2] answer   = {answer}");
    }

    private static void DoF3()
    {
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(answer))
        { Console.WriteLine("⚠️  Need question (F1) and answer (F2) first."); return; }

        // Alt+P → wait 0.5 s → clipboard
        sim.Keyboard.ModifiedKeyStroke(VirtualKeyCode.MENU, VirtualKeyCode.VK_P);
        string path = ClipboardAfter(500).Trim(' ', '"');

        if (!path.EndsWith(".canvas", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        { Console.WriteLine("❌  No *.canvas path."); return; }

        // ── read or create bare canvas ────────────────────────────────
        JObject doc;
        try { doc = JObject.Parse(File.ReadAllText(path)); }
        catch { doc = new JObject(); }

        var nodes = (JArray?)(doc["nodes"] ??= new JArray());
        var edges = (JArray?)(doc["edges"] ??= new JArray());

        // ── find first blank node + edge ──────────────────────────────
        JObject? nTarget = null, eTarget = null;

        var blankNodes = nodes
            .OfType<JObject>()
            .Where(n => string.IsNullOrWhiteSpace((string?)n["text"]))
            .ToDictionary(n => (string)n["id"]!);

        foreach (JObject e in edges)
        {
            var to = (string?)e["toNode"];
            if (to != null &&
                blankNodes.TryGetValue(to, out nTarget) &&
                string.IsNullOrWhiteSpace((string?)e["label"]))
            { eTarget = e; break; }
        }

        if (nTarget != null && eTarget != null)
        {
            // ── fill existing blank ────────────────────────────────
            nTarget["text"] = answer;
            nTarget["width"] = BOX_W;
            nTarget["height"] = BOX_H;
            eTarget["label"] = question;
            Console.WriteLine("✅  Filled and resized existing blank node.");
        }
        else
        {
            // ── create new node + edge (old Python logic) ──────────
            string anchorId;
            if (nodes.Count > 0)
            {
                anchorId = (string)((JObject)nodes[^1])["id"]!;
            }
            else
            {
                anchorId = NewId();
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
            }

            int yBase = nodes
                .OfType<JObject>()
                .Select(n => (int?)n["y"] ?? 0)
                .DefaultIfEmpty(0)
                .Max();

            string targetId = NewId();
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

        // ── save via tmp → atomic-ish write ─────────────────────────
        try
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, doc.ToString());
            File.Move(tmp, path, overwrite: true);
            Console.WriteLine("💾  Canvas saved.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌  Write failed: {ex.Message}");
        }
    }

    // ── Main loop ─────────────────────────────────────────────────────
    [STAThread]
    private static void Main()
    {
        if (!RegisterHotKey(IntPtr.Zero, ID_F1, MOD_NOREPEAT, 0x70) ||   // F1
            !RegisterHotKey(IntPtr.Zero, ID_F2, MOD_NOREPEAT, 0x71) ||   // F2
            !RegisterHotKey(IntPtr.Zero, ID_F3, MOD_NOREPEAT, 0x72))     // F3
        {
            Console.WriteLine("‼️  RegisterHotKey failed — run as Administrator.");
            return;
        }

        Console.WriteLine("Hot-keys ready →  F1  F2  F3  (Ctrl+C quits)");

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) != 0)
        {
            if (msg.message == WM_HOTKEY)
            {
                switch ((int)msg.wParam)
                {
                    case ID_F1: DoF1(); break;
                    case ID_F2: DoF2(); break;
                    case ID_F3: DoF3(); break;
                }
            }
        }

        UnregisterHotKey(IntPtr.Zero, ID_F1);
        UnregisterHotKey(IntPtr.Zero, ID_F2);
        UnregisterHotKey(IntPtr.Zero, ID_F3);
    }
}
