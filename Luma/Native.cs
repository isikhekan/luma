using System;
using System.Runtime.InteropServices;

namespace Luma;

internal static class Native
{
    internal delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left, Top, Right, Bottom; public readonly int Width => Right - Left; public readonly int Height => Bottom - Top; }
    [StructLayout(LayoutKind.Sequential)] internal struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct MonitorInfo
    {
        public uint Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DisplayDevice
    {
        public uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct PhysicalMonitor
    {
        public IntPtr Handle;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields; public int PositionX, PositionY; public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel, PelsWidth, PelsHeight, DisplayFlags, DisplayFrequency, ICMMethod, ICMIntent, MediaType, DitherType, Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct Luid { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rational { public uint Numerator, Denominator; }
    [StructLayout(LayoutKind.Sequential)] internal struct SourceInfo { public Luid Adapter; public uint Id, ModeIndex, Status; }
    [StructLayout(LayoutKind.Sequential)] internal struct TargetInfo
    {
        public Luid Adapter; public uint Id, ModeIndex, OutputTechnology, Rotation, Scaling;
        public Rational Refresh; public uint ScanLine, Available, Status;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct DisplayPath { public SourceInfo Source; public TargetInfo Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct InfoHeader { public uint Type, Size; public Luid Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct SourceName
    {
        public InfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct AdvancedColorInfo { public InfoHeader Header; public uint Value, Encoding, BitsPerChannel; }
    [StructLayout(LayoutKind.Sequential)] internal struct AdvancedColorInfo2 { public InfoHeader Header; public uint Value, Encoding, BitsPerChannel, ActiveColorMode; }
    [StructLayout(LayoutKind.Sequential)] internal struct ColorState { public InfoHeader Header; public uint Enabled; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct TargetName
    {
        public InfoHeader Header; public uint Flags, OutputTechnology; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string FriendlyName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DevicePath;
    }

    [DllImport("user32.dll")] internal static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplayDevices(string? device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr CreateDC(string driver, string device, string? output, IntPtr init);
    [DllImport("gdi32.dll")] internal static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] internal static extern bool GetDeviceGammaRamp(IntPtr dc, [Out] ushort[] ramp);
    [DllImport("gdi32.dll")] internal static extern bool SetDeviceGammaRamp(IntPtr dc, [In] ushort[] ramp);
    [DllImport("dxva2.dll")] internal static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);
    [DllImport("dxva2.dll", CharSet = CharSet.Unicode)] internal static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] internal static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);
    [DllImport("dxva2.dll")] internal static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll")] internal static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);
    [DllImport("dxva2.dll")] internal static extern bool GetCapabilitiesStringLength(IntPtr monitor, out uint length);
    [DllImport("dxva2.dll", CharSet = CharSet.Ansi)] internal static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr monitor, System.Text.StringBuilder text, uint length);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern bool EnumDisplaySettings(string device, int index, ref DevMode mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int ChangeDisplaySettingsEx(string? device, ref DevMode mode, IntPtr window, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")] internal static extern int ApplyDisplaySettings(string? device, IntPtr mode, IntPtr window, uint flags, IntPtr param);
    [DllImport("user32.dll")] internal static extern int SetDisplayConfig(uint pathCount, IntPtr paths, uint modeCount, IntPtr modes, uint flags);
    [DllImport("user32.dll")] internal static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] internal static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] DisplayPath[] pathInfo, ref uint modes, IntPtr modeInfo, IntPtr topology);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetSourceName(ref SourceName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetTargetName(ref TargetName info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetAdvancedColor(ref AdvancedColorInfo info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo")] internal static extern int GetAdvancedColor2(ref AdvancedColorInfo2 info);
    [DllImport("user32.dll", EntryPoint = "DisplayConfigSetDeviceInfo")] internal static extern int SetColorState(ref ColorState info);
    [DllImport("user32.dll")] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] internal static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect value, uint size);
}
