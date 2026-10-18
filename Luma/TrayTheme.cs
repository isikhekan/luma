using System.Drawing;
using System.Windows.Forms;

namespace Luma;

internal sealed class TrayColors : ProfessionalColorTable
{
    private static Color Surface => Color.FromArgb(27, 30, 38);
    private static Color Selected => Color.FromArgb(65, 52, 37);
    public override Color ToolStripDropDownBackground => Surface;
    public override Color ImageMarginGradientBegin => Surface;
    public override Color ImageMarginGradientMiddle => Surface;
    public override Color ImageMarginGradientEnd => Surface;
    public override Color MenuBorder => Color.FromArgb(65, 67, 77);
    public override Color MenuItemBorder => Color.FromArgb(116, 89, 57);
    public override Color MenuItemSelected => Selected;
    public override Color MenuItemSelectedGradientBegin => Selected;
    public override Color MenuItemSelectedGradientEnd => Selected;
    public override Color MenuItemPressedGradientBegin => Selected;
    public override Color MenuItemPressedGradientMiddle => Selected;
    public override Color MenuItemPressedGradientEnd => Selected;
    public override Color SeparatorDark => Color.FromArgb(51, 55, 66);
    public override Color SeparatorLight => Surface;
}
