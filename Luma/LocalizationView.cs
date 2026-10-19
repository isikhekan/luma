using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;

namespace Luma;

public partial class MainWindow
{
    private TextBlock? _gameOverlayStatus;
    private Button? _gameOverlaySetupButton;
    private ProgressBar? _gameOverlaySetupProgress;
    private StackPanel? _gameOverlayConsent;
    internal void ShowGameOverlaySetup() { _page = 4; RenderNavigation(); RenderPage(); }
    internal void RefreshGameOverlayStatus()
    {
        var app = (App)Application.Current;
        if (_gameOverlayStatus != null) _gameOverlayStatus.Text = Loc.T(app.GameOverlayStatus);
        bool enabled = _engine.Settings.GameOverlay && _engine.Settings.ProfileNotifications;
        if (_gameOverlaySetupProgress != null) _gameOverlaySetupProgress.Visibility = enabled && app.GameOverlaySetupBusy ? Visibility.Visible : Visibility.Collapsed;
        if (_gameOverlaySetupButton != null)
        {
            _gameOverlaySetupButton.IsEnabled = enabled && !app.GameOverlaySetupBusy && app.GameOverlaySetupState != RtssSetupState.NotChecked;
            _gameOverlaySetupButton.Content = Loc.T(app.GameOverlaySetupBusy ? "Hazırlanıyor…" : app.GameOverlaySetupState == RtssSetupState.Ready ? "Bağlantıyı kontrol et" : app.GameOverlayInstallMissing && !_engine.Settings.RtssInstallApproved ? "Kabul et ve otomatik kur" : "Yeniden dene");
        }
        if (_gameOverlayConsent != null) _gameOverlayConsent.Visibility = app.GameOverlaySetupState == RtssSetupState.Ready ? Visibility.Collapsed : Visibility.Visible;
    }
    public event Action<ProfileFeedback>? SettingApplied;
    private void ApplyLanguage()
    {
        Language = XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        Title = Loc.T("Luma · Ekranın, tam kararında");
        PauseHeader.ToolTip = Loc.T("Renk filtresini duraklat / sürdür");
        AutomationProperties.SetName(PauseHeader, Loc.T("Renk filtresini duraklat veya sürdür"));
        AutomationProperties.SetName(PinButton, Loc.T("Paneli açık tut"));
        HideHeader.ToolTip = Loc.T("Sistem tepsisine küçült · Esc");
        AutomationProperties.SetName(HideHeader, Loc.T("Sistem tepsisine küçült"));
    }
    private void OnLanguageChanged()
    {
        ApplyLanguage(); ToastBorder.Visibility = Visibility.Collapsed;
        if (_ready) { RenderDisplays(); RenderNavigation(); RenderPage(); }
    }
    private void RenderLanguageSettings()
    {
        var language = new StackPanel();
        language.Children.Add(T(Loc.T("Dil / Language"), 13, "#F3D3AC", FontWeights.SemiBold));
        language.Children.Add(T(Loc.T("Dil değişikliği hemen uygulanır. Monitör, oyun ve özel profil adların korunur."), 10, "#A0A8BA", margin: new(0, 7, 0, 12)));
        var choice = new ComboBox { ItemsSource = Loc.Languages, DisplayMemberPath = "Name", SelectedValuePath = "Code", SelectedValue = Loc.Normalize(_engine.Settings.Language) };
        AutomationProperties.SetName(choice, Loc.T("Dil seçimi"));
        choice.SelectionChanged += (_, _) =>
        {
            if (choice.SelectedValue is not string code || code == _engine.Settings.Language) return;
            _engine.Settings.Language = code; _engine.SaveSoon(); Loc.Set(code);
        };
        language.Children.Add(choice);
        language.Children.Add(Divider());
        language.Children.Add(SwitchRow(Loc.T("Profil bildirimleri"), Loc.T("Oyun ve ayar değişikliklerini ekranın sağında kısa bir belirteçle göster."), _engine.Settings.ProfileNotifications,
            value => { _engine.Settings.ProfileNotifications = value; _engine.SettingsChanged(); ((App)Application.Current).PrepareGameOverlay(); }));
        language.Children.Add(Btn(Loc.T("Bildirimi dene"), () => ReportSetting(Loc.T("Bu belirteç birkaç saniye sonra kendiliğinden kaybolur."), "preview", "Bildirim örneği"), new(0, 12, 0, 0)));
        language.Children.Add(Divider());
        language.Children.Add(SwitchRow(Loc.T("Oyun içi tam ekran bildirimi"), Loc.T("Kurulu RTSS ile oyunun içinde göster. FPS etkisi oyuna bağlıdır; sıfır kayıp garanti edilemez."), _engine.Settings.GameOverlay,
            value => { _engine.Settings.GameOverlay = value; _engine.SettingsChanged(); ((App)Application.Current).PrepareGameOverlay(); }));
        _gameOverlayStatus = T(Loc.T(((App)Application.Current).GameOverlayStatus), 10, "#B8CBA9", margin: new(0, 10, 0, 0)); language.Children.Add(_gameOverlayStatus);
        _gameOverlaySetupProgress = new ProgressBar { IsIndeterminate = true, Height = 3, Margin = new(0, 10, 0, 0), Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_gameOverlaySetupProgress, Loc.T("RTSS kurulum durumu")); language.Children.Add(_gameOverlaySetupProgress);
        _gameOverlayConsent = new StackPanel();
        _gameOverlayConsent.Children.Add(T(Loc.T("RTSS eksikse Luma indirip kurar ve bildirim bağlantısını hazırlar. İlk kurulum için aşağıdaki koşulları kabul et; Windows yönetici izni isteyebilir."), 10, "#A0A8BA", margin: new(0, 10, 0, 0)));
        _gameOverlayConsent.Children.Add(T(Loc.T("Kurulum oyun veya tam ekran uygulama sırasında ertelenir. Mevcut RTSS profilleri ve FPS sınırları korunur."), 10, "#A0A8BA", margin: new(0, 7, 0, 0)));
        _gameOverlayConsent.Children.Add(Btn(Loc.T("RTSS koşulları ve resmî indirme"), () => OpenSetupLink(WindowsRtssSetup.PublisherUrl), new(0, 10, 0, 0)));
        _gameOverlayConsent.Children.Add(Btn(Loc.T("Windows paket yöneticisi koşulları"), () => OpenSetupLink(WindowsRtssSetup.PackageManagerTermsUrl), new(0, 6, 0, 0)));
        _gameOverlayConsent.Children.Add(Btn(Loc.T("Paket yöneticisi eksikse App Installer'ı edin"), () => OpenSetupLink(WindowsRtssSetup.PackageManagerUrl), new(0, 6, 0, 0)));
        language.Children.Add(_gameOverlayConsent);
        _gameOverlaySetupButton = Btn(Loc.T("Kabul et ve otomatik kur"), () =>
        {
            var app = (App)Application.Current;
            if (!app.GameOverlayInstallMissing) app.PrepareGameOverlay();
            else app.ApproveGameOverlaySetup();
        }, new(0, 12, 0, 0));
        AutomationProperties.SetName(_gameOverlaySetupButton, Loc.T("RTSS otomatik kurulumu")); language.Children.Add(_gameOverlaySetupButton);
        language.Children.Add(T(Loc.T("RTSS'nin izin verdiği oyunlarda çalışır. Anti-cheat veya devre dışı OSD bildirimi engelleyebilir."), 10, "#A0A8BA", margin: new(0, 7, 0, 0)));
        RefreshGameOverlayStatus();
        ViewPanel.Children.Add(Card(language, new(0, 0, 0, 12)));
    }
    private static void OpenSetupLink(string url)
    { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (System.ComponentModel.Win32Exception e) { Log.Write(e); } }
    private void ReportSetting(string message, string key = "setting", string title = "Ayar uygulandı")
        => SettingApplied?.Invoke(new(title, message, key + ":" + message));
    internal void CheckLanguageSwitches()
    {
        string original = _engine.Settings.Language;
        string colorBefore = System.Text.Json.JsonSerializer.Serialize(_engine.Settings.Displays);
        foreach (var option in Loc.Languages)
        {
            _page = 4; RenderPage();
            var picker = System.Linq.Enumerable.First(Descendants<ComboBox>(ViewPanel), x => ReferenceEquals(x.ItemsSource, Loc.Languages));
            picker.SelectedValue = option.Code;
            if (Loc.Language != Loc.Resolve(option.Code) || Title != Loc.T("Luma · Ekranın, tam kararında") || _engine.Settings.Language != option.Code)
                throw new InvalidOperationException("Live language switching failed: " + option.Code);
            if (colorBefore != System.Text.Json.JsonSerializer.Serialize(_engine.Settings.Displays))
                throw new InvalidOperationException("Language selection changed color settings.");
        }
        _engine.Settings.Language = original; Loc.Set(original);
    }
}
