using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Luma;

public sealed record BrightnessChannel(string Instance, double Percent);
public static class WindowsBrightness
{
    private static void Release(object? value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
    private static object? Property(object item, string name)
    {
        dynamic? properties = null, property = null;
        try { properties = ((dynamic)item).Properties_; property = properties.Item(name); return property.Value; }
        finally { Release(property); Release(properties); }
    }
    private static void SetProperty(object item, string name, object value)
    {
        dynamic? properties = null, property = null;
        try { properties = ((dynamic)item).Properties_; property = properties.Item(name); property.Value = value; }
        finally { Release(property); Release(properties); }
    }
    public static string Identity(string instance)
    {
        string path = instance.Replace("\\\\?\\", "").Replace('\\', '#');
        var parts = path.Split('#');
        return parts.Length >= 3 ? parts[1] + "#" + Regex.Replace(parts[2], @"_\d+$", "") : path;
    }
    public static IReadOnlyList<BrightnessChannel> Read()
    {
        var result = new List<BrightnessChannel>(); object? locator = null, services = null, query = null;
        try
        {
            var type = Type.GetTypeFromProgID("WbemScripting.SWbemLocator"); if (type == null) return result;
            locator = Activator.CreateInstance(type); services = ((dynamic)locator!).ConnectServer(".", @"root\wmi");
            query = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active=True");
            foreach (object item in (IEnumerable)query)
            try { result.Add(new(Convert.ToString(Property(item, "InstanceName"))!, Convert.ToDouble(Property(item, "CurrentBrightness")))); }
            finally { Release(item); }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { Release(query); Release(services); Release(locator); }
        return result;
    }
    public static bool Set(string instance, double percent)
    {
        object? locator = null, services = null, query = null;
        try
        {
            locator = Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);
            services = ((dynamic)locator!).ConnectServer(".", @"root\wmi");
            query = ((dynamic)services).ExecQuery("SELECT * FROM WmiMonitorBrightnessMethods WHERE Active=True");
            foreach (object item in (IEnumerable)query)
            try
            {
                if (!string.Equals(Convert.ToString(Property(item, "InstanceName")), instance, StringComparison.OrdinalIgnoreCase)) continue;
                object? methods = null, method = null, parameters = null, input = null, output = null;
                try
                {
                    methods = ((dynamic)item).Methods_; method = ((dynamic)methods).Item("WmiSetBrightness"); parameters = ((dynamic)method).InParameters;
                    input = ((dynamic)parameters).SpawnInstance_(); SetProperty(input, "Timeout", 0u); SetProperty(input, "Brightness", (byte)Math.Round(Math.Clamp(percent, 0, 100)));
                    output = ((dynamic)item).ExecMethod_("WmiSetBrightness", input);
                    if (Convert.ToUInt32(Property(output, "ReturnValue")) != 0) return false;
                    return Read().Any(x => x.Instance.Equals(instance, StringComparison.OrdinalIgnoreCase) && Math.Abs(x.Percent - percent) <= 1);
                }
                finally { Release(output); Release(input); Release(parameters); Release(method); Release(methods); }
            }
            finally { Release(item); }
        }
        catch (Exception e) { Log.Write(e); }
        finally { Release(query); Release(services); Release(locator); }
        return false;
    }
}
