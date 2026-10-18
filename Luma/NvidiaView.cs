using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Luma;

public partial class MainWindow
{
    private void RenderNvidia()
    {
        PageTitle(Loc.T("NVIDIA, tam burada."), Loc.T("Sürücünün gerçek ayarları. Luma'nın içinden."));
        var intro = new StackPanel();
        intro.Children.Add(T(string.Join("\n", _engine.Displays.Gpus.DefaultIfEmpty(Loc.T("NVIDIA ekran kartı bulunamadı"))), 14, "#C4D9B3", FontWeights.SemiBold));
        intro.Children.Add(T(Loc.T(_engine.Nvidia.Status) + (_engine.Nvidia.DriverVersion.Length > 0 ? " · " + _engine.Nvidia.DriverVersion : ""), 10, "#9FAF9C", margin: new(0, 7, 0, 0)));
        ViewPanel.Children.Add(Card(intro));
        if (!_engine.NvidiaReady || !_engine.Nvidia.Available)
        {
            ViewPanel.Children.Add(T(_engine.NvidiaReady ? Loc.T("Bu sistemde erişilebilir NVIDIA kontrolü yok. AMD, Intel ve diğer bağdaştırıcılarda monitör kontrolleri ile Windows renk profilleri Ekranlar sekmesinden kullanılabilir.") : Loc.T("Ekran kartı kontrolleri hazırlanıyor…"), 12, "#A6AEBD", margin: new(0, 18, 0, 12)));
            ViewPanel.Children.Add(Btn(Loc.T("↻  Yeniden tara"), RefreshClicked)); return;
        }
        var monitors = _engine.Displays.Monitors.Where(x => _engine.Nvidia.Displays.ContainsKey(x.Id)).ToList();
        var display = monitors.FirstOrDefault(x => x.Id == _engine.SelectedDisplayId) ?? monitors.FirstOrDefault(x => x.Id == _hardwareDisplayId) ?? monitors.FirstOrDefault();
        if (display != null)
        {
            _hardwareDisplayId = display.Id;
            ViewPanel.Children.Add(Heading(Loc.T("EKRANA ÖZEL RENKLER"), Loc.T("Diğer ekranı etkilemez"), new(0, 22, 0, 10)));
            var pick = new ComboBox { ItemsSource = monitors, DisplayMemberPath = "Name", SelectedValuePath = "Id", SelectedValue = display.Id, Margin = new(0, 0, 0, 12) };
            AutomationProperties.SetName(pick, Loc.T("NVIDIA ayarları yapılacak ekran"));
            pick.SelectionChanged += (_, _) => { if (pick.SelectedValue is string id) { _engine.SelectedDisplayId = id; _hardwareDisplayId = id; RenderDisplays(); RenderPage(); } }; ViewPanel.Children.Add(pick);
            var state = _engine.Nvidia.Displays[display.Id]; var colors = new StackPanel();
            if (state.Vibrance is { } vibrance)
                AddNvidiaSlider(colors, display.Id, Loc.T("Dijital canlılık"), vibrance.Percent, vibrance.Minimum == vibrance.Default ? 50 : 0, 100, v => Loc.Percent(v), async value =>
                {
                    await _engine.Nvidia.SetVibranceAsync(display.Id, value);
                    var previous = _engine.Settings.NvidiaDisplays.GetValueOrDefault(display.Id, new());
                    _engine.Settings.NvidiaDisplays[display.Id] = previous with { Vibrance = Math.Round(value) }; _engine.SaveSoon();
                });
            if (state.Hue is int hue)
                AddNvidiaSlider(colors, display.Id, Loc.T("Renk tonu"), hue, 0, 359, v => $"{v:0}°", async value =>
                {
                    await _engine.Nvidia.SetHueAsync(display.Id, (int)Math.Round(value));
                    var previous = _engine.Settings.NvidiaDisplays.GetValueOrDefault(display.Id, new());
                    _engine.Settings.NvidiaDisplays[display.Id] = previous with { Hue = (int)Math.Round(value) }; _engine.SaveSoon();
                });
            if (state.Vibrance == null && state.Hue == null) colors.Children.Add(T(Loc.T("Bu ekran için canlılık ve ton kontrolü sürücü tarafından sunulmuyor."), 11, "#A7ACB8"));
            colors.Children.Add(T(Loc.T("Canlılıkta %50 nötrdür. Değerler sürücüden geri okunur ve Luma yeniden açıldığında korunur."), 10, "#939EAE", margin: new(0, 6, 0, 12)));
            colors.Children.Add(Btn(Loc.T("Bu ekranın canlılık / ton değerlerini nötrle"), async () =>
            {
                try
                {
                    if (state.Vibrance != null) await _engine.Nvidia.SetVibranceAsync(display.Id, 50);
                    if (state.Hue.HasValue) await _engine.Nvidia.SetHueAsync(display.Id, state.DefaultHue ?? 0);
                    _engine.Settings.NvidiaDisplays[display.Id] = new(state.Vibrance != null ? 50 : null, state.Hue.HasValue ? state.DefaultHue ?? 0 : null);
                    _engine.SaveSoon(); RenderPage(); Toast(Loc.T("Seçili ekranın NVIDIA renkleri nötrleştirildi."), true);
                }
                catch (Exception e) { Log.Write(e); Toast(e.Message); }
            }));
            ViewPanel.Children.Add(Card(colors));
            if (state.Output != null) RenderNvidiaOutput(display, state.Output);
            RenderTuning(display);
        }
        ViewPanel.Children.Add(Heading(Loc.T("GENEL 3D AYARLARI"), Loc.T("Tüm uygulamalar için"), new(0, 22, 0, 10)));
        var driver = new StackPanel(); var pending = new Dictionary<uint, uint>();
        driver.Children.Add(T(Loc.T("Bu tercihler NVIDIA'nın genel profiline kaydedilir. Oyunların kendi NVIDIA profilleri öncelikli olabilir. Değişiklikler Uygula ile kaydedilir."), 11, "#A3ABBA", margin: new(0, 0, 0, 14)));
        foreach (var definition in NvidiaService.Definitions)
        {
            var setting = _engine.Nvidia.DriverSettings.FirstOrDefault(x => x.Id == definition.Id); if (setting == null) continue;
            driver.Children.Add(T(Loc.T(definition.Name), 12, "#DDE1DA", FontWeights.SemiBold, new(0, 2, 0, 5)));
            driver.Children.Add(T(Loc.T(definition.Description), 10, "#909BA9", margin: new(0, 0, 0, 7)));
            if (definition.Numeric)
            {
                var fps = new TextBox { Text = setting.Value.ToString(), MaxLength = 4, Margin = new(0, 0, 0, 14), Padding = new(6) }; AutomationProperties.SetName(fps, Loc.T(definition.Name));
                fps.TextChanged += (_, _) => { if (uint.TryParse(fps.Text, out var value) && (value == 0 || value is >= 20 and <= 1023)) pending[definition.Id] = value; else pending[definition.Id] = uint.MaxValue; }; driver.Children.Add(fps);
            }
            else
            {
                var options = definition.Options.ToList(); if (options.All(x => x.Value != setting.Value)) options.Add(new(setting.Value, Loc.F($"Mevcut değer (0x{setting.Value:X})")));
                var choice = new ComboBox { ItemsSource = options, DisplayMemberPath = "DisplayName", SelectedValuePath = "Value", SelectedValue = setting.Value, Margin = new(0, 0, 0, 14) }; AutomationProperties.SetName(choice, Loc.T(definition.Name));
                choice.SelectionChanged += (_, _) => { if (choice.SelectedValue is uint value) { if (value == setting.Value) pending.Remove(definition.Id); else pending[definition.Id] = value; } }; driver.Children.Add(choice);
            }
        }
        var apply = Btn(Loc.T("3D değişikliklerini uygula"), async () =>
        {
            if (pending.Count == 0) { Toast(Loc.T("Önce değiştirmek istediğin bir ayarı seç.")); return; }
            if (pending.Values.Contains(uint.MaxValue)) { Toast(Loc.T("FPS için 0 veya 20–1023 arasında bir değer yaz.")); return; }
            try { await _engine.Nvidia.ApplyDriverSettingsAsync(pending); RenderPage(); Toast(Loc.T("3D ayarları sürücüye kaydedildi ve geri okunarak doğrulandı."), true); }
            catch (Exception e) { Log.Write(e); Toast(e.Message); }
        }); apply.IsEnabled = _engine.Nvidia.DriverSettings.Count > 0; apply.Foreground = Brush("#C4D9B3"); apply.BorderBrush = Brush("#4C6442"); driver.Children.Add(apply);
        if (File.Exists(NvidiaService.BackupPath)) driver.Children.Add(Btn(Loc.T("3D ayarlarını önceki yedeğe döndür"), async () =>
        {
            try { await _engine.Nvidia.RestoreDriverBackupAsync(); RenderPage(); Toast(Loc.T("Luma öncesindeki 3D ayarları geri yüklendi."), true); }
            catch (Exception e) { Log.Write(e); Toast(e.Message); }
        }, new(0, 9, 0, 0)));
        if (_engine.Nvidia.DriverSettings.Count == 0) driver.Children.Add(T(Loc.T("Genel 3D ayarları bu sürücüden okunamadı."), 11, "#C3AF91", margin: new(0, 10, 0, 0)));
        ViewPanel.Children.Add(Card(driver));
        ViewPanel.Children.Add(T(Loc.T("G-SYNC etkinleştirme ve özel çözünürlük oluşturma bu sürüme dahil değildir. Luma desteklenen sürücü kontrollerini gösterir."), 10, "#7F8A9C", margin: new(0, 14, 0, 10)));
    }
    private void AddNvidiaSlider(StackPanel panel, string id, string name, double value, double min, double max, Func<double, string> format, Func<double, Task> apply)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) }; _viewTimers.Add(timer);
        var feedback = T("", 10, "#A5C29F");
        var slider = AddSlider(panel, name, "\uE790", value, min, max, format, _ => { timer.Stop(); timer.Start(); }, 1);
        panel.Children.Add(feedback); bool busy = false; double? pending = null;
        async Task Send()
        {
            if (busy) return; busy = true;
            try
            {
                while (pending.HasValue) { var next = pending.Value; pending = null; feedback.Text = Loc.T("Sürücüye uygulanıyor…"); await apply(next); feedback.Text = Loc.T("NVIDIA sürücüsünden doğrulandı"); ReportSetting(Loc.T(name), "nvidia"); }
            }
            catch (Exception e) { Log.Write(e); feedback.Foreground = Brush("#EAC18D"); feedback.Text = Loc.Status(e.Message); }
            finally { busy = false; }
        }
        timer.Tick += async (_, _) => { timer.Stop(); pending = Math.Round(slider.Value); await Send(); };
    }
    private void RenderNvidiaOutput(Display display, NvidiaOutput output)
    {
        ViewPanel.Children.Add(Heading(Loc.T("NVIDIA RENK ÇIKIŞI"), Loc.T("Bağlantı biçimi"), new(0, 22, 0, 10)));
        var panel = new StackPanel(); panel.Children.Add(T(Loc.Status(output.Description), 12, "#C4D9B3", FontWeights.SemiBold, new(0, 0, 0, 12)));
        var format = OutputCombo(new[] { new NvidiaOption(0, "RGB"), new NvidiaOption(2, "YCbCr 4:4:4"), new NvidiaOption(1, "YCbCr 4:2:2"), new NvidiaOption(3, "YCbCr 4:2:0") }, output.Format, Loc.T("Renk biçimi"), panel);
        var range = OutputCombo(new[] { new NvidiaOption(0, Loc.T("Tam (0–255)")), new NvidiaOption(1, Loc.T("Sınırlı (16–235)")) }, output.Range, Loc.T("Dinamik aralık"), panel);
        var bits = OutputCombo(new[] { new NvidiaOption(2, "8 bit"), new NvidiaOption(3, "10 bit"), new NvidiaOption(4, "12 bit") }, output.Bits, Loc.T("Çıkış renk derinliği"), panel);
        panel.Children.Add(T(Loc.T("PC monitöründe genellikle RGB + Tam uygundur. Yalnızca sürücünün desteklediği kombinasyon uygulanır; görüntü kısa süre kesilebilir."), 10, "#959FAE", margin: new(0, 4, 0, 12)));
        var applyOutput = Btn(Loc.T("Renk çıkışını uygula"), async () =>
        {
            if (format.SelectedValue is not uint f || range.SelectedValue is not uint r || bits.SelectedValue is not uint b) return;
            var desired = output with { Format = (byte)f, Colorimetry = f == 0 ? (byte)0 : (byte)2, Range = (byte)r, Bits = b, Policy = 0, Depth = 0 };
            try
            {
                if (!await _engine.Nvidia.SupportsOutputAsync(display.Id, desired)) { Toast(Loc.T("Bu renk biçimi / bit derinliği mevcut çözünürlük ve Hz'de desteklenmiyor.")); return; }
                _dialog = true;
                Directory.CreateDirectory(AppSettings.Folder);
                var recovery = new NvidiaOutputRecovery(display.Id, output, display.ActiveColorMode);
                File.WriteAllText(NvidiaOutputRecovery.FilePath, System.Text.Json.JsonSerializer.Serialize(recovery));
                try
                {
                    await _engine.Nvidia.SetOutputAsync(display.Id, desired);
                    await _engine.Displays.SetWideColorAsync(display.Id, display.ActiveColorMode == 1);
                    bool keep = ConfirmOutput();
                    if (!keep) { await _engine.Nvidia.SetOutputAsync(display.Id, output); await _engine.Displays.SetWideColorAsync(display.Id, recovery.PreviousColorMode == 1); Toast(Loc.T("Önceki NVIDIA renk çıkışı geri yüklendi.")); }
                    else Toast(Loc.T("NVIDIA renk çıkışı kaydedildi ve doğrulandı."), true);
                    File.Delete(NvidiaOutputRecovery.FilePath);
                }
                catch { await _engine.Nvidia.SetOutputAsync(display.Id, output); await _engine.Displays.SetWideColorAsync(display.Id, recovery.PreviousColorMode == 1); File.Delete(NvidiaOutputRecovery.FilePath); throw; }
                finally { _dialog = false; }
                RenderPage();
            }
            catch (Exception e) { Log.Write(e); Toast(e.Message); }
        });
        applyOutput.IsEnabled = !display.Hdr; panel.Children.Add(applyOutput);
        if (display.Hdr) panel.Children.Add(T(Loc.T("Bu çıkış kontrolleri SDR içindir. Bu ekranda HDR açık."), 10, "#D2B78F", margin: new(0, 8, 0, 0)));
        ViewPanel.Children.Add(Card(panel));
    }
    private bool ConfirmOutput()
    {
        bool keep = false; int seconds = 15;
        var content = new StackPanel { Margin = new(20) };
        content.Children.Add(T(Loc.T("Görüntü düzgün mü?"), 18, "#F2E8DA", FontWeights.SemiBold));
        var countdown = T(Loc.T("15 saniye içinde onaylamazsan önceki çıkış geri gelir."), 12, "#ADB3BF", margin: new(0, 10, 0, 18)); content.Children.Add(countdown);
        var dialog = new Window { Owner = this, Title = Loc.T("Luma · NVIDIA renk çıkışı"), Width = 365, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner, WindowStyle = WindowStyle.ToolWindow, ResizeMode = ResizeMode.NoResize, Background = Brush("#1B1E26"), Foreground = Brushes.White, Topmost = true, ShowInTaskbar = false, Content = content };
        var buttons = new StackPanel(); buttons.Children.Add(Btn(Loc.T("Bu renk çıkışını koru"), () => { keep = true; dialog.Close(); })); buttons.Children.Add(Btn(Loc.T("Geri al"), () => dialog.Close(), new(0, 8, 0, 0))); content.Children.Add(buttons);
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; clock.Tick += (_, _) => { seconds--; countdown.Text = Loc.F($"Otomatik geri alma: {seconds} saniye"); if (seconds <= 0) dialog.Close(); };
        dialog.Closed += (_, _) => clock.Stop(); clock.Start(); dialog.ShowDialog(); return keep;
    }
    private static ComboBox OutputCombo(IEnumerable<NvidiaOption> options, uint selected, string name, StackPanel panel)
    {
        panel.Children.Add(T(name, 10, "#A1AABB", margin: new(0, 0, 0, 6)));
        var items = options.ToList(); if (items.All(x => x.Value != selected)) items.Add(new(selected, Loc.T("Sürücünün mevcut değeri")));
        var combo = new ComboBox { ItemsSource = items, DisplayMemberPath = "DisplayName", SelectedValuePath = "Value", SelectedValue = selected, Margin = new(0, 0, 0, 11) }; AutomationProperties.SetName(combo, Loc.T(name)); panel.Children.Add(combo); return combo;
    }
}

public sealed record NvidiaOutputRecovery(string DisplayId, NvidiaOutput Output, uint? PreviousColorMode = null)
{
    public static string FilePath => Path.Combine(AppSettings.Folder, "nvidia-output-pending.json");
}
