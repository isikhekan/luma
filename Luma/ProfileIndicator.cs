using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Luma;

/// <summary>A single bounded, click-through window. Never activates or injects into games.</summary>
public sealed class ProfileIndicator : Window
{
    private readonly TextBlock _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _message = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0), Foreground = ColorBrush("#B9BDCB") };
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly FeedbackGate _gate = new();
    private int _generation;
    public ProfileIndicator()
    {
        Width = 350; SizeToContent = SizeToContent.Height; MaxHeight = 230;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; ShowActivated = false; Topmost = true; Focusable = false; IsHitTestVisible = false;
        FontFamily = new("Segoe UI Variable, Segoe UI, Microsoft YaHei UI"); Foreground = ColorBrush("#FFF0DC");
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = "luma", Foreground = ColorBrush("#FFC681"), FontSize = 11, Margin = new(0, 0, 0, 8) });
        text.Children.Add(_title); text.Children.Add(_message);
        var card = new Border { Background = ColorBrush("#F51B1E26"), BorderBrush = ColorBrush("#73604A"), BorderThickness = new(1),
            CornerRadius = new(14), Padding = new(19, 15, 19, 17), Margin = new(5), Child = text, RenderTransform = _slide };
        Content = card;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x80L | 0x20L));
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
            { if (msg == 0x21) { handled = true; return new IntPtr(3); } return IntPtr.Zero; });
        };
        _hide.Tick += (_, _) => Dismiss();
        Closed += (_, _) => _hide.Stop();
    }
    public void Present(ProfileFeedback feedback)
    {
        if (!_gate.Accept(feedback.Key, DateTimeOffset.Now)) return;
        _hide.Stop(); _generation++;
        _title.Text = Loc.T(feedback.Title); _message.Text = feedback.Message;
        Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        // Measure content directly: a hidden Window can report a zero DesiredSize.
        var content = (FrameworkElement)Content;
        content.Measure(new Size(350, 230));
        double height = Math.Clamp(content.DesiredSize.Height, 90, 230);
        // Follow the foreground game/app rather than whichever monitor contains the cursor.
        var monitor = Native.MonitorFromWindow(Native.GetForegroundWindow(), 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        _ = GetDpiForMonitor(monitor, 0, out uint dpi, out _); double scale = dpi > 0 ? dpi / 96.0 : 1;
        Left = (info.Work.Right - 20) / scale - Width;
        Top = Math.Max(info.Work.Top / scale + 16, (info.Work.Bottom - 32) / scale - height);
        Show();
        if (SystemParameters.ClientAreaAnimation)
        {
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(Width, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)));
        }
        else { _slide.BeginAnimation(TranslateTransform.XProperty, null); _slide.X = 0; BeginAnimation(OpacityProperty, null); Opacity = 1; }
        _hide.Start();
    }
    public void Dismiss()
    {
        _hide.Stop(); int generation = ++_generation;
        if (!IsVisible) return;
        if (!SystemParameters.ClientAreaAnimation) { Hide(); return; }
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(210));
        fade.Completed += (_, _) => { if (_generation == generation) Hide(); };
        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Width, TimeSpan.FromMilliseconds(230)));
    }
    internal void RenderPreview(string file)
    {
        var handle = new WindowInteropHelper(this).EnsureHandle();
        long styles = GetWindowLongPtr(handle, -20).ToInt64();
        if (ShowActivated || ShowInTaskbar || (styles & 0x08000020L) != 0x08000020L)
            throw new InvalidOperationException("Profile indicator must remain nonactivating and click-through.");
        _title.Text = Loc.T("Oyun algılandı"); _message.Text = Loc.F($"{Loc.T("Gaming")} · {2} ekran");
        var element = (FrameworkElement)Content;
        element.Measure(new Size(350, 230)); element.Arrange(new Rect(0, 0, 350, element.DesiredSize.Height)); element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(350, (int)Math.Ceiling(element.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream = System.IO.File.Create(file); png.Save(stream);
    }
    internal static (int Width, int Height) RenderOverlayImage(ProfileFeedback feedback, string file)
    {
        // Render one cached image per notification; RTSS reuses it during animation and hold.
        // Rasterization preserves every language independently of RTSS's ANSI font renderer.
        var card = new ProfileIndicator();
        card._title.Text = Loc.T(feedback.Title); card._message.Text = feedback.Message;
        card.Language = System.Windows.Markup.XmlLanguage.GetLanguage(Loc.Culture.IetfLanguageTag);
        var element = (FrameworkElement)card.Content;
        element.Measure(new Size(350, 230)); int height = (int)Math.Ceiling(element.DesiredSize.Height);
        element.Arrange(new Rect(0, 0, 350, height)); element.UpdateLayout();
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(350, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(element);
        var png = new System.Windows.Media.Imaging.PngBitmapEncoder(); png.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using (var stream = System.IO.File.Create(file)) png.Save(stream);
        card.Close(); return (350, height);
    }
    private static SolidColorBrush ColorBrush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
}
