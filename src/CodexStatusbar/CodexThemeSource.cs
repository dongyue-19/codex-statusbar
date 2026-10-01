using System.Text.RegularExpressions;

namespace CodexStatusbar;

internal enum CodexThemeKind
{
    Unknown = 0,
    Dark = 1,
    Light = 2
}

/// <summary>
/// Reads the theme <b>Codex Desktop itself</b> is using, from the <c>appearanceTheme</c> key in
/// <c>~/.codex/config.toml</c>.
///
/// <para>Why not the Windows theme: Codex keeps its own appearance setting, and it is routinely the
/// opposite of the system one. On the machine this was built on, Windows reports
/// <c>AppsUseLightTheme = 0</c> (dark) while Codex is set to <c>light</c>, so a Windows-only
/// <c>auto</c> would paint white glyphs onto Codex's light chrome.</para>
///
/// <para>Read-only and deliberately narrow: exactly one key is parsed, only from the top-level table,
/// and nothing else in the file is read, retained, or logged — that file also holds credentials.</para>
/// </summary>
internal sealed class CodexThemeSource
{
    private static readonly Regex AppearancePattern = new(
        "^\\s*appearanceTheme\\s*=\\s*[\"']?(?<value>[A-Za-z_\\-]+)[\"']?\\s*(?:#.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private readonly string _configPath;
    private readonly Func<DateTime> _clock;
    private CodexThemeKind _cached = CodexThemeKind.Unknown;
    private DateTime _readAtUtc = DateTime.MinValue;

    public CodexThemeSource(string? configPath = null, Func<DateTime>? clock = null)
    {
        _configPath = configPath ?? DefaultConfigPath;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public static string DefaultConfigPath
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(root))
            {
                root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".codex");
            }

            return Path.Combine(root, "config.toml");
        }
    }

    /// <summary>The parsed kind, re-read at most every <see cref="CacheDuration"/>.</summary>
    public CodexThemeKind Current
    {
        get
        {
            var now = _clock();
            if (now - _readAtUtc < CacheDuration)
            {
                return _cached;
            }

            _readAtUtc = now;
            _cached = Read(_configPath);
            return _cached;
        }
    }

    public static CodexThemeKind Read(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
            {
                return CodexThemeKind.Unknown;
            }

            return Parse(File.ReadAllLines(configPath, System.Text.Encoding.UTF8));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return CodexThemeKind.Unknown;
        }
    }

    internal static CodexThemeKind Parse(IEnumerable<string> lines)
    {
        // TOML keys belong to the table they follow, and a key can appear anywhere in the file — after
        // a dozen [sections]. So the current table is tracked rather than "stop at the first [".
        //
        // The key is desktop-scoped in practice: on this machine it sits under [desktop], not at the
        // root. Both are accepted; anything deeper is a sub-theme and must not be mistaken for the
        // application-wide setting.
        var table = string.Empty;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('['))
            {
                table = trimmed.Trim('[', ']', ' ', '\t').ToLowerInvariant();
                continue;
            }

            if (!IsApplicationScope(table))
            {
                continue;
            }

            var match = AppearancePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            return match.Groups["value"].Value.ToLowerInvariant() switch
            {
                "light" => CodexThemeKind.Light,
                "dark" => CodexThemeKind.Dark,
                _ => CodexThemeKind.Unknown
            };
        }

        return CodexThemeKind.Unknown;
    }

    /// <summary>Root table and <c>[desktop]</c>; not <c>[desktop.appearance…]</c> or deeper.</summary>
    private static bool IsApplicationScope(string table) =>
        table.Length == 0
        || table.Equals("desktop", StringComparison.Ordinal)
        || table.Equals("desktop.appearance", StringComparison.Ordinal);
}