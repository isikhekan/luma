using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Luma;

public readonly record struct RtssTarget(uint ProcessId, double FramesPerSecond, double PixelZoom);
internal readonly record struct RtssLayout(uint Version, long AppOffset, int AppSize, int AppCount, long OsdOffset, int OsdSize, int OsdCount)
{
    public static bool TryRead(MemoryMappedViewAccessor view, out RtssLayout layout)
    {
        layout = default;
        if (view.Capacity < 40 || view.ReadUInt32(0) != 0x52545353) return false;
        uint version = view.ReadUInt32(4);
        // Require the SDK's cooperative writer lock (v2.14+) and extended text slots.
        if (version < 0x0002000e || (version >> 16) != 2) return false;
        uint appSize = view.ReadUInt32(8), appOffset = view.ReadUInt32(12), appCount = view.ReadUInt32(16);
        uint osdSize = view.ReadUInt32(20), osdOffset = view.ReadUInt32(24), osdCount = view.ReadUInt32(28);
        bool Bounds(uint offset, uint size, uint count) => offset >= 40 && offset <= view.Capacity && (ulong)size * count <= (ulong)(view.Capacity - offset);
        if (appSize < 284 || appSize > 1_048_576 || appCount > 4096 || !Bounds(appOffset, appSize, appCount)
            || osdSize < 4608 || osdSize > 1_048_576 || osdCount < 2 || osdCount > 256 || !Bounds(osdOffset, osdSize, osdCount)) return false;
        if (appOffset < (ulong)osdOffset + (ulong)osdSize * osdCount && osdOffset < (ulong)appOffset + (ulong)appSize * appCount) return false;
        layout = new(version, appOffset, (int)appSize, (int)appCount, osdOffset, (int)osdSize, (int)osdCount); return true;
    }
}

/// <summary>Original client for RTSS's documented shared-memory ABI. No hooks or game memory writes.</summary>
public sealed class RtssClient : IDisposable
{
    public const string MappingName = "RTSSSharedMemoryV2";
    private readonly string _mappingName;
    private readonly string _owner;
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private int _slot = -1;
    private bool _ownsSlot;
    public int Writes { get; private set; }
    public RtssClient(string mappingName = MappingName, string? owner = null)
    { _mappingName = mappingName; _owner = owner ?? "Luma.OSD." + Environment.ProcessId; }
    private bool Connect(out RtssLayout layout)
    {
        layout = default;
        try
        {
            if (_view == null)
            {
                _mapping = MemoryMappedFile.OpenExisting(_mappingName, MemoryMappedFileRights.ReadWrite);
                _view = _mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.ReadWrite);
            }
            if (RtssLayout.TryRead(_view, out layout)) return true;
            Disconnect(); return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { Disconnect(); return false; }
    }
    public bool TryGetTarget(uint processId, out RtssTarget target)
    {
        target = default;
        if (processId == 0 || !Connect(out var layout)) return false;
        for (int i = 0; i < layout.AppCount; i++)
        {
            long entry = layout.AppOffset + (long)i * layout.AppSize;
            if (_view!.ReadUInt32(entry) != processId) continue;
            uint frameTime = _view.ReadUInt32(entry + 280), frames = _view.ReadUInt32(entry + 276);
            if (frameTime == 0 || frames == 0) return false;
            double zoom = layout.AppSize >= 328 ? _view.ReadUInt32(entry + 324) : 1;
            target = new(processId, 1_000_000d / frameTime, zoom is >= 1 and <= 16 ? zoom : 1); return true;
        }
        return false;
    }
    public bool Available => Connect(out _);
    public bool TryWrite(string markup)
    {
        // The ANSI protocol receives only our fixed tags and a validated image path.
        byte[] text;
        try { text = ProtocolEncoding.GetBytes(markup); }
        catch (EncoderFallbackException) { return false; }
        if (text.Length >= 4096 || markup.IndexOf('\0') >= 0 || !Connect(out var layout)) return false;
        return WithLock(() =>
        {
            _slot = FindSlot(layout);
            if (_slot < 1) return false;
            long entry = layout.OsdOffset + (long)_slot * layout.OsdSize;
            var owner = Encoding.ASCII.GetBytes(_owner);
            if (owner.Length > 254) return false;
            _view!.WriteArray(entry + 256, owner, 0, owner.Length); _view.Write(entry + 256 + owner.Length, (byte)0);
            _view.Write(entry, (byte)0);
            if (layout.Version >= 0x00020014 && layout.OsdSize >= 299_520) _view.Write(entry + 266_752, (byte)0);
            _view.WriteArray(entry + 512, text, 0, text.Length); _view.Write(entry + 512 + text.Length, (byte)0);
            IncrementFrame(); Writes++; _ownsSlot = true; return true;
        });
    }
    private int FindSlot(RtssLayout layout)
    {
        int free = -1;
        for (int i = 1; i < layout.OsdCount; i++)
        {
            long ownerAt = layout.OsdOffset + (long)i * layout.OsdSize + 256;
            var bytes = new byte[256]; _view!.ReadArray(ownerAt, bytes, 0, bytes.Length);
            int end = Array.IndexOf(bytes, (byte)0); if (end < 0) continue;
            string owner = Encoding.ASCII.GetString(bytes, 0, end);
            if (owner == _owner) return i;
            if (owner.StartsWith("Luma.OSD.", StringComparison.Ordinal) && int.TryParse(owner[9..], out int oldProcess) && oldProcess > 0 && DeadProcess(oldProcess))
            {
                long entry = ownerAt - 256;
                _view.Write(entry, (byte)0); _view.Write(entry + 512, (byte)0); _view.Write(ownerAt, (byte)0);
                if (layout.Version >= 0x00020014 && layout.OsdSize >= 299_520) _view.Write(entry + 266_752, (byte)0);
                IncrementFrame(); owner = "";
            }
            if (owner.Length == 0 && free < 0) free = i;
        }
        return free;
    }
    private static bool DeadProcess(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return process.HasExited; }
        catch (ArgumentException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
    public bool Clear()
    {
        if (!_ownsSlot) return true;
        if (!Connect(out var layout)) { _ownsSlot = false; return true; }
        bool cleared = WithLock(() =>
        {
            for (int i = 1; i < layout.OsdCount; i++)
            {
                long entry = layout.OsdOffset + (long)i * layout.OsdSize;
                var bytes = new byte[256]; _view!.ReadArray(entry + 256, bytes, 0, 256);
                int end = Array.IndexOf(bytes, (byte)0);
                if (end < 0 || Encoding.ASCII.GetString(bytes, 0, end) != _owner) continue;
                _view.Write(entry, (byte)0); _view.Write(entry + 512, (byte)0);
                if (layout.Version >= 0x00020014 && layout.OsdSize >= 299_520) _view.Write(entry + 266_752, (byte)0);
                _view.Write(entry + 256, (byte)0); IncrementFrame();
            }
            _slot = -1; _ownsSlot = false; return true;
        });
        return cleared;
    }
    private unsafe bool WithLock(Func<bool> action)
    {
        if (_view == null) return false;
        byte* address = null; var handle = _view.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref address);
        try
        {
            ref int busy = ref *(int*)(address + _view.PointerOffset + 36);
            int current = Volatile.Read(ref busy);
            if ((current & 1) != 0 || Interlocked.CompareExchange(ref busy, current | 1, current) != current) return false;
            try { return action(); }
            finally { Interlocked.And(ref busy, ~1); }
        }
        finally { handle.ReleasePointer(); }
    }
    private unsafe void IncrementFrame()
    {
        byte* address = null; var handle = _view!.SafeMemoryMappedViewHandle; handle.AcquirePointer(ref address);
        try { Interlocked.Increment(ref *(int*)(address + _view.PointerOffset + 32)); }
        finally { handle.ReleasePointer(); }
    }
    private void Disconnect() { _slot = -1; _view?.Dispose(); _view = null; _mapping?.Dispose(); _mapping = null; }
    public void Dispose() { try { Clear(); } finally { Disconnect(); } }
    private static Encoding ProtocolEncoding
    {
        get { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); return Encoding.GetEncoding((int)GetACP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback); }
    }
    public static string? FindInstalledServer()
    {
        foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            string path = Path.Combine(root, "RivaTuner Statistics Server", "RTSS.exe");
            if (!File.Exists(path)) continue;
            var info = FileVersionInfo.GetVersionInfo(path);
            if (string.Equals(info.OriginalFilename, "RTSS.exe", StringComparison.OrdinalIgnoreCase)
                && (info.ProductName == "RTSS" || info.ProductName?.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase) == true)) return path;
        }
        return null;
    }
    public static bool StartInstalledServer()
    {
        string? path = FindInstalledServer(); if (path == null) return false;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(path)! });
            return process != null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Write(e); return false; }
    }
    public static string? ImagePath(string file)
    {
        try { _ = ProtocolEncoding.GetByteCount(file); return file; }
        catch (EncoderFallbackException)
        {
            var shortName = new StringBuilder(1024);
            if (GetShortPathName(file, shortName, shortName.Capacity) == 0) return null;
            try { _ = ProtocolEncoding.GetByteCount(shortName.ToString()); return shortName.ToString(); } catch (EncoderFallbackException) { return null; }
        }
    }
    [DllImport("kernel32.dll")] private static extern uint GetACP();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern uint GetShortPathName(string path, StringBuilder shortPath, int capacity);
}
