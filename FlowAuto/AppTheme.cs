namespace FlowAuto;

internal static class AppTheme
{
    public static readonly Color Window = Color.FromArgb(15, 18, 25);
    public static readonly Color Surface = Color.FromArgb(23, 27, 36);
    public static readonly Color SurfaceRaised = Color.FromArgb(31, 36, 47);
    public static readonly Color Canvas = Color.FromArgb(18, 22, 30);
    public static readonly Color Border = Color.FromArgb(48, 55, 70);
    public static readonly Color Text = Color.FromArgb(235, 239, 246);
    public static readonly Color TextMuted = Color.FromArgb(145, 155, 175);
    public static readonly Color Accent = Color.FromArgb(91, 124, 250);
    public static readonly Color Success = Color.FromArgb(42, 190, 130);
    public static readonly Color Warning = Color.FromArgb(245, 174, 65);
    public static readonly Color Danger = Color.FromArgb(239, 83, 105);

    public static Button StyleButton(Button button, Color? background = null)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = background ?? Border;
        button.FlatAppearance.MouseOverBackColor = ControlPaint.Light(background ?? SurfaceRaised, 0.08f);
        button.FlatAppearance.MouseDownBackColor = ControlPaint.Dark(background ?? SurfaceRaised, 0.08f);
        button.BackColor = background ?? SurfaceRaised;
        button.ForeColor = Text;
        button.Font = new Font("Segoe UI", 9, FontStyle.Regular);
        button.Cursor = Cursors.Hand;
        return button;
    }
}
