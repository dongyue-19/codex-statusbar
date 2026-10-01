namespace CodexStatusbar;

internal sealed class AttachmentTargetHighlightForm : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int RingWidthDip = 2;

    private static readonly Color TransparencyColor = Color.Fuchsia;
    private OverlayThemePalette _palette = OverlayThemePalette.For(OverlayThemeKind.Dark);

    public AttachmentTargetHighlightForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = TransparencyColor;
        TransparencyKey = TransparencyColor;
        DoubleBuffered = true;
    }

    internal int SetBoundsCoreCallCount { get; private set; }
    internal OverlayThemePalette CurrentThemePalette => _palette;

    public void ApplyTheme(OverlayThemePalette palette)
    {
        ArgumentNullException.ThrowIfNull(palette);
        if (_palette == palette)
        {
            return;
        }

        _palette = palette;
        Invalidate();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow | WsExNoActivate | WsExTransparent;
            return parameters;
        }
    }

    public void ShowTarget(IntRect bounds)
    {
        if (bounds.IsEmpty)
        {
            ClearTarget();
            return;
        }

        SetBounds(
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            BoundsSpecified.All);
        ReplaceRegion(CreateRingRegion(ClientSize, DeviceDpi));
        if (!Visible)
        {
            Show();
        }
        Invalidate();
    }

    public void ClearTarget()
    {
        if (Visible)
        {
            Hide();
        }
        ReplaceRegion(null);
    }

    protected override void SetBoundsCore(
        int x,
        int y,
        int width,
        int height,
        BoundsSpecified specified)
    {
        SetBoundsCoreCallCount++;
        base.SetBoundsCore(x, y, width, height, specified);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WmNcHitTest)
        {
            message.Result = (IntPtr)HtTransparent;
            return;
        }

        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs eventArgs)
    {
        base.OnPaint(eventArgs);
        eventArgs.Graphics.Clear(_palette.TargetHighlight);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ReplaceRegion(null);
        }
        base.Dispose(disposing);
    }

    private void ReplaceRegion(Region? next)
    {
        var previous = Region;
        Region = next;
        previous?.Dispose();
    }

    private static Region CreateRingRegion(Size size, int dpi)
    {
        var outer = new Rectangle(Point.Empty, size);
        var region = new Region(outer);
        var effectiveDpi = dpi <= 0 ? 96 : dpi;
        var thickness = Math.Max(1, (int)Math.Round(
            RingWidthDip * effectiveDpi / 96d,
            MidpointRounding.AwayFromZero));
        if (size.Width > thickness * 2 && size.Height > thickness * 2)
        {
            region.Exclude(Rectangle.Inflate(outer, -thickness, -thickness));
        }
        return region;
    }
}
