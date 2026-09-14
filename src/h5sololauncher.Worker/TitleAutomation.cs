using System.Buffers.Binary;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using H5SoloLauncher.Core;
using H5SoloLauncher.Core.Forge;

namespace H5SoloLauncher.Worker;

public sealed record TitleEntryResult(string State, int Phase, string Message, string? Code = null, string? Details = null);

/// <summary>Uses the title's original CheckIsOnline action, after native readiness checks.</summary>
public static class TitleAutomation
{
    public static TitleEntryResult Run(ForgeLaunchRequest request, IProgress<IndexProgress> progress, CancellationToken cancellation)
    {
        var phase = 0; var started = false;
        try
        {
            var target = PackageTarget.Forge(request.ForgeRoot, request.PackageFullName);
            var process = target.Find() ?? throw new CacheException("FORGE_NOT_RUNNING", "Forge closed before campaign startup.");
            using var session = PackageProcessSession.Attach(target, "h5sololauncher.TitleAutomation.dll", "H5Title", cancellation);
            byte[] Call(int operation, byte[]? xml = null)
            {
                if (target.Find() != process) throw new CacheException("FORGE_PROCESS_CHANGED", "Forge restarted during automatic title entry.");
                var buffer = new byte[552 + 1024 * 1024]; W(buffer, 0, 0x35525448); W(buffer, 4, 1); W(buffer, 8, (uint)operation);
                BinaryPrimitives.WriteInt64LittleEndian(buffer.AsSpan(16), process.Created);
                if (xml is not null) { W(buffer, 32, (uint)xml.Length); xml.CopyTo(buffer, 552); }
                var result = session.Call(buffer, cancellation); var status = U(result, 12); phase = (int)U(result, 24);
                if (status != 0)
                {
                    var message = Encoding.UTF8.GetString(result, 40, Array.IndexOf(result, (byte)0, 40, 512) - 40);
                    throw new CacheException("FORGE_TITLE_ENTRY", message);
                }
                return result;
            }
            var until = DateTime.UtcNow.AddSeconds(120); byte[] observed;
            do
            {
                cancellation.ThrowIfCancellationRequested(); observed = Call(0);
                if (phase == 3) return new("MainMenu",phase,"Forge's main menu is ready.");
                if (U(observed, 32) > 0) break;
                if (DateTime.UtcNow > until) throw new CacheException("FORGE_TITLE_WAIT", "Forge did not reach its title screen. Keep its window open and check the startup details.");
                progress.Report(new("Waiting for Forge's title screen", 0, 0)); if (cancellation.WaitHandle.WaitOne(250)) cancellation.ThrowIfCancellationRequested();
            } while (true);
            var length = checked((int)U(observed, 32)); if (length <= 1 || length > 1024 * 1024) throw new CacheException("FORGE_TITLE_SOURCE", "The native title source exceeds its supported size.");
            var original = observed.AsSpan(552, length).ToArray(); var prepared = Prepare(original);
            progress.Report(new("Continuing through Forge's title screen", 0, 0)); started = true; Call(1, prepared);
            while (true)
            {
                if (cancellation.WaitHandle.WaitOne(200)) cancellation.ThrowIfCancellationRequested(); Call(2);
                if (phase == 3) return new("MainMenu", phase, "Forge entered its main menu automatically.");
                if (phase == 4) return new("SignInRequired", phase, "Forge is waiting at sign-in or its title screen. Complete any sign-in prompt in Forge; no timed Solo action is required.");
                progress.Report(new("Waiting for Forge to finish sign-in", 0, 0));
            }
        }
        catch (OperationCanceledException) { return new("Paused", phase, started ? "Automatic title entry was already queued. Its buffers were kept; check Forge before continuing." : "Title entry paused before starting."); }
        catch (Exception e) { return new("Failed", phase, e.Message, e is CacheException known ? known.Code : "FORGE_TITLE_ENTRY_FAILED", e.ToString()); }
    }
    internal static byte[] Prepare(byte[] original)
    {
        var text = new UTF8Encoding(false, true).GetString(original).TrimEnd('\0').TrimStart('\uFEFF');
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace); XNamespace halo = "clr-namespace:HaloWPFPipeline.HaloWPFBlend.Controls;assembly=HaloWPFPipeline.HaloWPFBlend.Controls";
        var loaded = document.Descendants().Where(x => x.Name.LocalName == "EventTrigger" && (string?)x.Attribute("RoutedEvent") == "FrameworkElement.Loaded").ToArray();
        if (loaded.Length != 1 || !document.Descendants(halo + "DoLuaAction").Any(x => (string?)x.Attribute("LuaLine") == "CheckIsOnline()") ||
            !document.Descendants(halo + "ScriptTagResource").Any(x => (string?)x.Attribute("Uri") == "Gx000277a7"))
            throw new CacheException("FORGE_TITLE_SOURCE", "Forge's title no longer has the expected native Continue action.");
        loaded[0].Add(new XElement(halo + "DoLuaAction", new XAttribute("LuaLine", "CheckIsOnline()")));
        return new UTF8Encoding(false, true).GetBytes(document.ToString(SaveOptions.DisableFormatting) + '\0');
    }
    private static uint U(byte[] bytes, int at) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at));
    private static void W(byte[] bytes, int at, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), value);
}
