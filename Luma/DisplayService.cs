using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Luma;

public sealed class HardwareFeature
{
    public byte Code { get; init; }
    public string Name { get; init; } = "";
    public uint Current { get; set; }
    public uint Maximum { get; init; }
    public double Percent => Maximum == 0 ? Current : Math.Round(Current * 100.0 / Maximum);
}

public sealed class Display
{
    public override string ToString() => Name;
    public string Id { get; init; } = "";
    public string Device { get; init; } = "";
    public string AdapterName { get; init; } = "Windows ekran bağdaştırıcısı";
    public string AdapterDeviceId { get; init; } = "";
    public string GpuVendor => ProfilePlanner.Vendor(AdapterDeviceId, AdapterName);
    public string? WindowsBrightnessInstance { get; internal set; }
    public double? WindowsBrightnessPercent { get; internal set; }
    public double? BrightnessPercent => Features.TryGetValue(0x10, out var f) ? f.Percent : WindowsBrightnessPercent;
    public string BrightnessBackend => Features.ContainsKey(0x10) ? "DDC/CI" : WindowsBrightnessInstance != null ? "Windows WMI" : "Yazılımsal";
    public string Name { get; set; } = "Ekran";
    public int Number { get; set; }
    public bool Primary { get; init; }
    public bool Hdr { get; internal set; }
    public uint ActiveColorMode { get; internal set; }
    public uint AdvancedColorFlags { get; internal set; }
    public bool ColorInfoKnown { get; init; }
    public bool GammaAvailable { get; init; }
    public bool SoftwareControlFailed { get; internal set; }
    public string ColorStatus { get; set; } = "Hazır";
    public string HardwareStatus { get; set; } = "Kontrol ediliyor…";
    public IReadOnlyDictionary<byte, HardwareFeature> Features { get; internal set; } = new Dictionary<byte, HardwareFeature>();
    public IReadOnlyDictionary<uint, string> Inputs { get; internal set; } = new Dictionary<uint, string>();
    internal Native.Rect Bounds { get; init; }
    internal Native.Rect Work { get; init; }
    internal IntPtr MonitorHandle { get; init; }
    internal Native.PhysicalMonitor[] Physical { get; set; } = Array.Empty<Native.PhysicalMonitor>();
    internal ushort[] Baseline { get; init; } = ColorMath.Identity();
    public uint Refresh { get; init; }
    public uint PixelWidth { get; init; }
    public uint PixelHeight { get; init; }
    public string Detail => $"{PixelWidth} × {PixelHeight} · {Refresh} Hz{(Hdr ? " · HDR" : "")}";
}

public sealed class DisplayService : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _colorGate = new(1, 1);
    private readonly Dictionary<string, ushort[]> _baselines = new();
    private bool _disposed;
    public IReadOnlyList<Display> Monitors { get; private set; } = Array.Empty<Display>();
    public IReadOnlyList<string> Gpus { get; private set; } = Array.Empty<string>();
    public event Action? HardwareLoaded;

    public async Task RefreshAsync(bool includeHardware = false)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await Task.Run(() =>
            {
                foreach (var old in Monitors) Release(old);
                var metadata = ReadDisplayConfig();
                var adapters = new Dictionary<string, Native.DisplayDevice>(StringComparer.OrdinalIgnoreCase);
                for (uint i = 0; i < 32; i++)
                {
                    var adapter = new Native.DisplayDevice { Size = (uint)Marshal.SizeOf<Native.DisplayDevice>() };
                    if (!Native.EnumDisplayDevices(null, i, ref adapter, 0)) break;
                    if ((adapter.StateFlags & 1) != 0) adapters[adapter.DeviceName] = adapter;
                }
                var found = new List<Display>();
                Native.MonitorEnum callback = (IntPtr handle, IntPtr dc, ref Native.Rect rect, IntPtr data) =>
                {
                    var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
                    if (!Native.GetMonitorInfo(handle, ref info)) return true;
                    metadata.TryGetValue(info.Device, out var meta);
                    adapters.TryGetValue(info.Device, out var sourceAdapter);
                    string id = !string.IsNullOrWhiteSpace(meta.Path) ? meta.Path : info.Device;
                    var baseline = new ushort[768]; bool gamma = false;
                    var hdc = Native.CreateDC("DISPLAY", info.Device, null, IntPtr.Zero);
                    try { gamma = hdc != IntPtr.Zero && Native.GetDeviceGammaRamp(hdc, baseline); }
                    finally { if (hdc != IntPtr.Zero) Native.DeleteDC(hdc); }
                    if (!_baselines.TryGetValue(id, out var original))
                    {
                        original = gamma ? baseline : ColorMath.Identity();
                        _baselines[id] = original;
                    }
                    var mode = new Native.DevMode { Size = (ushort)Marshal.SizeOf<Native.DevMode>() };
                    Native.EnumDisplaySettings(info.Device, -1, ref mode);
                    var monitor = new Display
                    {
                        Id = id, Device = info.Device, MonitorHandle = handle, Primary = (info.Flags & 1) != 0,
                        AdapterName = sourceAdapter.DeviceString ?? "Windows ekran bağdaştırıcısı", AdapterDeviceId = sourceAdapter.DeviceId ?? "",
                        Bounds = info.Monitor, Work = info.Work, Name = string.IsNullOrWhiteSpace(meta.Name) ? "Monitör" : meta.Name,
                        Hdr = meta.Hdr, ActiveColorMode = meta.ColorMode, AdvancedColorFlags = meta.Flags, ColorInfoKnown = meta.Known, GammaAvailable = gamma, Baseline = original, Refresh = mode.DisplayFrequency, PixelWidth = mode.PelsWidth, PixelHeight = mode.PelsHeight,
                        ColorStatus = meta.Hdr ? "HDR: renk filtresi devre dışı" : meta.ColorMode == 1 ? "Windows otomatik renk yönetimi açık; filtre atlandı" : !meta.Known ? "HDR durumu okunamadı; renk filtresi kapalı" : gamma ? "Hazır" : "Sürücü renk filtresini desteklemiyor"
                    };
                    found.Add(monitor); return true;
                };
                Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
                found = found.OrderBy(x => x.Device, StringComparer.Ordinal).ToList();
                for (int i = 0; i < found.Count; i++) found[i].Number = i + 1;
                if (includeHardware) Parallel.ForEach(found, ReadHardware);
                Monitors = found;
                var gpus = new List<string>();
                for (uint i = 0; i < 32; i++)
                {
                    var device = new Native.DisplayDevice { Size = (uint)Marshal.SizeOf<Native.DisplayDevice>() };
                    if (!Native.EnumDisplayDevices(null, i, ref device, 0)) break;
                    if ((device.StateFlags & 1) != 0 && !gpus.Contains(device.DeviceString)) gpus.Add(device.DeviceString);
                }
                Gpus = gpus;
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task LoadHardwareAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await Task.Run(() =>
            {
                Parallel.ForEach(Monitors, ReadHardware);
                var windows = WindowsBrightness.Read();
                foreach (var monitor in Monitors)
                {
                    var channel = windows.FirstOrDefault(x => WindowsBrightness.Identity(x.Instance).Equals(WindowsBrightness.Identity(monitor.Id), StringComparison.OrdinalIgnoreCase));
                    if (channel != null) { monitor.WindowsBrightnessInstance = channel.Instance; monitor.WindowsBrightnessPercent = channel.Percent; }
                }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        HardwareLoaded?.Invoke();
    }

    private static Dictionary<string, (string Name, string Path, bool Hdr, bool Known, uint ColorMode, uint Flags, Native.Luid Adapter, uint Target)> ReadDisplayConfig()
    {
        var result = new Dictionary<string, (string, string, bool, bool, uint, uint, Native.Luid, uint)>();
        if (Native.GetDisplayConfigBufferSizes(2, out var paths, out var modes) != 0 || paths > 64 || modes > 256) return result;
        var pathInfo = new Native.DisplayPath[paths]; var modeInfo = Marshal.AllocHGlobal((int)modes * 64);
        try
        {
            if (Native.QueryDisplayConfig(2, ref paths, pathInfo, ref modes, modeInfo, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < paths; i++)
            {
                var p = pathInfo[i];
                var source = new Native.SourceName { Header = new() { Type = 1, Size = (uint)Marshal.SizeOf<Native.SourceName>(), Adapter = p.Source.Adapter, Id = p.Source.Id } };
                var target = new Native.TargetName { Header = new() { Type = 2, Size = (uint)Marshal.SizeOf<Native.TargetName>(), Adapter = p.Target.Adapter, Id = p.Target.Id } };
                var color = new Native.AdvancedColorInfo { Header = new() { Type = 9, Size = (uint)Marshal.SizeOf<Native.AdvancedColorInfo>(), Adapter = p.Target.Adapter, Id = p.Target.Id } };
                if (Native.GetSourceName(ref source) != 0) continue;
                Native.GetTargetName(ref target);
                bool known = Native.GetAdvancedColor(ref color) == 0;
                var color2 = new Native.AdvancedColorInfo2 { Header = new() { Type = 15, Size = (uint)Marshal.SizeOf<Native.AdvancedColorInfo2>(), Adapter = p.Target.Adapter, Id = p.Target.Id } };
                bool modern = Native.GetAdvancedColor2(ref color2) == 0;
                bool hdr = modern ? color2.ActiveColorMode == 2 : known && (color.Value & 2) != 0;
                result[source.Name] = (target.FriendlyName ?? "", target.DevicePath ?? "", hdr, known || modern, modern ? color2.ActiveColorMode : hdr ? 2u : 0u, modern ? color2.Value : color.Value, p.Target.Adapter, p.Target.Id);
            }
        }
        catch (Exception e) { Log.Write(e); }
        finally { Marshal.FreeHGlobal(modeInfo); }
        return result;
    }

    private static void ReadHardware(Display display)
    {
        var features = new Dictionary<byte, HardwareFeature>();
        var inputs = new Dictionary<uint, string>();
        if (!Native.GetNumberOfPhysicalMonitorsFromHMONITOR(display.MonitorHandle, out var count) || count == 0 || count > 16)
        { display.HardwareStatus = "DDC/CI bağlantısı bulunamadı"; return; }
        var physical = new Native.PhysicalMonitor[count];
        if (!Native.GetPhysicalMonitorsFromHMONITOR(display.MonitorHandle, count, physical))
        { display.HardwareStatus = "Monitör donanımına erişilemedi"; return; }
        display.Physical = physical;
        var handle = physical[0].Handle;
        foreach (var (code, name) in new (byte, string)[] { (0x10, "Parlaklık"), (0x12, "Kontrast"), (0x16, "Kırmızı"), (0x18, "Yeşil"), (0x1A, "Mavi"), (0x62, "Hoparlör sesi"), (0x60, "Giriş kaynağı") })
        {
            if (Native.GetVCPFeatureAndVCPFeatureReply(handle, code, out _, out var current, out var max) && (max > 0 || code == 0x60))
                features[code] = new() { Code = code, Name = name, Current = code == 0x60 ? current & 0xff : current, Maximum = max };
        }
        if (features.ContainsKey(0x60) && Native.GetCapabilitiesStringLength(handle, out var length) && length > 0 && length < 65536)
        {
            var text = new StringBuilder((int)length);
            if (Native.CapabilitiesRequestAndCapabilitiesReply(handle, text, length))
            {
                var match = Regex.Match(text.ToString(), @"(?i)(?:\s|\()60\s*\(([^)]*)\)");
                if (match.Success)
                    foreach (var value in match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (uint.TryParse(value, System.Globalization.NumberStyles.HexNumber, null, out var input))
                            inputs[input] = InputName(input);
            }
        }
        display.Features = features; display.Inputs = inputs;
        display.HardwareStatus = features.Count > 0 ? "DDC/CI bağlı" : "DDC/CI yanıt vermiyor · monitör menüsünden açabilirsin";
    }

    public static string InputName(uint code) => code switch { 0x01 => "VGA", 0x03 => "DVI 1", 0x04 => "DVI 2", 0x0F => "DisplayPort 1", 0x10 => "DisplayPort 2", 0x11 => "HDMI 1", 0x12 => "HDMI 2", 0x1B => "USB-C", _ => $"Giriş 0x{code:X2}" };

    public async Task ApplyColorsAsync(IReadOnlyDictionary<string, ColorSettings> colors, bool verify, CancellationToken cancellation = default)
    {
        await _colorGate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await Task.Run(() =>
            {
                foreach (var display in Monitors)
                {
                    if (cancellation.IsCancellationRequested) break;
                    if (display.ActiveColorMode != 0 || !display.ColorInfoKnown || !display.GammaAvailable || display.SoftwareControlFailed || !colors.TryGetValue(display.Id, out var setting)) continue;
                    var ramp = ColorMath.Build(setting, display.Baseline);
                    var hdc = Native.CreateDC("DISPLAY", display.Device, null, IntPtr.Zero);
                    try
                    {
                        if (hdc == IntPtr.Zero || !Native.SetDeviceGammaRamp(hdc, ramp)) display.ColorStatus = "Sürücü ayarı uygulayamadı";
                        else if (verify)
                        {
                            var actual = new ushort[768];
                            bool read = Native.GetDeviceGammaRamp(hdc, actual);
                            bool matches = read && actual.Zip(ramp, (a, b) => Math.Abs(a - b)).Max() < 768;
                            display.ColorStatus = matches ? "Renk ayarı uygulandı" : "Sürücü doğrulamadı · Gece Işığı / HDR'ı kontrol et";
                        }
                    }
                    finally { if (hdc != IntPtr.Zero) Native.DeleteDC(hdc); }
                }
            }, cancellation).ConfigureAwait(false);
        }
        finally { _colorGate.Release(); }
    }

    public async Task<bool> SetHardwareAsync(string id, byte code, uint value)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return false;
            var display = Monitors.FirstOrDefault(x => x.Id == id);
            if (display == null || display.Physical.Length == 0 || !display.Features.TryGetValue(code, out var feature)) return false;
            if (code == 0x60 ? !display.Inputs.ContainsKey(value) : value > feature.Maximum) return false;
            return await Task.Run(() =>
            {
                if (!Native.SetVCPFeature(display.Physical[0].Handle, code, value)) return false;
                // Input changes disconnect the DDC path on many monitors. Do not read it again.
                if (code == 0x60) { feature.Current = value; return true; }
                if (!Native.GetVCPFeatureAndVCPFeatureReply(display.Physical[0].Handle, code, out _, out var current, out _)) return false;
                feature.Current = current;
                return Math.Abs((long)current - value) <= 1;
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public async Task<bool> SetBrightnessAsync(string id, double percent)
    {
        var monitor = Monitors.SingleOrDefault(x => x.Id == id); if (monitor == null) return false;
        if (monitor.Features.TryGetValue(0x10, out var feature)) return await SetHardwareAsync(id, 0x10, (uint)Math.Round(feature.Maximum * Math.Clamp(percent, 0, 100) / 100));
        if (monitor.WindowsBrightnessInstance == null) return false;
        bool changed = await Task.Run(() => WindowsBrightness.Set(monitor.WindowsBrightnessInstance, percent));
        if (changed) monitor.WindowsBrightnessPercent = Math.Round(percent);
        return changed;
    }

    public async Task RestoreAsync()
    {
        await _colorGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => { foreach (var monitor in Monitors) Restore(monitor); }).ConfigureAwait(false); }
        finally { _colorGate.Release(); }
    }
    public async Task RestoreColorAsync(string id)
    {
        await _colorGate.WaitAsync().ConfigureAwait(false);
        try { await Task.Run(() => { var display = Monitors.FirstOrDefault(x => x.Id == id); if (display != null) Restore(display); }).ConfigureAwait(false); }
        finally { _colorGate.Release(); }
    }
    public async Task SetWideColorAsync(string displayId, bool enabled)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var display = Monitors.SingleOrDefault(x => x.Id == displayId) ?? throw new InvalidOperationException("Renk yönetimi için ekran bulunamadı.");
            await Task.Run(() =>
            {
                var metadata = ReadDisplayConfig();
                if (!metadata.TryGetValue(display.Device, out var before) || !before.Known) throw new InvalidOperationException("Windows renk modu okunamadı.");
                if (before.Hdr) throw new InvalidOperationException("HDR açıkken SDR renk yönetimi değiştirilmez.");
                if ((before.ColorMode == 1) != enabled)
                {
                    var request = new Native.ColorState { Header = new() { Type = 17, Size = (uint)Marshal.SizeOf<Native.ColorState>(), Adapter = before.Adapter, Id = before.Target }, Enabled = enabled ? 1u : 0u };
                    int code = Native.SetColorState(ref request);
                    if (code != 0) throw new InvalidOperationException($"Windows otomatik renk yönetimi değiştirilemedi ({code}).");
                }
                var after = ReadDisplayConfig();
                if (!after.TryGetValue(display.Device, out var state) || state.Hdr || (state.ColorMode == 1) != enabled) throw new InvalidOperationException("Windows renk modu doğrulanamadı.");
                display.Hdr = state.Hdr; display.ActiveColorMode = state.ColorMode; display.AdvancedColorFlags = state.Flags;
                display.ColorStatus = state.ColorMode == 0 ? "Hazır" : "Windows otomatik renk yönetimi açık; filtre atlandı";
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    private static void Restore(Display display)
    {
        if (!display.GammaAvailable || display.ActiveColorMode != 0 || !display.ColorInfoKnown) return;
        var dc = Native.CreateDC("DISPLAY", display.Device, null, IntPtr.Zero);
        try { if (dc != IntPtr.Zero) Native.SetDeviceGammaRamp(dc, display.Baseline); }
        finally { if (dc != IntPtr.Zero) Native.DeleteDC(dc); }
    }

    public async Task<Dictionary<string, NativeLayout>> ArrangeAsync(List<string> orderedIds)
    {
        await _gate.WaitAsync();
        try
        {
            var ordered = orderedIds.Select(id => Monitors.Single(x => x.Id == id)).ToList();
            if (ordered.Count != Monitors.Count || ordered.Select(x => x.Id).Distinct().Count() != Monitors.Count) throw new InvalidOperationException("Tüm ekranları birer kez seç.");
            var backup = new Dictionary<string, NativeLayout>();
            var modes = new Dictionary<string, Native.DevMode>();
            foreach (var d in ordered)
            {
                var mode = new Native.DevMode { Size = (ushort)Marshal.SizeOf<Native.DevMode>() };
                if (!Native.EnumDisplaySettings(d.Device, -1, ref mode)) throw new InvalidOperationException("Ekran yerleşimi okunamadı.");
                modes[d.Device] = mode; backup[d.Device] = new(mode.PositionX, mode.PositionY);
            }
            int primaryIndex = ordered.FindIndex(x => x.Primary); if (primaryIndex < 0) throw new InvalidOperationException("Ana ekran bulunamadı.");
            int x = -ordered.Take(primaryIndex).Sum(d => d.Bounds.Width);
            var changed = new Dictionary<string, Native.DevMode>();
            foreach (var d in ordered)
            {
                var mode = modes[d.Device]; mode.Fields = 0x20; mode.PositionX = x; mode.PositionY = 0; x += d.Bounds.Width;
                if (Native.ChangeDisplaySettingsEx(d.Device, ref mode, IntPtr.Zero, 2, IntPtr.Zero) != 0) throw new InvalidOperationException("Windows bu yerleşimi desteklemiyor.");
                changed[d.Device] = mode;
            }
            try
            {
                foreach (var item in changed)
                {
                    var mode = item.Value;
                    int code = Native.ChangeDisplaySettingsEx(item.Key, ref mode, IntPtr.Zero, 0x10000001, IntPtr.Zero);
                    if (code != 0) throw new InvalidOperationException($"Yerleşim kaydedilemedi ({code}).");
                }
                if (Native.ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero) != 0) throw new InvalidOperationException("Yerleşim uygulanamadı.");
            }
            catch { RestoreLayout(backup); throw; }
            return backup;
        }
        finally { _gate.Release(); }
    }
    public static void RestoreLayout(Dictionary<string, NativeLayout> saved)
    {
        foreach (var item in saved)
        {
            var mode = new Native.DevMode { Size = (ushort)Marshal.SizeOf<Native.DevMode>() };
            if (!Native.EnumDisplaySettings(item.Key, -1, ref mode)) continue;
            mode.Fields = 0x20; mode.PositionX = item.Value.X; mode.PositionY = item.Value.Y;
            Native.ChangeDisplaySettingsEx(item.Key, ref mode, IntPtr.Zero, 0x10000001, IntPtr.Zero);
        }
        Native.ApplyDisplaySettings(null, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
    }
    private static void Release(Display display)
    {
        if (display.Physical.Length > 0) { Native.DestroyPhysicalMonitors((uint)display.Physical.Length, display.Physical); display.Physical = Array.Empty<Native.PhysicalMonitor>(); }
    }
    public void Dispose()
    {
        _gate.Wait(); _colorGate.Wait();
        try { if (_disposed) return; _disposed = true; foreach (var d in Monitors) { Restore(d); Release(d); } }
        finally { _colorGate.Release(); _gate.Release(); }
    }
}

public sealed record NativeLayout(int X, int Y);
