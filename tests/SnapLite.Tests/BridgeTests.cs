using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SnapLite.Core.Bridge;
using SnapLite.Core.Splitting;

namespace SnapLite.Tests;

public class BridgeTests
{
    [Fact]
    public void Native_messages_round_trip_with_little_endian_length()
    {
        var ms = new MemoryStream();
        NativeMessaging.Write(ms, Encoding.UTF8.GetBytes("""{"type":"hello"}"""));
        NativeMessaging.Write(ms, Encoding.UTF8.GetBytes("""{"type":"chunk","data":"åäö"}"""));
        byte[] raw = ms.ToArray();
        Assert.Equal([16, 0, 0, 0], raw[..4]);

        ms.Position = 0;
        Assert.Equal("""{"type":"hello"}""", Encoding.UTF8.GetString(NativeMessaging.Read(ms)!));
        Assert.Equal("""{"type":"chunk","data":"åäö"}""", Encoding.UTF8.GetString(NativeMessaging.Read(ms)!));
        Assert.Null(NativeMessaging.Read(ms)); // browser closed the port
    }

    [Fact]
    public void Cut_off_or_absurd_messages_are_rejected()
    {
        Assert.Throws<EndOfStreamException>(() => NativeMessaging.Read(new MemoryStream([10, 0, 0, 0, 1, 2])));
        Assert.Throws<EndOfStreamException>(() => NativeMessaging.Read(new MemoryStream([10, 0])));
        Assert.Throws<InvalidDataException>(() => NativeMessaging.Read(new MemoryStream([0xFF, 0xFF, 0xFF, 0x7F])));
        Assert.Throws<ArgumentException>(() => NativeMessaging.Write(new MemoryStream(), new byte[NativeMessaging.MaxOutgoing + 1]));
    }

    [Fact]
    public void Extension_id_from_the_manifest_key_is_the_one_the_app_trusts()
    {
        string root = RepoRoot();
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "extension", "manifest.json")));
        string id = ExtensionId.FromManifestKey(manifest.RootElement.GetProperty("key").GetString()!);

        string setup = File.ReadAllText(Path.Combine(root, "src", "SnapLite", "Bridge", "BrowserSetup.cs"));
        string trusted = Regex.Match(setup, "ExtensionId = \"([a-p]{32})\"").Groups[1].Value;
        Assert.Equal(trusted, id);
        Assert.Matches("^[a-p]{32}$", id);
    }

    [Fact]
    public void Layout_from_the_browser_is_scaled_to_image_pixels()
    {
        using var doc = JsonDocument.Parse("""
            { "height": 2000, "width": 1280,
              "boundaries": [[0, 0], [801.4, 2]],
              "headings": [[801.4, 2, "Step 2: Do it"]],
              "zones": [[100, 120.6]] }
            """);
        var layout = PageLayoutJson.Parse(doc.RootElement, 1.25);
        Assert.Equal(2500, layout.Height);
        Assert.Equal(new Boundary(1002, BoundaryKind.Heading2), layout.Boundaries[1]);
        Assert.Equal(new Heading(1002, 2, "Step 2: Do it"), layout.Headings[0]);
        Assert.Equal(new Span(125, 151), layout.ContentZones[0]);
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SnapLite.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("SnapLite.sln not found above the test folder.");
    }
}
