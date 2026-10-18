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
        ViewPanel.Children.Add(Heading(Loc.T("MONİTÖRE ÖZEL PROFİLLER"), display.Name, new(0, 22, 0, 10)));
        var body = new StackPanel();
        body.Children.Add(T(Loc.T(display.GpuVendor) + " · " + display.AdapterName, 12, "#B8CBA9", FontWeights.SemiBold));
        body.Children.Add(T(Loc.F($"Parlaklık: {Loc.T(display.BrightnessBackend)}{(display.Hdr ? Loc.T(" · HDR korunuyor") : display.ActiveColorMode == 1 ? Loc.T(" · Windows renk yönetimi korunuyor") : " · SDR")}"), 10, "#A5ADBD", margin: new(0, 8, 0, 12)));
        if (!_engine.Settings.MonitorProfiles.TryGetValue(display.Id, out var profiles))
        { body.Children.Add(T(Loc.T("Monitör ve ekran kartı kontrolleri hazırlanıyor…"), 11, "#A5ADBD")); ViewPanel.Children.Add(Card(body)); return; }
        body.Children.Add(T(Loc.T(profiles.Strategy), 10, "#A5ADBD", margin: new(0, 0, 0, 12)));
        foreach (var scene in profiles.Scenes)
        {
            string mode = scene.Mode == "gaming" ? Loc.T("Gaming") : Loc.T("Akşam");
            string brightness = scene.Backlight is double value ? Loc.F($"%{value:0} gerçek parlaklık") : Loc.F($"%{scene.Color.Brightness:0} yazılımsal parlaklık");
            body.Children.Add(T(mode + " · " + brightness + $" · {scene.Color.Temperature:0} K", 11, "#DAD5CA", margin: new(0, 0, 0, 6)));
            body.Children.Add(Btn((TuningService.ActiveScene(_engine, display) == scene.Id ? "●  " : "") + Loc.F($"{mode} uygula"), async () =>
            {
                try { _dialog = true; await TuningService.ApplyAsync(_engine, display.Id, scene.Id); RenderPage(); Toast(Loc.F($"{mode} bu ekrana uygulandı."), true); }
                catch (Exception e) { Log.Write(e); Toast(e.Message); }
                finally { _dialog = false; }
            }, new(0, 0, 0, 10)));
        }
        body.Children.Add(T(Loc.T("Yalnızca bu ekranın sunduğu kontroller kullanılır. HDR / otomatik renk yönetiminde SDR eğrisi atlanabilir. Profiller ölçülmüş renk kalibrasyonu değildir."), 10, "#A09485", margin: new(0, 5, 0, 10)));
        var restore = Btn(Loc.T("Bu ekranın önceki ayarlarını geri yükle"), async () =>
        {
            try { _dialog = true; await TuningService.RestoreAsync(_engine, display.Id); RenderPage(); Toast(Loc.T("Önceki ayarlar geri yüklendi. Oyun + saat otomasyonu kapatıldı."), true); }
            catch (Exception e) { Log.Write(e); Toast(e.Message); }
            finally { _dialog = false; }
        }); restore.IsEnabled = File.Exists(TuningService.BackupPath(display.Id)); body.Children.Add(restore);
        ViewPanel.Children.Add(Card(body));
    }
}
