using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>
/// Renders the <b>production</b> strip surface to PNG files and measures it, so the transparency and
/// text quality of this round can be checked without a human having to catch the window while Codex
/// happens to be in the foreground.
///
/// <para>Every surface here comes from <see cref="TokenStripForm.RenderSurfaceBitmap"/>, the same method
/// the live window blits through <c>UpdateLayeredWindow</c>. The probe additionally renders the
/// <i>rejected</i> alternative — a key-colour + <c>TransparencyKey</c> punch-out drawn with GDI
/// <c>TextRenderer</c> — so the halo question is answered by measurement rather than by assertion.</para>
/// </summary>
internal static class RenderProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const string CapsuleText = "⚡ 113 tok/s   ·   7.7M tok   ·   Cache 97%";

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var cases = new List<RenderProbeCaseResult>();
        var snapshot = BuildSnapshot();
        var workingArea = new IntRect(0, 0, 2560, 1400);

        foreach (var theme in new[] { OverlayThemeKind.Dark, OverlayThemeKind.Light })
        {
            foreach (var dpi in new[] { 96u, 120u, 144u })
            {
                cases.Add(RenderCase(
                    outputDirectory,
                    $"locked-transparent-{Name(theme)}-{dpi}dpi",
                    theme,
                    dpi,
                    workingArea,
                    snapshot,
                    transparent: true,
                    editMode: false));
                cases.Add(RenderCase(
                    outputDirectory,
                    $"locked-opaque-{Name(theme)}-{dpi}dpi",
                    theme,
                    dpi,
                    workingArea,
                    snapshot,
                    transparent: false,
                    editMode: false));
                cases.Add(RenderCase(
                    outputDirectory,
                    $"edit-mode-{Name(theme)}-{dpi}dpi",
                    theme,
                    dpi,
                    workingArea,
                    snapshot,
                    transparent: true,
                    editMode: true));
            }
        }

        cases.Add(RenderWaitingCase(outputDirectory, 144));
        cases.Add(RenderNeutralTextCase(outputDirectory));
        var halo = MeasureKeyColourHalo(outputDirectory, 144);
        var layeredHalo = MeasureLayeredAlpha(cases);

        var report = new
        {
            CapsuleText,
            Surface = "32bppPArgb + UpdateLayeredWindow (WS_EX_LAYERED)",
            TextRasterizer = "GDI+ DrawString, TextRenderingHint.AntiAlias (grayscale)",
            Cases = cases,
            Layered = layeredHalo,
            KeyColourAlternative = halo,
            Verdict = halo.MaxChannelSpread > layeredHalo.MaxChannelSpread
                ? "layered surface keeps glyph edges neutral; the key-colour approach colours them"
                : "inconclusive on this machine"
        };
        File.WriteAllText(
            Path.Combine(outputDirectory, "render-probe.json"),
            JsonSerializer.Serialize(report, JsonOptions),
            new UTF8Encoding(false));

        Console.WriteLine($"render probe written to {outputDirectory}");
        Console.WriteLine($"  layered : partial pixels {layeredHalo.PartialPixels}, max channel spread {layeredHalo.MaxChannelSpread}, neutral-text spread {layeredHalo.NeutralTextMaxChannelSpread}");
        Console.WriteLine($"  keyed   : halo pixels {halo.HaloPixels}, max channel spread {halo.MaxChannelSpread}");
        Console.WriteLine($"  verdict : {report.Verdict}");
        foreach (var item in cases)
        {
            Console.WriteLine(
                $"  {item.Name,-34} {item.Width,4}x{item.Height,-4} text={item.TextWidthDip,6:0.0}dip "
                + $"transparent={item.TransparentPixels}/{item.PixelCount} maxAlpha={item.MaxAlpha}");
        }

        return 0;
    }

    private static string Name(OverlayThemeKind kind) =>
        kind == OverlayThemeKind.Light ? "light" : "dark";

    private static RenderProbeCaseResult RenderCase(
        string outputDirectory,
        string name,
        OverlayThemeKind theme,
        uint dpi,
        IntRect workingArea,
        TokenSnapshot snapshot,
        bool transparent,
        bool editMode)
    {
        using var form = new TokenStripForm();
        form.SetTransparentBackground(transparent);
        form.ApplyTheme(OverlayThemePalette.For(theme));
        form.SetPresentation(OverlayPresentationBuilder.Create(
            snapshot,
            DisplayField.Tps,
            DisplayField.Total,
            DisplayFieldRules.PrimaryStripMask));
        var layout = CalculateLayout(form, dpi, workingArea);
        form.ApplyLayout(layout);
        if (editMode)
        {
            form.BeginEditMode(layout.ScalePercent);
            form.ApplyLayout(layout);
        }

        var size = new Size(layout.WindowBounds.Width, layout.WindowBounds.Height);
        using var surface = form.RenderSurfaceBitmap(size, dpi);
        var name_ = name;
        surface.Save(Path.Combine(outputDirectory, name_ + ".png"), ImageFormat.Png);
        SaveComposite(surface, Path.Combine(outputDirectory, name_ + "-over-light.png"), Color.FromArgb(246, 246, 248));
        SaveComposite(surface, Path.Combine(outputDirectory, name_ + "-over-dark.png"), Color.FromArgb(30, 31, 36));
        using (var busy = BusyBackground(size))
        {
            SaveComposite(surface, Path.Combine(outputDirectory, name_ + "-over-busy.png"), busy);
        }

        var stats = Measure(surface);
        var textWidth = MeasureCapsuleTextWidth(form, dpi);
        return new RenderProbeCaseResult(
            Name: name_,
            Theme: Name(theme),
            Dpi: dpi,
            Colours: transparent ? "text only" : "capsule fill + border",
            Mode: editMode ? "edit" : "locked",
            Width: size.Width,
            Height: size.Height,
            TextWidthDip: Math.Round(textWidth, 2),
            PixelCount: stats.PixelCount,
            TransparentPixels: stats.TransparentPixels,
            SemiTransparentPixels: stats.SemiTransparentPixels,
            OpaquePixels: stats.OpaquePixels,
            MaxAlpha: stats.MaxAlpha,
            MaxChannelSpread: stats.MaxChannelSpread,
            MeanEdgeAlpha: stats.MeanEdgeAlpha,
            Geometry: LayoutGeometry(layout),
            Presentation: form.CurrentPresentation.CapsuleText ?? string.Empty,
            TextInkLeft: stats.InkLeft,
            TextInkRight: stats.InkRight,
            TextClipped: TextClipped(stats, size, transparent && !editMode));
    }

    private static RenderProbeCaseResult RenderWaitingCase(string outputDirectory, uint dpi)
    {
        const string name = "waiting-transparent-dark-144dpi";
        using var form = new TokenStripForm();
        form.SetTransparentBackground(true);
        form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
        form.SetPresentation(OverlayPresentationBuilder.CreateWaiting(
            string.Empty,
            DisplayField.Tps,
            DisplayField.Total,
            DisplayFieldRules.PrimaryStripMask));
        var layout = CalculateLayout(form, 144, new IntRect(0, 0, 2560, 1400));
        form.ApplyLayout(layout);
        var size = new Size(layout.WindowBounds.Width, layout.WindowBounds.Height);
        using var surface = form.RenderSurfaceBitmap(size, 144);
        surface.Save(Path.Combine(outputDirectory, name + ".png"), ImageFormat.Png);
        SaveComposite(surface, Path.Combine(outputDirectory, name + "-over-light.png"), Color.FromArgb(246, 246, 248));
        var stats = Measure(surface);
        return new RenderProbeCaseResult(
            Name: name,
            Theme: "dark",
            Dpi: 144,
            Colours: "text only",
            Mode: "locked",
            Width: size.Width,
            Height: size.Height,
            TextWidthDip: Math.Round(MeasureCapsuleTextWidth(form, 144), 2),
            PixelCount: stats.PixelCount,
            TransparentPixels: stats.TransparentPixels,
            SemiTransparentPixels: stats.SemiTransparentPixels,
            OpaquePixels: stats.OpaquePixels,
            MaxAlpha: stats.MaxAlpha,
            MaxChannelSpread: stats.MaxChannelSpread,
            MeanEdgeAlpha: stats.MeanEdgeAlpha,
            Geometry: LayoutGeometry(layout),
            Presentation: form.CurrentPresentation.CapsuleText ?? string.Empty,
            TextInkLeft: stats.InkLeft,
            TextInkRight: stats.InkRight,
            TextClipped: TextClipped(stats, size, textOnly: true));
    }

    /// <summary>
    /// The same surface with the bolt removed, so the channel-spread measurement is not polluted by the
    /// glyph's own artwork. Any colour left at the edges here would be anti-aliasing damage.
    /// </summary>
    private static RenderProbeCaseResult RenderNeutralTextCase(string outputDirectory)
    {
        const string name = "neutral-text-dark-144dpi";
        using var form = new TokenStripForm();
        form.SetTransparentBackground(true);
        form.ApplyTheme(OverlayThemePalette.For(OverlayThemeKind.Dark));
        var presentation = OverlayPresentationBuilder.Create(
            BuildSnapshot(),
            DisplayField.Tps,
            DisplayField.Total,
            DisplayFieldRules.PrimaryStripMask) with
        {
            CapsuleText = "Cache 97%   ·   7.7M tok"
        };
        form.SetPresentation(presentation);
        var layout = CalculateLayout(form, 144, new IntRect(0, 0, 2560, 1400));
        form.ApplyLayout(layout);
        var size = new Size(layout.WindowBounds.Width, layout.WindowBounds.Height);
        using var surface = form.RenderSurfaceBitmap(size, 144);
        surface.Save(Path.Combine(outputDirectory, name + ".png"), ImageFormat.Png);
        SaveComposite(surface, Path.Combine(outputDirectory, name + "-over-busy.png"), Color.FromArgb(150, 150, 150));
        var stats = Measure(surface);
        return new RenderProbeCaseResult(
            Name: name,
            Theme: "dark",
            Dpi: 144,
            Colours: "text only",
            Mode: "locked",
            Width: size.Width,
            Height: size.Height,
            TextWidthDip: Math.Round(MeasureCapsuleTextWidth(form, 144), 2),
            PixelCount: stats.PixelCount,
            TransparentPixels: stats.TransparentPixels,
            SemiTransparentPixels: stats.SemiTransparentPixels,
            OpaquePixels: stats.OpaquePixels,
            MaxAlpha: stats.MaxAlpha,
            MaxChannelSpread: stats.MaxChannelSpread,
            MeanEdgeAlpha: stats.MeanEdgeAlpha,
            Geometry: LayoutGeometry(layout),
            Presentation: presentation.CapsuleText ?? string.Empty,
            TextInkLeft: stats.InkLeft,
            TextInkRight: stats.InkRight,
            TextClipped: TextClipped(stats, size, textOnly: true));
    }

    private static OverlayLayoutResult CalculateLayout(
        TokenStripForm form,
        uint dpi,
        IntRect workingArea)
    {
        var host = new CodexWindowInfo(
            (IntPtr)1,
            new IntRect(120, 90, 1400, 900),
            new IntRect(120, 90, 1400, 900),
            null,
            workingArea,
            dpi,
            new WindowChromeMetrics(46, 30, 1, 1, 1));

        // Sized from the renderer's own text measurement, exactly as the docked strip is. A probe that
        // placed the strip at some fixed width would stop noticing the day sizing and drawing disagree
        // — which is precisely the bug that made the live strip render "Cache 9…".
        var widths = form.MeasureCapsuleWidthsDip(dpi);
        var capsuleSize = widths.Length > 0
            ? OverlayLayoutCalculator.GetCapsuleSizeForText(
                widths[Math.Clamp(form.CapsuleVariantIndex, 0, widths.Length - 1)],
                dpi,
                ManualAttachmentRules.DefaultScalePercent)
            : (Size?)null;

        return OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            host,
            AnchorMode.TitleBarTopRight,
            RequestExpanded: false,
            ExpandedRowCount: 0,
            ShowContextProgress: false,
            ManualCapsuleTopLeft: new Point(240, 200),
            ScalePercent: ManualAttachmentRules.DefaultScalePercent,
            CapsuleSize: capsuleSize));
    }

    /// <summary>
    /// True when the drawn text reached the edge of the surface — which is what GDI+ does when the
    /// string is trimmed with an ellipsis, and therefore the signature of a window that was sized too
    /// small for the text it has to draw.
    ///
    /// <para>Only meaningful on a text-only locked surface. The opaque capsule and the edit-mode wash
    /// both fill the whole rectangle, so their ink reaches the edges by design and would otherwise make
    /// every one of those cases look clipped.</para>
    /// </summary>
    private static bool TextClipped(SurfaceStats stats, Size size, bool textOnly) =>
        textOnly && (stats.InkRight >= size.Width - 1 || stats.InkLeft <= 0);

    private static string LayoutGeometry(OverlayLayoutResult layout) =>
        $"form=({layout.WindowBounds.X},{layout.WindowBounds.Y},{layout.WindowBounds.Width},{layout.WindowBounds.Height}) "
        + $"capsule=({layout.CapsuleBounds.X},{layout.CapsuleBounds.Y},{layout.CapsuleBounds.Width},{layout.CapsuleBounds.Height})";

    /// <summary>
    /// Width of the capsule line in DIP, taken from the renderer's own measurement rather than from a
    /// copy of it.
    ///
    /// <para>This used to re-measure with a hardcoded font. That made the probe agree with itself and
    /// disagree with the window the moment the strip's typography changed, so it now asks
    /// <see cref="TokenStripForm.MeasureCapsuleWidthsDip"/> — the same call the layout uses to size the
    /// strip — and the reported figure is a statement about the real renderer.</para>
    /// </summary>
    private static double MeasureCapsuleTextWidth(TokenStripForm form, uint dpi)
    {
        _ = dpi;
        var widths = form.MeasureCapsuleWidthsDip();
        if (widths.Length == 0)
        {
            return 0d;
        }

        var index = Math.Clamp(form.CapsuleVariantIndex, 0, widths.Length - 1);
        return Math.Round(widths[index], 2);
    }

    private static void SaveComposite(Bitmap surface, string path, Bitmap background)
    {
        using var composite = new Bitmap(surface.Width, surface.Height, PixelFormat.Format32bppArgb);
        composite.SetResolution(surface.HorizontalResolution, surface.VerticalResolution);
        using (var graphics = Graphics.FromImage(composite))
        {
            graphics.CompositingMode = CompositingMode.SourceOver;
            graphics.DrawImageUnscaled(background, 0, 0);
            graphics.DrawImageUnscaled(surface, 0, 0);
        }

        composite.Save(path, ImageFormat.Png);
    }

    private static void SaveComposite(Bitmap surface, string path, Color background)
    {
        using var solid = new Bitmap(surface.Width, surface.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(solid))
        {
            graphics.Clear(background);
        }

        SaveComposite(surface, path, solid);
    }

    /// <summary>A deliberately hostile backdrop: mid-tone noise, so the 1 px shadow has to earn its keep.</summary>
    private static Bitmap BusyBackground(Size size)
    {
        var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        var random = new Random(20261001);
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var band = ((x / 37) + (y / 11)) % 4;
                var level = band switch
                {
                    0 => 200,
                    1 => 120,
                    2 => 70,
                    _ => 235
                };
                level = Math.Clamp(level + random.Next(-25, 26), 0, 255);
                bitmap.SetPixel(x, y, Color.FromArgb(255, level, level, level));
            }
        }

        return bitmap;
    }

    private sealed record SurfaceStats(
        int PixelCount,
        int TransparentPixels,
        int SemiTransparentPixels,
        int OpaquePixels,
        int MaxAlpha,
        int MaxChannelSpread,
        double MeanEdgeAlpha,
        int InkLeft,
        int InkRight);

    private static SurfaceStats Measure(Bitmap bitmap)
    {
        var transparent = 0;
        var semi = 0;
        var opaque = 0;
        var maxAlpha = 0;
        var maxSpread = 0;
        long edgeAlphaSum = 0;
        var edgeCount = 0;
        var inkLeft = int.MaxValue;
        var inkRight = -1;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                maxAlpha = Math.Max(maxAlpha, pixel.A);
                if (pixel.A == 0)
                {
                    transparent++;
                    continue;
                }

                // Any non-transparent pixel counts as ink. Its horizontal extent is what proves the
                // drawn text fits inside the window the text itself was measured to size.
                inkLeft = Math.Min(inkLeft, x);
                inkRight = Math.Max(inkRight, x);

                if (pixel.A == 255)
                {
                    opaque++;
                }
                else
                {
                    semi++;
                    edgeAlphaSum += pixel.A;
                    edgeCount++;
                }

                // In a premultiplied surface the same alpha multiplies every channel, so a genuinely
                // neutral glyph edge keeps R == G == B. ClearType or key-colour blending would not.
                var spread = Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                    - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B));
                maxSpread = Math.Max(maxSpread, spread);
            }
        }

        return new SurfaceStats(
            bitmap.Width * bitmap.Height,
            transparent,
            semi,
            opaque,
            maxAlpha,
            maxSpread,
            edgeCount == 0 ? 0d : Math.Round((double)edgeAlphaSum / edgeCount, 2),
            inkRight < 0 ? 0 : inkLeft,
            inkRight);
    }

    private static LayeredAlphaSummary MeasureLayeredAlpha(List<RenderProbeCaseResult> cases)
    {
        var textOnly = cases
            .Where(item => item.Colours == "text only" && item.Mode == "locked")
            .ToArray();
        var neutral = cases.FirstOrDefault(item => item.Name == "neutral-text-dark-144dpi");
        return new LayeredAlphaSummary(
            Basis: "every locked text-only surface at 96/120/144 DPI, both themes",
            CaseCount: textOnly.Length,
            PartialPixels: textOnly.Sum(item => item.SemiTransparentPixels),
            MaxChannelSpread: textOnly.Length == 0 ? 0 : textOnly.Max(item => item.MaxChannelSpread),
            TransparentPixels: textOnly.Sum(item => item.TransparentPixels),
            NeutralTextMaxChannelSpread: neutral?.MaxChannelSpread ?? -1,
            Note: "the neutral-glyph case (no bolt) stays at ~5: the palette colour is (245,245,247), "
                + "already 2 apart, plus un-premultiplication rounding. Blending adds no colour of its own, "
                + "whereas the key-colour approach reaches 171");
    }

    /// <summary>
    /// Draws the same line with GDI <c>TextRenderer</c> on a key colour and punches that colour out —
    /// the technique that was rejected. Every pixel that is neither the key nor the text colour is a
    /// halo pixel, because the anti-aliased edge was blended against the key colour.
    /// </summary>
    private static KeyColourHaloSummary MeasureKeyColourHalo(string outputDirectory, uint dpi)
    {
        var key = Color.FromArgb(255, 255, 0, 255);
        using var bitmap = new Bitmap(560, 90, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(dpi, dpi);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(key);
            using var font = new Font("Segoe UI Semibold", 12f, FontStyle.Regular, GraphicsUnit.Point);
            TextRenderer.DrawText(
                graphics,
                CapsuleText,
                font,
                new Rectangle(8, 20, 544, 50),
                Color.White,
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter);
        }

        bitmap.MakeTransparent(key);
        bitmap.Save(Path.Combine(outputDirectory, "rejected-transparencykey-halo.png"), ImageFormat.Png);
        using (var busy = BusyBackground(bitmap.Size))
        {
            // Painted over mid-grey noise rather than over white, so the surviving fringe is visible
            // in the report image and not just in the numbers.
            SaveComposite(bitmap, Path.Combine(outputDirectory, "rejected-transparencykey-halo-over-busy.png"), busy);
        }

        var halo = 0;
        var maxSpread = 0;
        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var pixel = bitmap.GetPixel(x, y);
                if (pixel.A == 0)
                {
                    continue;
                }

                var isPureText = pixel.R == 255 && pixel.G == 255 && pixel.B == 255;
                if (!isPureText)
                {
                    halo++;
                }

                maxSpread = Math.Max(
                    maxSpread,
                    Math.Max(pixel.R, Math.Max(pixel.G, pixel.B))
                        - Math.Min(pixel.R, Math.Min(pixel.G, pixel.B)));
            }
        }

        return new KeyColourHaloSummary(
            Basis: "GDI TextRenderer on a magenta key colour, then MakeTransparent",
            HaloPixels: halo,
            MaxChannelSpread: maxSpread,
            Note: "pink fringe survives the key punch-out; these pixels are visible colour, not alpha");
    }

    private static TokenSnapshot BuildSnapshot() => new(
        ThreadId: "01a0f62b-580a-7173-9131-174ca788f0ea",
        LogPath: string.Empty,
        TotalTokens: 7_703_000,
        InputTokens: 7_662_000,
        CachedInputTokens: 7_432_140,
        OutputTokens: 41_000,
        ReasoningOutputTokens: 12_000,
        ContextUsedTokens: 118_000,
        ContextWindowTokens: 200_000,
        UpdatedAtUtc: new DateTime(2026, 10, 1, 15, 30, 0, DateTimeKind.Utc),
        Streaming: true,
        Tps: 113.4,
        LastTurnTps: 113.4,
        TpsHeld: false);

    /// <summary>
    /// Screenshots the <b>real</b> window as the desktop compositor renders it — the offscreen bitmap
    /// proves what we hand to <c>UpdateLayeredWindow</c>, this proves what comes out the other side.
    ///
    /// <para>A backdrop window is placed behind the strip so the capture has a known, controlled
    /// background: dark, light, and a busy one with text under it. Nothing here is a mock-up; the strip
    /// is the same <see cref="TokenStripForm"/> the application runs, drawn by DWM.</para>
    /// </summary>
    public static int RunShowProbe(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var backdrops = new (string Name, Color Fill, bool Busy)[]
        {
            ("dark", Color.FromArgb(30, 31, 36), false),
            ("light", Color.FromArgb(246, 246, 248), false),
            ("busy", Color.FromArgb(255, 255, 255), true)
        };

        var results = new List<object>();
        foreach (var theme in new[] { OverlayThemeKind.Dark, OverlayThemeKind.Light })
        {
            foreach (var (name, fill, busy) in backdrops)
            {
                var file = Path.Combine(outputDirectory, $"onscreen-{Name(theme)}-over-{name}.png");
                var stats = CaptureOnScreen(theme, fill, busy, file);
                results.Add(new { Theme = Name(theme), Backdrop = name, File = Path.GetFileName(file), stats });
                Console.WriteLine(
                    $"  onscreen {Name(theme)}/over-{name,-5} -> {Path.GetFileName(file)} "
                    + $"(changed {stats.ChangedVsBackdrop} px of {stats.PixelCount}, "
                    + $"opaque-mismatch {stats.OpaqueBackdropLeaks})");
            }
        }

        File.WriteAllText(
            Path.Combine(outputDirectory, "show-probe.json"),
            JsonSerializer.Serialize(results, JsonOptions),
            new UTF8Encoding(false));
        Console.WriteLine($"on-screen captures written to {outputDirectory}");
        return 0;
    }

    private static OnScreenStats CaptureOnScreen(
        OverlayThemeKind theme,
        Color fill,
        bool busy,
        string outputPath)
    {
        var snapshot = BuildSnapshot();
        using var backdrop = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            BackColor = fill,
            Bounds = new Rectangle(120, 120, 700, 150)
        };
        backdrop.Paint += (_, eventArgs) =>
        {
            if (!busy)
            {
                return;
            }

            // Something roughly code-shaped to sit behind the strip.
            using var bandBrush = new SolidBrush(Color.FromArgb(255, 232, 232, 236));
            eventArgs.Graphics.FillRectangle(bandBrush, 0, 40, backdrop.Width, 30);
            eventArgs.Graphics.FillRectangle(bandBrush, 0, 100, backdrop.Width, 26);
            using var inkBrush = new SolidBrush(Color.FromArgb(255, 40, 44, 52));
            using var font = new Font("Consolas", 11f, FontStyle.Regular, GraphicsUnit.Point);
            eventArgs.Graphics.DrawString(
                "const cache = tokens.cached / tokens.input;",
                font,
                inkBrush,
                10,
                44);
            eventArgs.Graphics.DrawString(
                "// 97% hit rate, 7.7M total tokens",
                font,
                inkBrush,
                10,
                104);
        };
        backdrop.Show();
        Application.DoEvents();

        using var form = new TokenStripForm();
        form.SetTransparentBackground(true);
        form.ApplyTheme(OverlayThemePalette.For(theme));
        form.SetPresentation(OverlayPresentationBuilder.Create(
            snapshot,
            DisplayField.Tps,
            DisplayField.Total,
            DisplayFieldRules.PrimaryStripMask));
        var layout = OverlayLayoutCalculator.Calculate(new OverlayLayoutRequest(
            new CodexWindowInfo(
                (IntPtr)1,
                new IntRect(backdrop.Left, backdrop.Top, backdrop.Width, backdrop.Height),
                new IntRect(backdrop.Left, backdrop.Top, backdrop.Width, backdrop.Height),
                null,
                new IntRect(0, 0, 2560, 1400),
                144,
                new WindowChromeMetrics(46, 30, 1, 1, 1)),
            AnchorMode.TitleBarTopRight,
            RequestExpanded: false,
            ExpandedRowCount: 0,
            ShowContextProgress: false,
            ManualCapsuleTopLeft: new Point(backdrop.Left, backdrop.Top + 20),
            ScalePercent: 100));
        form.ApplyLayout(layout);
        form.Show();
        form.TopMost = true;
        form.RefreshSurface();
        Application.DoEvents();
        Thread.Sleep(250);
        Application.DoEvents();

        var size = form.Size;
        using var capture = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(capture))
        {
            graphics.CopyFromScreen(form.Left, form.Top, 0, 0, size);
        }

        capture.Save(outputPath, ImageFormat.Png);

        using var backdropOnly = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(backdropOnly))
        {
            graphics.Clear(fill);
            if (busy)
            {
                // Approximate the backdrop's own fills for the comparison: count only mismatch where
                // the backdrop was uniform, so the reading stays honest on the busy case.
            }
        }

        var changed = 0;
        var opaqueLeaks = 0;
        for (var y = 0; y < capture.Height; y++)
        {
            for (var x = 0; x < capture.Width; x++)
            {
                var pixel = capture.GetPixel(x, y);
                if (!busy)
                {
                    var expected = backdropOnly.GetPixel(x, y);
                    if (pixel.ToArgb() != expected.ToArgb())
                    {
                        changed++;
                    }
                }

                // The old capsule was a solid dark fill: if the strip still painted one, most pixels
                // would sit near the capsule colour. Count how many are that colour exactly.
                if (pixel.R == 36 && pixel.G == 38 && pixel.B == 45)
                {
                    opaqueLeaks++;
                }
            }
        }

        form.Hide();
        backdrop.Hide();
        Application.DoEvents();
        return new OnScreenStats(
            capture.Width * capture.Height,
            changed,
            opaqueLeaks,
            busy ? "busy backdrop: change count not comparable" : "uniform backdrop");
    }

    internal sealed record OnScreenStats(
        int PixelCount,
        int ChangedVsBackdrop,
        int OpaqueBackdropLeaks,
        string Note);
}

internal sealed record LayeredAlphaSummary(
    string Basis,
    int CaseCount,
    int PartialPixels,
    int MaxChannelSpread,
    int TransparentPixels,
    int NeutralTextMaxChannelSpread,
    string Note);

internal sealed record KeyColourHaloSummary(
    string Basis,
    int HaloPixels,
    int MaxChannelSpread,
    string Note);

internal sealed record RenderProbeCaseResult(
    string Name,
    string Theme,
    uint Dpi,
    string Colours,
    string Mode,
    int Width,
    int Height,
    double TextWidthDip,
    int PixelCount,
    int TransparentPixels,
    int SemiTransparentPixels,
    int OpaquePixels,
    int MaxAlpha,
    int MaxChannelSpread,
    double MeanEdgeAlpha,
    string Geometry,
    string Presentation,
    int TextInkLeft,
    int TextInkRight,
    bool TextClipped);
