using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace Luma;

public partial class MainWindow
{
    private void RenderTuning(Display display)
    {
        ViewPanel.Children.Add(Heading("MONİTÖRE ÖZEL PROFİLLER", display.Name, new(0, 22, 0, 10)));
        var body = new StackPanel();
        body.Children.Add(T(display.GpuVendor + " · " + display.AdapterName, 12, "#B8CBA9", FontWeights.SemiBold));
        body.Children.Add(T("Parlaklık: " + display.BrightnessBackend + (display.Hdr ? " · HDR korunuyor" : display.ActiveColorMode == 1 ? " · Windows renk yönetimi korunuyor" : " · SDR"), 10, "#A5ADBD", margin: new(0, 8, 0, 12)));
        if (!_engine.Settings.MonitorProfiles.TryGetValue(display.Id, out var profiles))
        { body.Children.Add(T("Monitör ve ekran kartı kontrolleri hazırlanıyor…", 11, "#A5ADBD")); ViewPanel.Children.Add(Card(body)); return; }
        body.Children.Add(T(profiles.Strategy, 10, "#A5ADBD", margin: new(0, 0, 0, 12)));
        foreach (var scene in profiles.Scenes)
        {
            string mode = scene.Mode == "gaming" ? "Gaming" : "Akşam";
            string brightness = scene.Backlight is double value ? $"%{value:0} gerçek parlaklık" : $"%{scene.Color.Brightness:0} yazılımsal parlaklık";
            body.Children.Add(T(mode + " · " + brightness + $" · {scene.Color.Temperature:0} K", 11, "#DAD5CA", margin: new(0, 0, 0, 6)));
            body.Children.Add(Btn((TuningService.ActiveScene(_engine, display) == scene.Id ? "●  " : "") + mode + " uygula", async () =>
            {
                try { _dialog = true; await TuningService.ApplyAsync(_engine, display.Id, scene.Id); RenderPage(); Toast(mode + " bu ekrana uygulandı."); }
                catch (Exception e) { Log.Write(e); Toast(e.Message); }
                finally { _dialog = false; }
            }, new(0, 0, 0, 10)));
        }
        body.Children.Add(T("Yalnızca bu ekranın sunduğu kontroller kullanılır. HDR / otomatik renk yönetiminde SDR eğrisi atlanabilir. Profiller ölçülmüş renk kalibrasyonu değildir.", 10, "#A09485", margin: new(0, 5, 0, 10)));
        var restore = Btn("Bu ekranın önceki ayarlarını geri yükle", async () =>
        {
            try { _dialog = true; await TuningService.RestoreAsync(_engine, display.Id); RenderPage(); Toast("Önceki ayarlar geri yüklendi. Oyun + saat otomasyonu kapatıldı."); }
            catch (Exception e) { Log.Write(e); Toast(e.Message); }
            finally { _dialog = false; }
        }); restore.IsEnabled = File.Exists(TuningService.BackupPath(display.Id)); body.Children.Add(restore);
        ViewPanel.Children.Add(Card(body));
    }
}
