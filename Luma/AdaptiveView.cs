using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;

namespace Luma;

public partial class MainWindow
{
    private void RenderAdaptiveAutomation()
    {
        var body = new StackPanel();
        body.Children.Add(SwitchRow("Oyun + saat otomasyonu", "Oyun açıkken Gaming; kapanınca saate göre Gaming veya Akşam. Her monitöre kendi desteklenen kontrolleri uygulanır.", _engine.Settings.AdaptiveEnabled,
            enabled => { _engine.Settings.AdaptiveEnabled = enabled; _engine.AdaptiveSettingsChanged(); RenderPage(); }));
        body.Children.Add(Divider());
        body.Children.Add(T(_engine.AdaptiveStatus, 12, "#E7D1AF", FontWeights.SemiBold, margin: new(0, 2, 0, 5)));
        body.Children.Add(T(_engine.Games.Status, 10, "#9DA5B5", margin: new(0, 0, 0, 12)));
        var times = new Grid(); times.ColumnDefinitions.Add(new()); times.ColumnDefinitions.Add(new());
        void AddTime(string label, string current, int column, Action<string> save)
        {
            var stack = new StackPanel { Margin = column == 0 ? new(0, 0, 8, 0) : new(8, 0, 0, 0) };
            stack.Children.Add(T(label, 10, "#A4ABBA", margin: new(0, 0, 0, 5)));
            var input = new TextBox { Text = current, MaxLength = 5 }; AutomationProperties.SetName(input, label);
            void Commit()
            {
                if (input.Text == current) return;
                string other = column == 0 ? _engine.Settings.EveningStarts : _engine.Settings.DayStarts;
                if (!TimeOnly.TryParseExact(input.Text, "HH:mm", out _) || input.Text == other)
                { input.Text = current; Toast("Farklı iki saat seç. Biçim: 19:00"); return; }
                current = input.Text; save(current); _engine.AdaptiveSettingsChanged();
            }
            input.LostKeyboardFocus += (_, _) => Commit();
            input.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) { Commit(); System.Windows.Input.Keyboard.ClearFocus(); } };
            stack.Children.Add(input); Grid.SetColumn(stack, column); times.Children.Add(stack);
        }
        AddTime("Gündüz / Gaming başlar", _engine.Settings.DayStarts, 0, x => _engine.Settings.DayStarts = x);
        AddTime("Akşam başlar", _engine.Settings.EveningStarts, 1, x => _engine.Settings.EveningStarts = x);
        body.Children.Add(times);
        body.Children.Add(T("Alt+Tab profili değiştirmez. Son oyun kapandıktan 8 sn sonra güncel saat kuralına dönülür. Oyun önceliklidir; klasik zaman planı bu otomasyon açıkken kullanılmaz.", 10, "#959EAF", margin: new(0, 12, 0, 0)));
        ViewPanel.Children.Add(Card(body));
        var catalog = new StackPanel();
        catalog.Children.Add(T($"{_engine.Games.InstalledCount} oyun otomatik eklendi", 16, "#E7D1AF", FontWeights.SemiBold));
        catalog.Children.Add(T("Kurulum listesi her dakika yenilenir. Yeni oyunlar kendisi eklenir; kaldırılanlar listeden düşer. EXE seçmen gerekmez.", 10, "#9DA5B5", margin: new(0, 8, 0, 10)));
        var found = new StackPanel();
        foreach (var game in _engine.Games.Catalog)
        {
            var row = new StackPanel { Margin = new(0, 0, 0, 9), ToolTip = game.Directory };
            row.Children.Add(T("✓  " + game.Name, 12, "#D7DCD9"));
            row.Children.Add(T(game.Source, 9, "#929CAC", margin: new(16, 3, 0, 0))); found.Children.Add(row);
        }
        if (_engine.Games.InstalledCount == 0) found.Children.Add(T("Tarama tamamlanınca oyunların burada görünecek.", 11, "#9DA5B5"));
        catalog.Children.Add(new ScrollViewer { Content = found, MaxHeight = 290, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        if (_engine.Games.CatalogScannedAt != default) catalog.Children.Add(T("Son tarama: " + _engine.Games.CatalogScannedAt.LocalDateTime.ToString("HH:mm"), 9, "#929CAC", margin: new(0, 7, 0, 9)));
        catalog.Children.Add(Btn("↻  Oyunları yeniden tara", async () =>
        {
            try { await _engine.Games.RefreshCatalogAsync(); RenderPage(); Toast($"{_engine.Games.InstalledCount} oyun otomatik tanındı."); }
            catch (Exception e) { Log.Write(e); Toast("Tarama tamamlanamadı. Ayrıntılar günlük dosyasında."); }
        }));
        if (_engine.Games.CatalogWarnings.Count > 0) catalog.Children.Add(T("Bazı kurulum kayıtları okunamadı. Erişilebilen oyunlar eklendi; sonraki taramada yeniden kontrol edilir.", 10, "#BCA183", margin: new(0, 8, 0, 0)));
        ViewPanel.Children.Add(Card(catalog));
        var games = new StackPanel();
        games.Children.Add(T("Oyunları tanıma", 15, "#E6DECF", FontWeights.SemiBold));
        games.Children.Add(T("Steam, Epic, Riot ve diğer desteklenen kurulumlar otomatik bulunur. Buradaki uygulama ekleme ve hariç tutma seçenekleri isteğe bağlıdır. Tam ekran video oyun sayılmaz.", 10, "#9DA5B5", margin: new(0, 8, 0, 12)));
        games.Children.Add(Btn("＋  İsteğe bağlı uygulama ekle", () =>
        {
            try
            {
                _dialog = true;
                var picker = new OpenFileDialog { Title = "Oyun uygulamasını seç", Filter = "Oyun uygulaması (*.exe)|*.exe", CheckFileExists = true };
                if (picker.ShowDialog(this) != true) return;
                if (_engine.Settings.GameExecutables.Count >= 100) { Toast("En fazla 100 oyun eklenebilir."); return; }
                if (!_engine.Settings.GameExecutables.Contains(picker.FileName, StringComparer.OrdinalIgnoreCase)) _engine.Settings.GameExecutables.Add(picker.FileName);
                _engine.Settings.IgnoredGameExecutables.RemoveAll(x => string.Equals(x, picker.FileName, StringComparison.OrdinalIgnoreCase));
                _engine.AdaptiveSettingsChanged(); RenderPage();
            }
            finally { _dialog = false; }
        }));
        foreach (string path in _engine.Settings.GameExecutables.ToArray())
        {
            var row = new DockPanel { Margin = new(0, 8, 0, 0) };
            var remove = Btn("×", () => { _engine.Settings.GameExecutables.Remove(path); _engine.AdaptiveSettingsChanged(); RenderPage(); });
            remove.ToolTip = "Eklenen oyunu kaldır"; DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var name = T(Path.GetFileName(path), 11, "#BFC5D1"); name.ToolTip = path; row.Children.Add(name); games.Children.Add(row);
        }
        foreach (var game in _engine.Games.Running.GroupBy(x => x.Executable, StringComparer.OrdinalIgnoreCase).Select(x => x.First()))
        {
            games.Children.Add(Btn(game.Name + " · oyun olarak sayma", () =>
            {
                _engine.Settings.IgnoredGameExecutables.Add(game.Executable); _engine.AdaptiveSettingsChanged(); RenderPage();
            }, new(0, 8, 0, 0)));
        }
        foreach (string path in _engine.Settings.IgnoredGameExecutables.ToArray())
            games.Children.Add(Btn(Path.GetFileName(path) + " · yeniden tanı", () => { _engine.Settings.IgnoredGameExecutables.Remove(path); _engine.AdaptiveSettingsChanged(); RenderPage(); }, new(0, 8, 0, 0)));
        ViewPanel.Children.Add(Expandable("Algılama için ek seçenekler", games));
    }
}
