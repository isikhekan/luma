using System;
using System.Globalization;

namespace Luma;

public static class OverlayMotion
{
    public static readonly TimeSpan Animation = TimeSpan.FromMilliseconds(240);
    public static double Ease(double progress) => 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);
    public static string Markup(string image, int width, int height, int screenWidth, int screenHeight, double visible, double zoom = 1)
    {
        if (image.IndexOfAny(new[] { '"', '<', '>', '\r', '\n', '\0' }) >= 0 || !System.IO.Path.IsPathFullyQualified(image)) throw new ArgumentException("Invalid overlay image path.");
        width = Math.Clamp(width, 1, 1024); height = Math.Clamp(height, 1, 512); visible = Math.Clamp(visible, 0, 1);
        int x = Math.Max(0, screenWidth - width - 24) + (int)Math.Round((1 - visible) * (width + 24));
        int y = Math.Max(0, screenHeight - height - 32);
        byte alpha = (byte)Math.Round(visible * 255);
        zoom = double.IsFinite(zoom) ? Math.Clamp(zoom, 1, 16) : 1;
        int px = (int)Math.Round(x / zoom), py = (int)Math.Round(y / zoom);
        int drawWidth = Math.Max(1, (int)Math.Round(width / zoom)), drawHeight = Math.Max(1, (int)Math.Round(height / zoom));
        // RTSS applies the target's OSD zoom to both coordinates and image dimensions.
        // Compensate locally, without changing the user's global or other applications' OSDs.
        return string.Create(CultureInfo.InvariantCulture, $"<LI=\"{image}\"><P={px},{py}><C={alpha:X2}FFFFFF><I={drawWidth},{drawHeight},0,0,{width},{height}><C>");
    }
}
