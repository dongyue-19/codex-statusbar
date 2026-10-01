using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

internal enum AnchorMode
{
    Auto = 0,
    InsideTopRight = 1,
    InsideBottomRight = 2,
    TitleBarTopRight = 3
}

[Flags]
internal enum DisplayField
{
    None = 0,
    Total = 1 << 0,
    Input = 1 << 1,
    Output = 1 << 2,
    CacheHit = 1 << 3,
    CacheMiss = 1 << 4,
    Context = 1 << 5,
    ContextPercent = 1 << 6,
    Reasoning = 1 << 7,
    Thread = 1 << 8,
    CacheHitRate = 1 << 9,

    /// <summary>Model output speed. One of the three primary metrics of the collapsed strip.</summary>
    Tps = 1 << 10
}

internal enum CollapsedSlot { Primary, Secondary }

/// <summary>How the strip's position is derived.</summary>
internal enum OverlayPositionMode
{
    /// <summary>
    /// Docked inside the Codex composer toolbar, immediately left of the official Context-usage
    /// indicator. The reference comes from UI Automation, so the strip follows the control rather
    /// than a remembered coordinate. This is the default.
    /// </summary>
    ComposerContextLeft = 2,

    /// <summary>
    /// Anchor + offset, re-resolved against the Codex window on every move / resize. The manual
    /// mode: the position is whatever the user dragged it to.
    /// </summary>
    FollowCodex = 0,

    /// <summary>Pinned to absolute screen coordinates; Codex movement does not affect it.</summary>
    FixedScreen = 1
}

/// <summary>Where the text colours come from. <see cref="Auto"/> tracks the Windows app theme.</summary>
internal enum OverlayThemePreference
{
    Auto = 0,
    Dark = 1,
    Light = 2
}

internal static class DisplayFieldRules
{
    public const DisplayField SupportedMask =
        DisplayField.Total | DisplayField.Input | DisplayField.Output |
        DisplayField.CacheHit | DisplayField.CacheMiss | DisplayField.Context |
        DisplayField.ContextPercent | DisplayField.Reasoning | DisplayField.Thread |
        DisplayField.CacheHitRate | DisplayField.Tps;

    /// <summary>
    /// The three primary metrics, in the order the collapsed strip shows them:
    /// <c>⚡ 243 tok/s   ·   5.7M tok   ·   Cache 98%</c>
    /// </summary>
    public static readonly DisplayField PrimaryStripMask =
        DisplayField.Tps | DisplayField.Total | DisplayField.CacheHitRate;

    public static readonly IReadOnlyList<DisplayField> Ordered = new[]
    {
        DisplayField.Total, DisplayField.Input, DisplayField.Output,
        DisplayField.CacheHit, DisplayField.CacheHitRate, DisplayField.CacheMiss, DisplayField.Context,
        DisplayField.ContextPercent, DisplayField.Reasoning, DisplayField.Thread, DisplayField.Tps
    };

    public static bool IsSingleSupported(DisplayField field)
    {
        var value = (int)field;
        return value > 0 &&
            (value & (value - 1)) == 0 &&
            (field & SupportedMask) == field;
    }

    public static DisplayField SanitizeVisible(DisplayField fields)
    {
        var sanitized = fields & SupportedMask;
        return sanitized == DisplayField.None ? DisplayField.Total : sanitized;
    }
}

internal sealed record OverlaySettingsLoadResult(
    OverlaySettings Settings,
    bool MustPersist);

internal sealed class OverlaySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,

        // camelCase is the documented, hand-editable schema (`anchor`, `offsetX`, `offsetY`, …).
        // Older files written in PascalCase still load, because matching is case-insensitive.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly string DefaultSettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CodexStatusbar",
        "settings.json");

    private const DisplayField DefaultVisibleFields =
        DisplayField.Tps
        | DisplayField.Total
        | DisplayField.Input
        | DisplayField.Output
        | DisplayField.CacheHit
        | DisplayField.CacheHitRate
        | DisplayField.CacheMiss
        | DisplayField.Context
        | DisplayField.ContextPercent;

    private sealed class PersistedSettings
    {
        public int? SettingsVersion { get; set; }

        // --- v2, flat and human-editable: transparency, anchor + offset, position mode, theme ---
        public bool? TransparentBackground { get; set; }
        public string? Anchor { get; set; }
        public double? OffsetX { get; set; }
        public double? OffsetY { get; set; }
        public string? PositionMode { get; set; }
        public int? FixedScreenX { get; set; }
        public int? FixedScreenY { get; set; }
        public string? Theme { get; set; }

        // --- v3: composer docking ---
        public double? ContextGapDip { get; set; }
        public bool? TextShadow { get; set; }

        // --- v1 layout keys, still written for backward compatibility ---
        public int? AnchorMode { get; set; }
        public int? VisibleFields { get; set; }
        public int? CollapsedPrimaryField { get; set; }
        public int? CollapsedSecondaryField { get; set; }
        public bool? ManualPlacementEnabled { get; set; }
        public PersistedWindowAttachment? MainAttachment { get; set; }
        public int? OverlayScalePercent { get; set; }
    }

    private sealed class PersistedWindowAttachment
    {
        public int? ReferencePoint { get; set; }
        public double? OffsetXDip { get; set; }
        public double? OffsetYDip { get; set; }
    }

    public const int CurrentSettingsVersion = 3;

    /// <summary>
    /// The gap between the strip's right edge and the Context indicator's left edge, in DIP. The
    /// documented target range is 8–12 DIP; 10 is the middle of it.
    /// </summary>
    public const double DefaultContextGapDip = 10d;

    public const double MinimumContextGapDip = 0d;
    public const double MaximumContextGapDip = 48d;

    public int SettingsVersion { get; private set; }
    public AnchorMode AnchorMode { get; set; }
    public DisplayField VisibleFields { get; set; }
    public DisplayField CollapsedPrimaryField { get; private set; }
    public DisplayField CollapsedSecondaryField { get; private set; }
    public bool ManualPlacementEnabled { get; set; }
    public WindowAttachment MainAttachment { get; set; } = ManualAttachmentRules.DefaultMainAttachment;
    public int OverlayScalePercent { get; set; }

    /// <summary>Draw only the text; no capsule fill, no border. On by default.</summary>
    public bool TransparentBackground { get; set; } = true;

    public OverlayPositionMode PositionMode { get; set; } = OverlayPositionMode.ComposerContextLeft;

    public OverlayThemePreference ThemePreference { get; set; } = OverlayThemePreference.Auto;

    /// <summary>Distance from the Context indicator's left edge, in DIP.</summary>
    public double ContextGapDip { get; set; } = DefaultContextGapDip;

    /// <summary>
    /// A faint drop shadow behind the strip's glyphs. Off by default: docked inside the composer the
    /// strip sits on Codex's own background, where the theme match is enough and a shadow would make
    /// it read as an overlay rather than as part of the toolbar.
    /// </summary>
    public bool TextShadow { get; set; }

    /// <summary>Absolute screen position, in physical pixels, used only by <see cref="OverlayPositionMode.FixedScreen"/>.</summary>
    public Point FixedScreenPosition { get; set; }

    public static OverlaySettings CreateDefault()
    {
        return new OverlaySettings
        {
            SettingsVersion = CurrentSettingsVersion,
            AnchorMode = AnchorMode.TitleBarTopRight,
            VisibleFields = DefaultVisibleFields,
            // The collapsed strip carries TPS / total / cache. TPS is the primary slot so the
            // narrow (PrimaryOnly) fallback still shows the headline number.
            CollapsedPrimaryField = DisplayField.Tps,
            CollapsedSecondaryField = DisplayField.Total,
            ManualPlacementEnabled = true,
            MainAttachment = ManualAttachmentRules.DefaultMainAttachment,
            OverlayScalePercent = ManualAttachmentRules.DefaultScalePercent,
            TransparentBackground = true,
            PositionMode = OverlayPositionMode.ComposerContextLeft,
            ThemePreference = OverlayThemePreference.Auto,
            ContextGapDip = DefaultContextGapDip,
            TextShadow = false,
            FixedScreenPosition = Point.Empty
        };
    }

    public static OverlaySettings Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var result = LoadFromFile(path);
        if (result.MustPersist)
        {
            result.Settings.Save(path);
        }

        return result.Settings;
    }

    internal static OverlaySettingsLoadResult LoadFromFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                // Write the documented schema out on first run: the file is meant to be findable and
                // hand-editable, and nothing should have to be changed once before it appears.
                return new OverlaySettingsLoadResult(CreateDefault(), MustPersist: true);
            }

            return ParseJson(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new OverlaySettingsLoadResult(CreateDefault(), false);
        }
    }

    internal static OverlaySettingsLoadResult ParseJson(string json)
    {
        try
        {
            var persisted = JsonSerializer.Deserialize<PersistedSettings>(json, JsonOptions);
            if (persisted is null)
            {
                return new OverlaySettingsLoadResult(CreateDefault(), false);
            }

            var settings = CreateDefault();
            var version = persisted.SettingsVersion ?? 0;

            settings.AnchorMode = SanitizeAnchorMode(persisted.AnchorMode);
            settings.VisibleFields = DisplayFieldRules.SanitizeVisible(
                (DisplayField)(persisted.VisibleFields ?? (int)DefaultVisibleFields));
            settings.SetCollapsedFields(
                (DisplayField)(persisted.CollapsedPrimaryField ?? (int)DisplayField.Tps),
                (DisplayField)(persisted.CollapsedSecondaryField ?? (int)DisplayField.Total));
            settings.ManualPlacementEnabled = persisted.ManualPlacementEnabled ?? true;
            settings.OverlayScalePercent = ManualAttachmentRules.SanitizeScale(
                persisted.OverlayScalePercent);

            // Position. The v2 flat keys are authoritative because that is the documented, hand-editable
            // schema; `mainAttachment` is still read so a v1 file keeps working.
            var legacyAttachment = DeserializeAttachment(persisted.MainAttachment);
            var flatAnchor = TryParseAnchor(persisted.Anchor);
            var flatAttachment = flatAnchor is { } anchor
                && persisted.OffsetX is { } offsetX
                && persisted.OffsetY is { } offsetY
                    ? new WindowAttachment(anchor, offsetX, offsetY)
                    : null;
            var untouchedLegacyDefault = flatAttachment is null
                && legacyAttachment == ManualAttachmentRules.LegacyDefaultMainAttachment;
            settings.MainAttachment = untouchedLegacyDefault
                ? ManualAttachmentRules.DefaultMainAttachment
                : ManualAttachmentRules.SanitizeMain(flatAttachment ?? legacyAttachment);

            settings.TransparentBackground = persisted.TransparentBackground ?? true;
            settings.ThemePreference = ParseThemePreference(persisted.Theme);
            settings.ContextGapDip = SanitizeContextGap(persisted.ContextGapDip);
            settings.TextShadow = persisted.TextShadow ?? false;
            settings.FixedScreenPosition = new Point(
                persisted.FixedScreenX ?? 0,
                persisted.FixedScreenY ?? 0);

            // Migration. A v2 file always wrote `positionMode`, and v2's default was FollowCodex, so
            // an explicit choice of FollowCodex is indistinguishable from never having touched it.
            // v3's default is docking, so a v2 file still on the old default is upgraded; an
            // explicitly chosen FixedScreen is preserved. The saved anchor + offset are kept either
            // way, so selecting the manual mode later restores exactly the previous position.
            var parsedMode = ParsePositionMode(persisted.PositionMode);
            settings.PositionMode = version < 3 && parsedMode == OverlayPositionMode.FollowCodex
                ? OverlayPositionMode.ComposerContextLeft
                : parsedMode;

            // Anything older than the current version gets rewritten once so the file on disk always
            // shows the current schema (including the new default anchor, which must not be
            // re-derived every launch).
            return new OverlaySettingsLoadResult(settings, version < CurrentSettingsVersion);
        }
        catch (JsonException)
        {
            return new OverlaySettingsLoadResult(CreateDefault(), false);
        }
    }

    internal string Serialize()
    {
        var attachment = ManualAttachmentRules.SanitizeMain(MainAttachment);
        var persisted = new PersistedSettings
        {
            SettingsVersion = CurrentSettingsVersion,

            // v2: the documented, hand-editable view of the position model.
            TransparentBackground = TransparentBackground,
            Anchor = AnchorName(attachment.ReferencePoint),
            OffsetX = attachment.OffsetXDip,
            OffsetY = attachment.OffsetYDip,
            PositionMode = PositionModeName(PositionMode),
            FixedScreenX = FixedScreenPosition.X,
            FixedScreenY = FixedScreenPosition.Y,
            Theme = ThemePreferenceName(ThemePreference),

            // v3: composer docking.
            ContextGapDip = SanitizeContextGap(ContextGapDip),
            TextShadow = TextShadow,

            // v1 keys, kept so an older build can still read this file.
            AnchorMode = (int)SanitizeAnchorMode((int)AnchorMode),
            VisibleFields = (int)DisplayFieldRules.SanitizeVisible(VisibleFields),
            CollapsedPrimaryField = (int)CollapsedPrimaryField,
            CollapsedSecondaryField = (int)CollapsedSecondaryField,
            ManualPlacementEnabled = ManualPlacementEnabled,
            MainAttachment = SerializeAttachment(attachment),
            OverlayScalePercent = ManualAttachmentRules.SanitizeScale(OverlayScalePercent)
        };
        return JsonSerializer.Serialize(persisted, JsonOptions);
    }

    /// <summary>The anchor's name as written to settings.json, e.g. <c>TopCenter</c>.</summary>
    public static string AnchorName(AttachmentReferencePoint anchor) => anchor.ToString();

    public static AttachmentReferencePoint? TryParseAnchor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Enum.TryParse<AttachmentReferencePoint>(value, ignoreCase: true, out var anchor)
            || !Enum.IsDefined(anchor))
        {
            return null;
        }

        return anchor;
    }

    public static string PositionModeName(OverlayPositionMode mode) => mode switch
    {
        OverlayPositionMode.ComposerContextLeft => "ComposerContextLeft",
        OverlayPositionMode.FixedScreen => "FixedScreen",
        _ => "FollowCodex"
    };

    public static double SanitizeContextGap(double? value) =>
        double.IsNaN(value ?? DefaultContextGapDip) || double.IsInfinity(value ?? DefaultContextGapDip)
            ? DefaultContextGapDip
            : Math.Clamp(value ?? DefaultContextGapDip, MinimumContextGapDip, MaximumContextGapDip);

    public static string ThemePreferenceName(OverlayThemePreference preference) => preference switch
    {
        OverlayThemePreference.Dark => "Dark",
        OverlayThemePreference.Light => "Light",
        _ => "Auto"
    };

    private static OverlayPositionMode ParsePositionMode(string? value) =>
        Enum.TryParse<OverlayPositionMode>(value, ignoreCase: true, out var mode)
            && Enum.IsDefined(mode)
                ? mode
                : OverlayPositionMode.ComposerContextLeft;

    private static OverlayThemePreference ParseThemePreference(string? value) =>
        Enum.TryParse<OverlayThemePreference>(value, ignoreCase: true, out var preference)
            && Enum.IsDefined(preference)
                ? preference
                : OverlayThemePreference.Auto;

    public bool TrySave(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, Serialize(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Save(string? settingsPath = null) => TrySave(settingsPath);

    public bool SelectCollapsedField(CollapsedSlot slot, DisplayField field)
    {
        if (!DisplayFieldRules.IsSingleSupported(field))
        {
            return false;
        }

        switch (slot)
        {
            case CollapsedSlot.Primary:
                if (CollapsedPrimaryField == field)
                {
                    return false;
                }

                if (CollapsedSecondaryField == field)
                {
                    (CollapsedPrimaryField, CollapsedSecondaryField) =
                        (CollapsedSecondaryField, CollapsedPrimaryField);
                    return true;
                }

                CollapsedPrimaryField = field;
                return true;

            case CollapsedSlot.Secondary:
                if (CollapsedSecondaryField == field)
                {
                    return false;
                }

                if (CollapsedPrimaryField == field)
                {
                    (CollapsedPrimaryField, CollapsedSecondaryField) =
                        (CollapsedSecondaryField, CollapsedPrimaryField);
                    return true;
                }

                CollapsedSecondaryField = field;
                return true;

            default:
                return false;
        }
    }

    public static string? ResolveSettingsOverride(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count; index++)
        {
            if (!args[index].Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
            {
                throw new ArgumentException("--settings 后必须提供绝对设置文件路径。", nameof(args));
            }

            var candidate = args[index + 1];
            if (!Path.IsPathFullyQualified(candidate))
            {
                throw new ArgumentException("--settings 只接受绝对设置文件路径。", nameof(args));
            }

            return Path.GetFullPath(candidate);
        }

        return null;
    }

    private static AnchorMode SanitizeAnchorMode(int? value)
    {
        return value switch
        {
            (int)AnchorMode.Auto => AnchorMode.Auto,
            (int)AnchorMode.InsideTopRight => AnchorMode.InsideTopRight,
            (int)AnchorMode.InsideBottomRight => AnchorMode.InsideBottomRight,
            (int)AnchorMode.TitleBarTopRight => AnchorMode.TitleBarTopRight,
            _ => AnchorMode.TitleBarTopRight
        };
    }

    private static WindowAttachment? DeserializeAttachment(PersistedWindowAttachment? value)
    {
        if (value?.ReferencePoint is not int referencePoint
            || value.OffsetXDip is not double offsetXDip
            || value.OffsetYDip is not double offsetYDip)
        {
            return null;
        }

        return ManualAttachmentRules.TrySanitize(
            new WindowAttachment((AttachmentReferencePoint)referencePoint, offsetXDip, offsetYDip),
            out var attachment)
            ? attachment
            : null;
    }

    private static PersistedWindowAttachment? SerializeAttachment(WindowAttachment? value)
    {
        return value is null
            ? null
            : new PersistedWindowAttachment
            {
                ReferencePoint = (int)value.ReferencePoint,
                OffsetXDip = value.OffsetXDip,
                OffsetYDip = value.OffsetYDip
            };
    }

    private void SetCollapsedFields(DisplayField primary, DisplayField secondary)
    {
        CollapsedPrimaryField = DisplayFieldRules.IsSingleSupported(primary)
            ? primary
            : DisplayField.Tps;
        CollapsedSecondaryField = DisplayFieldRules.IsSingleSupported(secondary)
            ? secondary
            : DisplayField.Total;

        if (CollapsedPrimaryField == CollapsedSecondaryField)
        {
            CollapsedSecondaryField = CollapsedPrimaryField == DisplayField.Total
                ? DisplayField.CacheHitRate
                : DisplayField.Total;
        }
    }
}

internal sealed class SettingsProbeRequest
{
    public List<SettingsProbeCase> Cases { get; set; } = new();
}

internal sealed class SettingsProbeCase
{
    public string Name { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string? Json { get; set; }
    public string? Slot { get; set; }
    public int? Field { get; set; }
    public string? SettingsPath { get; set; }
}

internal sealed record SettingsProbeCaseResult(
    string Name,
    OverlaySettings Settings,
    bool MustPersist);

internal sealed record SettingsProbeResult(IReadOnlyList<SettingsProbeCaseResult> Cases);

internal static class SettingsProbe
{
    public static SettingsProbeResult Execute(SettingsProbeRequest request)
    {
        var results = new List<SettingsProbeCaseResult>();
        foreach (var probeCase in request.Cases)
        {
            var result = probeCase.Operation switch
            {
                "Parse" => OverlaySettings.ParseJson(RequireJson(probeCase)),
                "Select" => Select(probeCase),
                "Load" => Load(probeCase),
                "SaveReload" => SaveReload(probeCase),
                _ => throw new ArgumentException($"不支持的设置探针操作：{probeCase.Operation}")
            };
            results.Add(new SettingsProbeCaseResult(probeCase.Name, result.Settings, result.MustPersist));
        }

        return new SettingsProbeResult(results);
    }

    private static OverlaySettingsLoadResult Select(SettingsProbeCase probeCase)
    {
        var result = OverlaySettings.ParseJson(RequireJson(probeCase));
        if (!Enum.TryParse<CollapsedSlot>(probeCase.Slot, ignoreCase: true, out var slot)
            || !probeCase.Field.HasValue)
        {
            throw new ArgumentException("Select 设置探针需要有效的 Slot 和 Field。", nameof(probeCase));
        }

        result.Settings.SelectCollapsedField(slot, (DisplayField)probeCase.Field.Value);
        return result;
    }

    private static OverlaySettingsLoadResult Load(SettingsProbeCase probeCase)
    {
        var path = RequireTemporaryPath(probeCase);
        if (probeCase.Json is not null)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, probeCase.Json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        var result = OverlaySettings.LoadFromFile(path);
        if (result.MustPersist)
        {
            result.Settings.Save(path);
        }

        return result;
    }

    private static OverlaySettingsLoadResult SaveReload(SettingsProbeCase probeCase)
    {
        var path = RequireTemporaryPath(probeCase);
        var parsed = OverlaySettings.ParseJson(RequireJson(probeCase));
        parsed.Settings.Save(path);
        return new OverlaySettingsLoadResult(OverlaySettings.Load(path), false);
    }

    private static string RequireJson(SettingsProbeCase probeCase)
    {
        return probeCase.Json ?? throw new ArgumentException(
            "设置探针操作需要 Json。",
            nameof(probeCase));
    }

    private static string RequireTemporaryPath(SettingsProbeCase probeCase)
    {
        if (string.IsNullOrWhiteSpace(probeCase.SettingsPath)
            || !Path.IsPathFullyQualified(probeCase.SettingsPath))
        {
            throw new ArgumentException(
                "设置探针需要绝对临时设置路径。",
                nameof(probeCase));
        }

        var path = Path.GetFullPath(probeCase.SettingsPath);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!path.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "设置探针只能使用临时设置路径。",
                nameof(probeCase));
        }

        return path;
    }
}
