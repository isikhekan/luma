using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using Luma;

static class RtssChecks
{
    public static void Run(Action<bool, string> check)
    {
        string name = "Luma.Test.RTSS." + Guid.NewGuid().ToString("N");
        using var mapping = MemoryMappedFile.CreateNew(name, 1_300_000);
        using var view = mapping.CreateViewAccessor();
        const int appOffset = 128, osdOffset = 2048, osdSize = 299520;
        void Seed()
        {
            view.Write(0, 0x52545353u); view.Write(4, 0x00020014u);
            view.Write(8, 512u); view.Write(12, (uint)appOffset); view.Write(16, 2u);
            view.Write(20, (uint)osdSize); view.Write(24, (uint)osdOffset); view.Write(28, 4u);
            view.Write(32, 0u); view.Write(36, 0);
        }
        Seed();
        view.Write(appOffset, 42u); view.Write(appOffset + 276, 100u); view.Write(appOffset + 280, 5000u);
        var reserved = Enumerable.Repeat((byte)0x55, osdSize).ToArray();
        view.WriteArray(osdOffset, reserved, 0, reserved.Length);
        view.WriteArray(osdOffset + osdSize, reserved, 0, reserved.Length);
        view.WriteArray(osdOffset + osdSize + 256, Encoding.ASCII.GetBytes("Other.OSD\0"), 0, 10);
        using var client = new RtssClient(name, "Luma.OSD.Test");
        check(client.Available, "RTSS compatible header accepted");
        check(client.TryGetTarget(42, out var target) && target.FramesPerSecond == 200, "RTSS rendering process and frame timing read");
        check(!client.TryGetTarget(0, out _) && !client.TryGetTarget(777, out _), "Unhooked processes rejected");
        string image = Path.Combine(Path.GetTempPath(), "luma-notification.png");
        string markup = OverlayMotion.Markup(image, 350, 110, 1920, 1080, 1);
        check(markup.Contains("<P=1546,938>") && markup.Contains("<C=FFFFFFFF>"), "Right-side position and full alpha");
        check(OverlayMotion.Markup(image, 350, 110, 1920, 1080, 0).Contains("<P=1920,938>"), "Entry starts outside screen");
        check(OverlayMotion.Markup(image, 350, 110, 1920, 1080, 1, 2).Contains("<P=773,469>") && OverlayMotion.Markup(image, 350, 110, 1920, 1080, 1, 2).Contains("<I=175,55,0,0,350,110>"), "Target OSD zoom compensated without changing settings");
        check(OverlayMotion.Ease(0) == 0 && OverlayMotion.Ease(1) == 1 && OverlayMotion.Ease(.5) > .5, "Bounded slide easing");
        bool rejected = false;
        try { OverlayMotion.Markup(image + "\"><C=FF0000>", 350, 110, 1920, 1080, 1); } catch (ArgumentException) { rejected = true; }
        check(rejected, "Image path cannot inject OSD markup");
        check(client.TryWrite(markup), "Own OSD slot published");
        var preserved = new byte[osdSize]; view.ReadArray(osdOffset, preserved, 0, preserved.Length);
        check(preserved.SequenceEqual(reserved), "Primary MSI/RTSS OSD slot untouched");
        var thirdParty = new byte[osdSize]; view.ReadArray(osdOffset + osdSize, thirdParty, 0, thirdParty.Length);
        check(thirdParty.Take(256).All(b => b == 0x55), "Other application's OSD text untouched");
        long ours = osdOffset + 2L * osdSize;
        var payload = new byte[4096]; view.ReadArray(ours + 512, payload, 0, payload.Length);
        check(Encoding.ASCII.GetString(payload, 0, Array.IndexOf(payload, (byte)0)) == markup, "OSD extended text roundtrip");
        check(view.ReadUInt32(32) == 1 && view.ReadInt32(36) == 0, "Atomic update counter and lock released");
        view.Write(36, 1);
        check(!client.TryWrite("busy"), "Busy renderer is never blocked or overwritten");
        check(view.ReadInt32(36) == 1 && view.ReadUInt32(32) == 1, "Busy renderer state preserved");
        view.Write(36, 2);
        check(client.TryWrite("updated") && view.ReadInt32(36) == 2, "Unrelated lock bits preserved");
        view.Write(36, 0);
        check(!client.TryWrite(new string('x', 4096)) && !client.TryWrite("x\0y"), "OSD buffers cannot overflow or embed NUL");
        check(client.Clear() && view.ReadByte(ours + 256) == 0 && view.ReadByte(ours + 512) == 0, "Notification and slot ownership cleared");
        byte[] deadOwner = Encoding.ASCII.GetBytes("Luma.OSD.2147483647\0");
        view.WriteArray(ours + 256, deadOwner, 0, deadOwner.Length);
        check(client.TryWrite(markup), "Abandoned slot from a terminated Luma process reclaimed");
        client.Clear();
        var timer = Stopwatch.StartNew();
        for (int i = 0; i < 1000; i++) check(client.TryWrite(markup), "Bounded OSD publish stress");
        timer.Stop();
        Console.WriteLine($"RTSS publisher benchmark: {timer.Elapsed.TotalMilliseconds / 1000:0.000} ms/update (synthetic shared memory; not game FPS).");
        client.Clear();
        foreach (var mutation in new[] { (0L, 0xDEADu), (4L, 0x0002000du), (4L, 0x00030000u), (12L, uint.MaxValue), (16L, uint.MaxValue), (20L, 100u), (24L, uint.MaxValue), (28L, 1u) })
        {
            Seed(); view.Write(mutation.Item1, mutation.Item2);
            using var invalid = new RtssClient(name, "Luma.OSD.Invalid");
            check(!invalid.TryWrite("test"), "Malformed RTSS mapping safely rejected");
            check(view.ReadInt32(36) == 0 && view.ReadUInt32(32) == 0, "Malformed mapping never mutated");
        }
        using var absent = new RtssClient("Luma.NoServer." + Guid.NewGuid());
        check(!absent.Available && !absent.TryWrite("test"), "Missing server handled without creating one");
    }
    public static void Probe(bool write)
    {
        using var client = new RtssClient(owner: "Luma.OSD.Probe." + Environment.ProcessId);
        bool available = client.Available; double? milliseconds = null; bool? cleared = null;
        if (available && write)
        {
            var timer = Stopwatch.StartNew(); bool published = client.TryWrite("Luma RTSS connection check"); timer.Stop();
            if (!published) throw new Exception("RTSS live slot write failed.");
            milliseconds = timer.Elapsed.TotalMilliseconds;
            for (int i = 0; i < 20; i++) { if (client.Clear()) { cleared = true; break; } Thread.Sleep(10); }
            if (cleared != true) throw new Exception("RTSS live slot cleanup failed.");
        }
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Installed = RtssClient.FindInstalledServer() != null, Available = available, PublishedMilliseconds = milliseconds, Cleared = cleared }));
    }
}
