using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bridge.Win32;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;

namespace Bridge.DesktopWorker;

/// <summary>
/// The isolated desktop worker (T14). One process, one MTA thread
/// for UIA: a hung COM call can only hang this process, never the
/// tray. It speaks JSON lines over a current-user-only named pipe.
/// The tray kills and restarts it on timeout or crash (T18).
/// </summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        var pipeName = args.Length > 0 ? args[0] : "BridgeDesktopWorker";
        using var server = new WorkerPipeServer(pipeName);
        await server.RunAsync(CancellationToken.None);
    }
}

/// <summary>Bounded UIA snapshot: foreground window tree only.</summary>
public static class UiaSnapshot
{
    public const int MaxDepth = 4;
    public const int MaxNodes = 200;

    public static JsonElement Capture()
    {
        // FlaUI UIA3 must run on an MTA thread; the caller guarantees it.
        using var automation = new UIA3Automation();
        var desktop = automation.GetDesktop();
        var foreground = desktop.FindFirstChild(
            cf => cf.ByControlType(FlaUI.Core.Definitions.ControlType.Window));
        // Prefer the actual foreground window when we can find it.
        var (hwnd, pid, title) = Win32Desktop.ForegroundWindow();
        var root = foreground;
        if (hwnd != IntPtr.Zero)
        {
            try
            {
                var byHwnd = automation.FromHandle(hwnd);
                if (byHwnd is not null)
                    root = byHwnd;
            }
            catch
            {
                // Fall back to the first window.
            }
        }
        var count = 0;
        var tree = Walk(root, 0, ref count);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("captured_at", DateTimeOffset.UtcNow.ToString("O"));
            writer.WriteNumber("process_id", pid);
            writer.WriteString("foreground_title", title);
            writer.WritePropertyName("tree");
            tree.WriteTo(writer);
            writer.WriteEndObject();
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static JsonElement Walk(AutomationElement? element, int depth, ref int count)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteNode(writer, element, depth, ref count);
        }
        return JsonDocument.Parse(stream.ToArray()).RootElement.Clone();
    }

    private static void WriteNode(Utf8JsonWriter writer, AutomationElement? element, int depth, ref int count)
    {
        writer.WriteStartObject();
        if (element is null || depth > MaxDepth || count >= MaxNodes)
        {
            writer.WriteEndObject();
            return;
        }
        count++;
        try
        {
            writer.WriteString("control_type", element.ControlType.ToString());
            writer.WriteString("name", Truncate(element.Name, 200));
            writer.WriteString("automation_id", Truncate(element.AutomationId, 200));
            var rect = element.BoundingRectangle;
            writer.WritePropertyName("bounds");
            writer.WriteStartObject();
            writer.WriteNumber("left", (int)rect.Left);
            writer.WriteNumber("top", (int)rect.Top);
            writer.WriteNumber("width", (int)rect.Width);
            writer.WriteNumber("height", (int)rect.Height);
            writer.WriteEndObject();
            writer.WriteBoolean("is_enabled", element.IsEnabled);
            writer.WriteBoolean("is_offscreen", element.IsOffscreen);
        }
        catch
        {
            // A dead element mid-walk must not kill the snapshot.
        }
        writer.WritePropertyName("children");
        writer.WriteStartArray();
        if (element is not null && depth < MaxDepth && count < MaxNodes)
        {
            try
            {
                foreach (var child in element.FindAllChildren())
                    WriteNode(writer, child, depth + 1, ref count);
            }
            catch
            {
                // See above.
            }
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static string Truncate(string? value, int max) =>
        value is null ? string.Empty :
        value.Length <= max ? value : value[..max];
}
