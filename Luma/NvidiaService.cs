using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Luma;

public sealed record NvidiaRange(int Current, int Minimum, int Maximum, int Default)
{
    public bool Valid => Minimum < Maximum && Default >= Minimum && Default <= Maximum && Current >= Minimum && Current <= Maximum;
    public double Percent => Current <= Default
        ? Default == Minimum ? 50 : (Current - Minimum) * 50.0 / (Default - Minimum)
        : 50 + (Current - Default) * 50.0 / (Maximum - Default);
    public int FromPercent(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        return (int)Math.Round(percent <= 50 ? Minimum + (Default - Minimum) * percent / 50 : Default + (Maximum - Default) * (percent - 50) / 50);
    }
}

public sealed record NvidiaOutput(byte Format, byte Colorimetry, byte Range, uint Bits, uint Policy, uint Depth)
{
    public string Description => $"{FormatName(Format)} · {BitsName(Bits)} · {RangeName(Range)}";
    public static string FormatName(byte value) => value switch { 0 => "RGB", 1 => "YCbCr 4:2:2", 2 => "YCbCr 4:4:4", 3 => "YCbCr 4:2:0", 254 => "Varsayılan", _ => "Otomatik" };
    public static string BitsName(uint value) => value switch { 1 => "6 bit", 2 => "8 bit", 3 => "10 bit", 4 => "12 bit", 5 => "16 bit", _ => "Otomatik bit" };
    public static string RangeName(byte value) => value switch { 0 => "Tam (0–255)", 1 => "Sınırlı (16–235)", _ => "Otomatik aralık" };
}

public sealed record NvidiaDisplayState(string Id, string Device, uint DriverDisplayId, NvidiaRange? Vibrance, int? Hue, int? DefaultHue, NvidiaOutput? Output);
public sealed record NvidiaOption(uint Value, string Name) { public override string ToString() => Name; }
public sealed record NvidiaSettingDefinition(uint Id, string Name, string Description, IReadOnlyList<NvidiaOption> Options, bool Numeric = false);
public sealed record NvidiaDriverSetting(uint Id, string Name, uint Value, uint PredefinedValue, bool IsPredefined, bool HasPredefined, bool Explicit = true);
public sealed record NvidiaPreference(double? Vibrance = null, int? Hue = null);
public sealed record NvidiaDrsBackup(DateTimeOffset Captured, IReadOnlyList<NvidiaDriverSetting> Settings);

public sealed class NvidiaService : IDisposable
{
    public static IReadOnlyList<NvidiaSettingDefinition> Definitions { get; } = new List<NvidiaSettingDefinition>
    {
        new(0x1057EB71, "Güç yönetimi", "Maksimum performans masaüstünde güç tüketimini artırabilir.", new[] { new NvidiaOption(5, "Normal / optimum güç"), new NvidiaOption(0, "Uyarlanabilir"), new NvidiaOption(2, "Sürücü yönetiminde"), new NvidiaOption(1, "Maksimum performansı tercih et") }),
        new(0x00A879CF, "Dikey senkronizasyon", "Oyun profilleri ve oyun içi ayarlar ayrıca etkili olabilir.", new[] { new NvidiaOption(0x60925292, "Uygulama kontrolünde"), new NvidiaOption(0x08416747, "Kapalı"), new NvidiaOption(0x47814940, "Açık"), new NvidiaOption(0x18888888, "Hızlı") }),
        new(0x00CE2691, "Doku filtreleme kalitesi", "Görüntü kalitesi ve performans tercihi.", new[] { new NvidiaOption(0xfffffff6, "Yüksek kalite"), new NvidiaOption(0, "Kalite"), new NvidiaOption(10, "Performans"), new NvidiaOption(20, "Yüksek performans") }),
        new(0x0064B541, "Tercih edilen yenileme hızı", "Uygulamaların mevcut ekran modları arasındaki tercihini etkiler.", new[] { new NvidiaOption(0, "Uygulama kontrolünde"), new NvidiaOption(1, "Kullanılabilir en yüksek") }),
        new(0x1074C972, "FXAA", "Desteklenen 3D uygulamalarda sürücü kenar yumuşatması.", new[] { new NvidiaOption(0, "Kapalı"), new NvidiaOption(1, "Açık") }),
        new(0x10835002, "Maksimum kare hızı", "0 sınırsızdır. Desteklenen aralık: 20–1023 FPS.", Array.Empty<NvidiaOption>(), true)
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private IntPtr _library;
    private QueryInterface? _query;
    private bool _initialized, _disposed;
    private readonly Dictionary<uint, Delegate> _delegates = new();
    private readonly Dictionary<string, (IntPtr Handle, uint Id)> _handles = new(StringComparer.OrdinalIgnoreCase);
    public bool Available { get; private set; }
    public string Status { get; private set; } = "NVIDIA sürücüsü kontrol ediliyor…";
    public string DriverVersion { get; private set; } = "";
    public IReadOnlyDictionary<string, NvidiaDisplayState> Displays { get; private set; } = new Dictionary<string, NvidiaDisplayState>();
    public IReadOnlyList<NvidiaDriverSetting> DriverSettings { get; private set; } = Array.Empty<NvidiaDriverSetting>();
    public event Action? Changed;
    public static string BackupPath => Path.Combine(AppSettings.Folder, "nvidia-3d-before.json");

    // ABI definitions follow NVIDIA's public nvapi.h. DVC and hue are optional private
    // entry points: detect support, validate ranges and read back every write.
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr QueryInterface(uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int EnumDisplay(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int DisplayName(IntPtr handle, StringBuilder name);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int DisplayId([MarshalAs(UnmanagedType.LPStr)] string name, out uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int ErrorMessage(int code, StringBuilder message);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int DriverInfo(out uint version, StringBuilder branch);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int DvcCall(IntPtr handle, uint output, ref DvcInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HueGet(IntPtr handle, uint output, ref HueInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int HueSet(IntPtr handle, uint output, int angle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ColorCall(uint id, ref ColorData color);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int CreateSession(out IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SessionCall(IntPtr session);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int BaseProfile(IntPtr session, out IntPtr profile);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetSetting(IntPtr session, IntPtr profile, uint id, ref DrsSetting setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SetSetting(IntPtr session, IntPtr profile, ref DrsSetting setting);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int RestoreSetting(IntPtr session, IntPtr profile, uint id);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int AvailableValues(uint id, ref uint count, IntPtr values);

    [StructLayout(LayoutKind.Sequential)] internal struct DvcInfo { public uint Version; public int Current, Minimum, Maximum, Default; }
    [StructLayout(LayoutKind.Sequential)] internal struct HueInfo { public uint Version; public int Current, Default; }
    [StructLayout(LayoutKind.Explicit, Size = 24)] internal struct ColorData
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4)] public ushort Size;
        [FieldOffset(6)] public byte Command;
        [FieldOffset(8)] public byte Format;
        [FieldOffset(9)] public byte Colorimetry;
        [FieldOffset(10)] public byte Range;
        [FieldOffset(12)] public uint Bits;
        [FieldOffset(16)] public uint Policy;
        [FieldOffset(20)] public uint Depth;
    }
    // NVDRS_SETTING_V1: 4096-byte UTF-16 name, two 4100-byte unions; pack(4).
    [StructLayout(LayoutKind.Explicit, Size = 12320, Pack = 4)] internal struct DrsSetting
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4100)] public uint Id;
        [FieldOffset(4104)] public uint Type;
        [FieldOffset(4108)] public uint Location;
        [FieldOffset(4112)] public uint IsPredefined;
        [FieldOffset(4116)] public uint PredefinedValid;
        [FieldOffset(4120)] public uint Predefined;
        [FieldOffset(8220)] public uint Current;
    }

    private T? Function<T>(uint id, bool required = false) where T : Delegate
    {
        if (_delegates.TryGetValue(id, out var cached)) return (T)cached;
        var pointer = _query?.Invoke(id) ?? IntPtr.Zero;
        if (pointer == IntPtr.Zero) { if (required) throw new NotSupportedException($"NVIDIA işlevi bu sürücüde bulunamadı (0x{id:X8})."); return null; }
        var function = Marshal.GetDelegateForFunctionPointer<T>(pointer); _delegates[id] = function; return function;
    }
    private bool Initialize()
    {
        if (_initialized && Available) return true;
        if (_initialized && !Available)
        {
            _delegates.Clear(); _query = null;
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
        }
        _initialized = true;
        try
        {
            // Only load the driver-installed system DLL, never a DLL from the working directory.
            _library = NativeLibrary.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvapi64.dll"));
            _query = Marshal.GetDelegateForFunctionPointer<QueryInterface>(NativeLibrary.GetExport(_library, "nvapi_QueryInterface"));
            Check(Function<Call>(0x0150e828, true)!(), "NVAPI başlatma");
            Available = true;
            var info = Function<DriverInfo>(0x2926aaad);
            if (info != null && info(out var version, new StringBuilder(64)) == 0) DriverVersion = $"{version / 100}.{version % 100:00}";
            Status = "NVIDIA sürücüsüne doğrudan bağlı"; return true;
        }
        catch (Exception e) { Available = false; Status = "NVIDIA sürücüsüne erişilemedi"; Log.Write(e); return false; }
    }
    private void Check(int code, string operation)
    {
        if (code == 0) return;
        var buffer = new StringBuilder(64); Function<ErrorMessage>(0x6c2d048c)?.Invoke(code, buffer);
        throw new InvalidOperationException($"{operation}: {(buffer.Length > 0 ? buffer.ToString() : "sürücü hatası")} ({code}).");
    }
    public async Task RefreshAsync(IReadOnlyList<Display> displays)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            await Task.Run(() =>
            {
                if (!Initialize()) return;
                ResolveHandles();
                var states = new Dictionary<string, NvidiaDisplayState>();
                foreach (var display in displays)
                {
                    if (!_handles.TryGetValue(display.Device, out var target)) continue;
                    NvidiaRange? vibrance = null; int? hue = null, defaultHue = null; NvidiaOutput? output = null;
                    var getDvc = Function<DvcCall>(0x0e45002d);
                    var dvc = new DvcInfo { Version = 20 | (1u << 16) };
                    if (getDvc != null && Function<DvcCall>(0x4a82c2b1) != null && getDvc(target.Handle, 0, ref dvc) == 0)
                    {
                        var range = new NvidiaRange(dvc.Current, dvc.Minimum, dvc.Maximum, dvc.Default);
                        if (range.Valid) vibrance = range;
                    }
                    var getHue = Function<HueGet>(0x95b64341); var hueData = new HueInfo { Version = 12 | (1u << 16) };
                    if (getHue != null && Function<HueSet>(0xf5a0f22c) != null && getHue(target.Handle, 0, ref hueData) == 0 && hueData.Current is >= 0 and <= 359)
                    { hue = hueData.Current; defaultHue = hueData.Default; }
                    var color = NewColor(1); var getColor = Function<ColorCall>(0x92f9d80d);
                    if (getColor != null && getColor(target.Id, ref color) == 0) output = Output(color);
                    states[display.Id] = new(display.Id, display.Device, target.Id, vibrance, hue, defaultHue, output);
                }
                Displays = states;
                try { DriverSettings = ReadDrs(); } catch (Exception e) { Log.Write(e); DriverSettings = Array.Empty<NvidiaDriverSetting>(); Status = "Renk kontrolleri bağlı; 3D ayarları okunamadı"; }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    private void ResolveHandles()
    {
        _handles.Clear();
        var enumerate = Function<EnumDisplay>(0x9abdd40d, true)!;
        var name = Function<DisplayName>(0x22a78b05, true)!;
        var getId = Function<DisplayId>(0xae457190, true)!;
        for (uint i = 0; i < 64; i++)
        {
            int result = enumerate(i, out var handle);
            if (result == -7) break; // NVAPI_END_ENUMERATION
            Check(result, "NVIDIA ekran listesi");
            var buffer = new StringBuilder(64);
            if (name(handle, buffer) == 0 && getId(buffer.ToString(), out var id) == 0) _handles[buffer.ToString()] = (handle, id);
        }
    }
    public async Task SetVibranceAsync(string displayId, double percent)
    {
        if (!double.IsFinite(percent) || percent < 0 || percent > 100) throw new ArgumentOutOfRangeException(nameof(percent));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var (state, target) = Target(displayId); var data = new DvcInfo { Version = 20 | (1u << 16) };
                var get = Function<DvcCall>(0x0e45002d, true)!; var set = Function<DvcCall>(0x4a82c2b1, true)!;
                Check(get(target.Handle, 0, ref data), "Dijital canlılık okuma");
                var range = new NvidiaRange(data.Current, data.Minimum, data.Maximum, data.Default);
                if (!range.Valid) throw new InvalidOperationException("Sürücü geçerli bir canlılık aralığı bildirmedi.");
                int desired = range.FromPercent(percent); data.Current = desired;
                Check(set(target.Handle, 0, ref data), "Dijital canlılık uygulama");
                data = new DvcInfo { Version = 20 | (1u << 16) }; Check(get(target.Handle, 0, ref data), "Dijital canlılık doğrulama");
                Update(state with { Vibrance = new(data.Current, data.Minimum, data.Maximum, data.Default) });
                if (Math.Abs(data.Current - desired) > 1) throw new InvalidOperationException("Sürücü dijital canlılık değerini doğrulamadı.");
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    public async Task SetHueAsync(string displayId, int degrees)
    {
        if (degrees is < 0 or > 359) throw new ArgumentOutOfRangeException(nameof(degrees));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var (state, target) = Target(displayId);
                Check(Function<HueSet>(0xf5a0f22c, true)!(target.Handle, 0, degrees), "Renk tonu uygulama");
                var data = new HueInfo { Version = 12 | (1u << 16) };
                Check(Function<HueGet>(0x95b64341, true)!(target.Handle, 0, ref data), "Renk tonu doğrulama");
                Update(state with { Hue = data.Current, DefaultHue = data.Default });
                if (data.Current != degrees) throw new InvalidOperationException("Sürücü renk tonunu doğrulamadı.");
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    public async Task<bool> SupportsOutputAsync(string displayId, NvidiaOutput output)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var (_, target) = Target(displayId); var data = Color(output, 3);
            return Function<ColorCall>(0x92f9d80d, true)!(target.Id, ref data) == 0;
        }
        finally { _gate.Release(); }
    }
    public async Task SetOutputAsync(string displayId, NvidiaOutput output)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var (state, target) = Target(displayId); var function = Function<ColorCall>(0x92f9d80d, true)!;
                var original = NewColor(1); Check(function(target.Id, ref original), "Önceki renk çıkışını okuma");
                var data = Color(output, 3); Check(function(target.Id, ref data), "Renk çıkışı desteği");
                bool attempted = false;
                try
                {
                    data = Color(output, 2); attempted = true; Check(function(target.Id, ref data), "Renk çıkışı uygulama");
                    data = NewColor(1); Check(function(target.Id, ref data), "Renk çıkışı doğrulama");
                    var actual = Output(data); Update(state with { Output = actual });
                    if (actual.Format != output.Format || actual.Range != output.Range || actual.Bits != output.Bits) throw new InvalidOperationException("Sürücü renk çıkışını doğrulamadı.");
                }
                catch
                {
                    if (attempted)
                    {
                        original.Command = 2; int restored = function(target.Id, ref original);
                        if (restored != 0) Log.Write("NVIDIA renk çıkışı geri alma başarısız: " + restored);
                        var readback = NewColor(1); if (function(target.Id, ref readback) == 0) Update(state with { Output = Output(readback) });
                    }
                    throw;
                }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    private (NvidiaDisplayState State, (IntPtr Handle, uint Id) Target) Target(string id)
    {
        if (_disposed || !Available || !Displays.TryGetValue(id, out var state)) throw new InvalidOperationException("NVIDIA ekran bağlantısı bulunamadı. Yeniden tara.");
        ResolveHandles();
        if (!_handles.TryGetValue(state.Device, out var target) || target.Id != state.DriverDisplayId) throw new InvalidOperationException("Ekran bağlantısı değişti. Yeniden tara.");
        return (state, target);
    }
    private void Update(NvidiaDisplayState state) { var next = Displays.ToDictionary(x => x.Key, x => x.Value); next[state.Id] = state; Displays = next; }
    private static ColorData NewColor(byte cmd) => new() { Version = 24 | (5u << 16), Size = 24, Command = cmd };
    private static ColorData Color(NvidiaOutput c, byte cmd) => new() { Version = 24 | (5u << 16), Size = 24, Command = cmd, Format = c.Format, Colorimetry = c.Colorimetry, Range = c.Range, Bits = c.Bits, Policy = c.Policy, Depth = c.Depth };
    private static NvidiaOutput Output(ColorData c) => new(c.Format, c.Colorimetry, c.Range, c.Bits, c.Policy, c.Depth);

    private (IntPtr Session, IntPtr Profile) OpenSession()
    {
        Check(Function<CreateSession>(0x0694d52e, true)!(out var session), "3D oturumu açma");
        try
        {
            Check(Function<SessionCall>(0x375dbd6b, true)!(session), "3D ayarlarını yükleme");
            Check(Function<BaseProfile>(0x617bff9f, true)!(session, out var profile), "Genel 3D profili"); return (session, profile);
        }
        catch { CloseSession(session); throw; }
    }
    private void CloseSession(IntPtr session) => Function<SessionCall>(0xdad9cff8)?.Invoke(session);
    private IReadOnlyList<NvidiaDriverSetting> ReadDrs()
    {
        var (session, profile) = OpenSession();
        try { return ReadDrs(session, profile); } finally { CloseSession(session); }
    }
    private List<NvidiaDriverSetting> ReadDrs(IntPtr session, IntPtr profile)
    {
        var result = new List<NvidiaDriverSetting>(); var get = Function<GetSetting>(0x73bf8338, true)!;
        foreach (var definition in Definitions)
        {
            var data = new DrsSetting { Version = 12320 | (1u << 16) };
            int code = get(session, profile, definition.Id, ref data);
            if (code == 0 && data.Type == 0)
                result.Add(new(definition.Id, definition.Name, data.Current, data.Predefined, data.IsPredefined != 0, data.PredefinedValid != 0));
            else if (code == -160)
            {
                var buffer = Marshal.AllocHGlobal(414112);
                try
                {
                    Marshal.Copy(new byte[414112], 0, buffer, 414112); Marshal.WriteInt32(buffer, 414112 | (1 << 16));
                    uint count = 100;
                    int available = Function<AvailableValues>(0x2ec39f90, true)!(definition.Id, ref count, buffer);
                    if (available == 0 && Marshal.ReadInt32(buffer, 8) == 0)
                    {
                        uint value = unchecked((uint)Marshal.ReadInt32(buffer, 12));
                        result.Add(new(definition.Id, definition.Name, value, value, true, true, false));
                    }
                    else Log.Write($"NVIDIA varsayılanı 0x{definition.Id:X8}: {available}");
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
        return result;
    }
    public async Task ApplyDriverSettingsAsync(IReadOnlyDictionary<uint, uint> requested)
    {
        foreach (var pair in requested)
        {
            var definition = Definitions.SingleOrDefault(x => x.Id == pair.Key) ?? throw new ArgumentException("Desteklenmeyen NVIDIA ayarı.");
            if (definition.Numeric ? pair.Value != 0 && (pair.Value < 20 || pair.Value > 1023) : !definition.Options.Any(x => x.Value == pair.Value)) throw new ArgumentException("Geçersiz NVIDIA ayar değeri.");
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!Initialize() || _disposed) throw new InvalidOperationException(Status);
                var (session, profile) = OpenSession();
                try
                {
                    var before = ReadDrs(session, profile);
                    if (requested.Keys.Any(id => before.All(x => x.Id != id))) throw new InvalidOperationException("Bir 3D ayarı sürücüden okunamadı.");
                    var changed = requested.Where(x => before.First(s => s.Id == x.Key).Value != x.Value).ToList();
                    if (changed.Count == 0) return;
                    // Save the exact previous values before the first committed driver change.
                    Directory.CreateDirectory(AppSettings.Folder);
                    var previous = File.Exists(BackupPath) ? JsonSerializer.Deserialize<NvidiaDrsBackup>(File.ReadAllText(BackupPath)) : null;
                    var backups = previous?.Settings?.ToDictionary(x => x.Id) ?? new Dictionary<uint, NvidiaDriverSetting>();
                    foreach (var change in changed) backups.TryAdd(change.Key, before.First(x => x.Id == change.Key));
                    File.WriteAllText(BackupPath + ".tmp", JsonSerializer.Serialize(new NvidiaDrsBackup(previous?.Captured ?? DateTimeOffset.Now, backups.Values.ToArray()), AppSettings.JsonOptions));
                    File.Move(BackupPath + ".tmp", BackupPath, true);
                    foreach (var change in changed)
                    {
                        var setting = new DrsSetting { Version = 12320 | (1u << 16), Id = change.Key, Type = 0, Current = change.Value };
                        Check(Function<SetSetting>(0x577dd202, true)!(session, profile, ref setting), "3D ayarı uygulama");
                    }
                    Check(Function<SessionCall>(0xfcbc7e14, true)!(session), "3D ayarlarını kaydetme");
                }
                finally { CloseSession(session); }
                DriverSettings = ReadDrs();
                if (requested.Any(x => DriverSettings.All(s => s.Id != x.Key || s.Value != x.Value))) throw new InvalidOperationException("NVIDIA kaydedilen 3D ayarlarını doğrulamadı.");
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    public async Task RestoreDriverBackupAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                var backup = JsonSerializer.Deserialize<NvidiaDrsBackup>(File.ReadAllText(BackupPath)) ?? throw new InvalidDataException("NVIDIA yedeği okunamadı.");
                if (!Initialize()) throw new InvalidOperationException(Status);
                var (session, profile) = OpenSession();
                try
                {
                    foreach (var previous in backup.Settings)
                    {
                        if (!Definitions.Any(x => x.Id == previous.Id)) continue;
                        if (!previous.Explicit)
                        {
                            int code = Function<RestoreSetting>(0xe4a26362, true)!(session, profile, previous.Id);
                            if (code != -160) Check(code, "3D özel ayarını kaldırma");
                        }
                        else if (previous.IsPredefined) Check(Function<RestoreSetting>(0x53f0381e, true)!(session, profile, previous.Id), "3D varsayılan ayarını geri alma");
                        else
                        {
                            var data = new DrsSetting { Version = 12320 | (1u << 16), Id = previous.Id, Current = previous.Value };
                            Check(Function<SetSetting>(0x577dd202, true)!(session, profile, ref data), "3D ayarını geri alma");
                        }
                    }
                    Check(Function<SessionCall>(0xfcbc7e14, true)!(session), "3D yedeğini kaydetme");
                }
                finally { CloseSession(session); }
                DriverSettings = ReadDrs();
                if (backup.Settings.Any(x => Definitions.Any(d => d.Id == x.Id) && DriverSettings.All(s => s.Id != x.Id || s.Value != x.Value))) throw new InvalidOperationException("3D geri alma değerleri doğrulanamadı.");
                File.Delete(BackupPath);
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        Changed?.Invoke();
    }
    internal async Task VerifyNoopDrsRoundtripAsync()
    {
        // Write the currently effective FXAA value, verify it, then restore its
        // original inherited/explicit state. This does not change rendering behavior.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                if (!Initialize()) throw new InvalidOperationException(Status);
                var (session, profile) = OpenSession();
                try
                {
                    var before = ReadDrs(session, profile).Single(x => x.Id == 0x1074c972); bool touched = false;
                    try
                    {
                        var data = new DrsSetting { Version = 12320 | (1u << 16), Id = before.Id, Current = before.Value };
                        Check(Function<SetSetting>(0x577dd202, true)!(session, profile, ref data), "3D yazma testi"); touched = true;
                        Check(Function<SessionCall>(0xfcbc7e14, true)!(session), "3D testini kaydetme");
                        Check(Function<SessionCall>(0x375dbd6b, true)!(session), "3D testini yeniden yükleme");
                        if (ReadDrs(session, profile).Single(x => x.Id == before.Id).Value != before.Value) throw new InvalidOperationException("3D yazma testi eşleşmedi.");
                    }
                    finally
                    {
                        if (touched)
                        {
                            if (!before.Explicit)
                            {
                                int status = Function<RestoreSetting>(0xe4a26362, true)!(session, profile, before.Id);
                                if (status != -160) Check(status, "3D test özel değerini kaldırma");
                            }
                            else if (before.IsPredefined) Check(Function<RestoreSetting>(0x53f0381e, true)!(session, profile, before.Id), "3D test varsayılanını koruma");
                            else
                            {
                                var previous = new DrsSetting { Version = 12320 | (1u << 16), Id = before.Id, Current = before.Value };
                                Check(Function<SetSetting>(0x577dd202, true)!(session, profile, ref previous), "3D testini geri alma");
                            }
                            Check(Function<SessionCall>(0xfcbc7e14, true)!(session), "3D geri almayı kaydetme");
                            Check(Function<SessionCall>(0x375dbd6b, true)!(session), "3D geri almayı yükleme");
                            var restored = ReadDrs(session, profile).Single(x => x.Id == before.Id);
                            if (restored.Value != before.Value || restored.Explicit != before.Explicit) throw new InvalidOperationException("3D testi sonrası önceki durum doğrulanamadı.");
                        }
                    }
                }
                finally { CloseSession(session); }
            }).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }
    public void Dispose()
    {
        _gate.Wait();
        try
        {
            if (_disposed) return; _disposed = true;
            if (Available) Function<Call>(0xd22bdd7e)?.Invoke();
            if (_library != IntPtr.Zero) { NativeLibrary.Free(_library); _library = IntPtr.Zero; }
            _delegates.Clear(); Available = false;
        }
        finally { _gate.Release(); }
    }
}
