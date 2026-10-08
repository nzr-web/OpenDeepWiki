using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenDeepWiki.Services.Wiki.EnvSecrets;

/// <summary>
/// Deterministic scanner for environment variables and expected secrets in a working copy.
/// Files are parsed with per-line regular expressions only (Helm templates are not valid YAML).
/// Never throws: unreadable files are skipped with a warning, any other error yields
/// an empty result with <see cref="EnvironmentScanResult.Failed"/> set.
/// Secret values are never returned, rendered or logged.
/// </summary>
internal sealed class EnvironmentReferenceScanner
{
    public const int MaxFileBytes = 512 * 1024;
    public const int MaxLocations = 5;
    public const int MaxRenderedChars = 24_000;
    private const int BinaryProbeBytes = 8 * 1024;

    internal const string SourceCSharp = "C#";
    internal const string SourceCSharpConfiguration = "C# configuration";
    internal const string SourcePython = "Python";
    internal const string SourceJavaScript = "JS/TS";
    internal const string SourceGo = "Go";
    internal const string SourceJvm = "Java/Kotlin";
    internal const string SourceSpringConfig = "Spring config";
    internal const string SourceCompose = "compose";
    internal const string SourceDockerfile = "Dockerfile";
    internal const string SourceKubernetes = "Kubernetes/Helm env";
    internal const string SourceKubernetesSecret = "Kubernetes secretKeyRef";
    internal const string SourceCiSecret = "GitHub Actions secret";
    internal const string SourceCiVariable = "GitHub Actions variable";
    internal const string SourceEnvExample = ".env example";

    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", "dist", "build", "out", "target", "vendor",
        ".venv", "venv", "__pycache__", ".next", "coverage"
    };

    private static readonly HashSet<string> ShellVariables = new(StringComparer.Ordinal)
    {
        "PWD", "HOME", "PATH", "HOSTNAME", "USER", "SHELL", "TERM", "LANG"
    };

    private static readonly HashSet<string> SecretTokens = new(StringComparer.Ordinal)
    {
        "KEY", "SECRET", "TOKEN", "PASSWORD", "PASSWD", "PWD", "CREDENTIAL", "CREDENTIALS",
        "PRIVATE", "CERT", "DSN", "APIKEY"
    };

    private static readonly HashSet<string> NonSecretSuffixTokens = new(StringComparer.Ordinal)
    {
        "PATH", "FILE", "URL", "TTL", "ID", "HEADER"
    };

    private const RegexOptions Options = RegexOptions.Compiled | RegexOptions.CultureInvariant;

    private static readonly Regex EnvNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", Options);
    private static readonly Regex DotNetKeyRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*(?::[A-Za-z0-9_]+)+$", Options);
    private static readonly Regex DoubleUnderscore = new(@"(?<=[A-Za-z0-9])__(?=[A-Za-z0-9])", Options);
    private static readonly Regex TokenSeparators = new(@"[_\-:.]+", Options);
    private static readonly Regex CamelBoundary = new(@"(?<=[a-z0-9])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", Options);

    // C#
    private static readonly Regex CsGetEnvironmentVariable = new(@"\bGetEnvironmentVariable\(\s*""([^""]+)""", Options);
    private static readonly Regex CsConfigurationIndexer = new(@"\b_?(?:[cC]onfiguration|[cC]onfig)\s*\[\s*""([^""]+)""\s*\]", Options);
    private static readonly Regex CsGetValue = new(@"\bGetValue<[^<>()]+>\(\s*""([^""]+)""", Options);

    // Python
    private static readonly Regex PyGetenv = new(@"\bos\.getenv\(\s*[""']([^""']+)[""']", Options);
    private static readonly Regex PyEnvironIndexer = new(@"\bos\.environ\[\s*[""']([^""']+)[""']\s*\]", Options);
    private static readonly Regex PyEnvironGet = new(@"\benviron\.get\(\s*[""']([^""']+)[""']", Options);

    // JS/TS
    private static readonly Regex JsProcessEnvMember = new(@"\bprocess\.env\.([A-Za-z_][A-Za-z0-9_]*)", Options);
    private static readonly Regex JsProcessEnvIndexer = new(@"\bprocess\.env\[\s*[""'`]([^""'`]+)[""'`]\s*\]", Options);
    private static readonly Regex JsImportMetaEnv = new(@"\bimport\.meta\.env\.([A-Za-z_][A-Za-z0-9_]*)", Options);

    // Go
    private static readonly Regex GoGetenv = new(@"\bos\.(?:Getenv|LookupEnv)\(\s*""([^""]+)""", Options);

    // Java/Kotlin
    private static readonly Regex JvmGetenv = new(@"\bSystem\.getenv\(\s*""([^""]+)""", Options);
    private static readonly Regex JvmValueAnnotation = new(@"@Value\(\s*""\\?\$\{([^}:""]+)", Options);

    // ${X...} start; the expansion tail is parsed by hand to cope with nesting.
    private static readonly Regex Expansion = new(@"(?<!\$)\$\{([A-Za-z_][A-Za-z0-9_]*)", Options);

    // YAML blocks
    private static readonly Regex EnvironmentBlockStart = new(@"^(\s*)(-\s+)?environment:\s*(#.*)?$", Options);
    private static readonly Regex EnvBlockStart = new(@"^(\s*)(-\s+)?env:\s*(#.*)?$", Options);
    private static readonly Regex ComposeListEntry = new(@"^\s*-\s*[""']?([A-Za-z_][A-Za-z0-9_]*)(?:=.*?)?[""']?\s*(#.*)?$", Options);
    private static readonly Regex ComposeMapEntry = new(@"^\s*[""']?([A-Za-z_][A-Za-z0-9_]*)[""']?\s*:(?:\s.*)?$", Options);
    private static readonly Regex KubernetesNameItem = new(@"^(\s*)-\s+name:\s*[""']?([^""'\s#]+)[""']?\s*(#.*)?$", Options);

    // CI
    private static readonly Regex WorkflowExpression = new(@"\$\{\{(.*?)\}\}", Options);
    private static readonly Regex WorkflowContextRef = new(@"\b(secrets|vars)\.([A-Za-z_][A-Za-z0-9_]*)", Options);

    // Dockerfile
    private static readonly Regex DockerInstruction = new(@"^\s*(ENV|ARG)\s+(.*)$", Options | RegexOptions.IgnoreCase);
    private static readonly Regex DockerPair = new(@"(?:^|\s)([A-Za-z_][A-Za-z0-9_]*)=(""(?:[^""\\]|\\.)*""|'[^']*'|\S*)", Options);

    // .env examples
    private static readonly Regex EnvExampleLine = new(@"^\s*(?:export\s+)?([A-Za-z_][A-Za-z0-9_]*)\s*=(.*)$", Options);

    private readonly ILogger _logger;

    public EnvironmentReferenceScanner(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    internal enum FileKind
    {
        None,
        CSharp,
        Python,
        JavaScript,
        Go,
        Jvm,
        SpringConfig,
        Compose,
        Dockerfile,
        Kubernetes,
        Workflow,
        EnvExample
    }

    internal sealed record Hit(
        string Name,
        string Source,
        string Path,
        int Line,
        string? DefaultValue = null,
        string? SecretSourceReason = null);

    /// <summary>
    /// Scans the working copy at <paramref name="rootDirectory"/>. Never throws.
    /// </summary>
    public EnvironmentScanResult Scan(string? rootDirectory)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rootDirectory) || !Directory.Exists(rootDirectory))
            {
                _logger.LogWarning("Environment scan skipped: working directory does not exist. Path: {Path}", rootDirectory);
                return EnvironmentScanResult.FailedResult();
            }

            var root = Path.GetFullPath(rootDirectory);
            var hits = new List<Hit>();
            foreach (var (fullPath, relativePath, kind) in EnumerateCandidateFiles(root))
            {
                var lines = TryReadLines(fullPath, relativePath);
                if (lines is null)
                {
                    continue;
                }

                ScanLines(kind, relativePath, lines, hits);
            }

            return new EnvironmentScanResult(Aggregate(hits), failed: false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Environment scan failed. ErrorType: {ErrorType}", ex.GetType().Name);
            return EnvironmentScanResult.FailedResult();
        }
    }

    private IEnumerable<(string FullPath, string RelativePath, FileKind Kind)> EnumerateCandidateFiles(string root)
    {
        var result = new List<(string, string, FileKind)>();
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            string[] files;
            string[] subdirectories;
            try
            {
                files = Directory.GetFiles(directory);
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "Environment scan skipped an unreadable directory. Path: {Path}, ErrorType: {ErrorType}",
                    ToRelative(root, directory), ex.GetType().Name);
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                if (SkippedDirectories.Contains(Path.GetFileName(subdirectory)))
                {
                    continue;
                }

                try
                {
                    if (new DirectoryInfo(subdirectory).LinkTarget is not null)
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }

                pending.Push(subdirectory);
            }

            foreach (var file in files)
            {
                var relative = ToRelative(root, file);
                var kind = Classify(relative);
                if (kind != FileKind.None)
                {
                    result.Add((file, relative, kind));
                }
            }
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Item2, b.Item2));
        return result;
    }

    private static string ToRelative(string root, string path)
    {
        return Path.GetRelativePath(root, path).Replace('\\', '/');
    }

    private string[]? TryReadLines(string fullPath, string relativePath)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxFileBytes)
            {
                return null;
            }

            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length > MaxFileBytes)
            {
                return null;
            }

            var probe = Math.Min(bytes.Length, BinaryProbeBytes);
            if (Array.IndexOf(bytes, (byte)0, 0, probe) >= 0)
            {
                return null;
            }

            var text = DecodeText(bytes);
            return text.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Environment scan skipped an unreadable file. Path: {Path}, ErrorType: {ErrorType}",
                relativePath, ex.GetType().Name);
            return null;
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        return Encoding.UTF8.GetString(bytes);
    }

    internal static FileKind Classify(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        var fileName = normalized[(normalized.LastIndexOf('/') + 1)..];
        var lower = fileName.ToLowerInvariant();
        var extension = Path.GetExtension(lower);
        var isYaml = extension is ".yml" or ".yaml";

        if (lower is ".env.example" or ".env.sample" or ".env.template" || lower.EndsWith(".env.example", StringComparison.Ordinal))
        {
            return FileKind.EnvExample;
        }

        if (lower.StartsWith("dockerfile", StringComparison.Ordinal))
        {
            return FileKind.Dockerfile;
        }

        if (isYaml && IsWorkflowFile(normalized))
        {
            return FileKind.Workflow;
        }

        if (isYaml && (lower.StartsWith("docker-compose", StringComparison.Ordinal) ||
                       lower.StartsWith("compose", StringComparison.Ordinal)))
        {
            return FileKind.Compose;
        }

        if ((isYaml || extension == ".properties") && lower.StartsWith("application", StringComparison.Ordinal))
        {
            return FileKind.SpringConfig;
        }

        if (isYaml)
        {
            return FileKind.Kubernetes;
        }

        return extension switch
        {
            ".cs" => FileKind.CSharp,
            ".py" => FileKind.Python,
            ".js" or ".jsx" or ".ts" or ".tsx" or ".mjs" or ".cjs" or ".mts" or ".cts" => FileKind.JavaScript,
            ".go" => FileKind.Go,
            ".java" or ".kt" or ".kts" => FileKind.Jvm,
            _ => FileKind.None
        };
    }

    private static bool IsWorkflowFile(string normalizedPath)
    {
        const string marker = ".github/workflows/";
        var lower = normalizedPath.ToLowerInvariant();
        int start;
        if (lower.StartsWith(marker, StringComparison.Ordinal))
        {
            start = marker.Length;
        }
        else
        {
            var index = lower.IndexOf("/" + marker, StringComparison.Ordinal);
            if (index < 0)
            {
                return false;
            }

            start = index + 1 + marker.Length;
        }

        return lower.IndexOf('/', start) < 0;
    }

    internal static void ScanLines(FileKind kind, string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        switch (kind)
        {
            case FileKind.CSharp:
                ScanCode(path, lines, hits, "//",
                    (CsGetEnvironmentVariable, SourceCSharp, false),
                    (CsConfigurationIndexer, SourceCSharpConfiguration, true),
                    (CsGetValue, SourceCSharpConfiguration, true));
                break;
            case FileKind.Python:
                ScanCode(path, lines, hits, "#",
                    (PyGetenv, SourcePython, false),
                    (PyEnvironIndexer, SourcePython, false),
                    (PyEnvironGet, SourcePython, false));
                break;
            case FileKind.JavaScript:
                ScanCode(path, lines, hits, "//",
                    (JsProcessEnvMember, SourceJavaScript, false),
                    (JsProcessEnvIndexer, SourceJavaScript, false),
                    (JsImportMetaEnv, SourceJavaScript, false));
                break;
            case FileKind.Go:
                ScanCode(path, lines, hits, "//", (GoGetenv, SourceGo, false));
                break;
            case FileKind.Jvm:
                ScanCode(path, lines, hits, "//",
                    (JvmGetenv, SourceJvm, false),
                    (JvmValueAnnotation, SourceJvm, false));
                break;
            case FileKind.SpringConfig:
                ScanExpansions(path, lines, hits, SourceSpringConfig, spring: true);
                break;
            case FileKind.Compose:
                ScanExpansions(path, lines, hits, SourceCompose, spring: false);
                ScanComposeEnvironmentBlocks(path, lines, hits);
                break;
            case FileKind.Dockerfile:
                ScanDockerfile(path, lines, hits);
                break;
            case FileKind.Kubernetes:
                ScanKubernetesEnvBlocks(path, lines, hits);
                break;
            case FileKind.Workflow:
                ScanWorkflow(path, lines, hits);
                break;
            case FileKind.EnvExample:
                ScanEnvExample(path, lines, hits);
                break;
        }
    }

    private static void ScanCode(
        string path,
        IReadOnlyList<string> lines,
        List<Hit> hits,
        string commentPrefix,
        params (Regex Pattern, string Source, bool AllowDotNetKey)[] patterns)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith(commentPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var (pattern, source, allowKey) in patterns)
            {
                foreach (Match match in pattern.Matches(line))
                {
                    AddHit(hits, match.Groups[1].Value, source, path, i + 1, allowDotNetKey: allowKey);
                }
            }
        }
    }

    /// <summary>
    /// <c>${X}</c>, <c>${X:-d}</c>, <c>${X-d}</c> (compose) and <c>${X:d}</c> (Spring).
    /// <c>$${X}</c> is an escape and is skipped.
    /// </summary>
    private static void ScanExpansions(string path, IReadOnlyList<string> lines, List<Hit> hits, string source, bool spring)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith('#'))
            {
                continue;
            }

            foreach (Match match in Expansion.Matches(line))
            {
                var name = match.Groups[1].Value;
                var tail = ReadExpansionTail(line, match.Index + match.Length);
                string? defaultValue = null;
                if (tail is not null)
                {
                    if (spring)
                    {
                        if (tail.StartsWith(':'))
                        {
                            defaultValue = tail[1..];
                        }
                    }
                    else if (tail.StartsWith(":-", StringComparison.Ordinal))
                    {
                        defaultValue = tail[2..];
                    }
                    else if (tail.StartsWith('-'))
                    {
                        defaultValue = tail[1..];
                    }
                }

                AddHit(hits, name, source, path, i + 1, defaultValue);
            }
        }
    }

    /// <summary>
    /// Text between the variable name and the closing brace, or null when the expansion
    /// is nested or unterminated (no default is taken from those).
    /// </summary>
    private static string? ReadExpansionTail(string line, int start)
    {
        var end = line.IndexOf('}', start);
        if (end < 0)
        {
            return null;
        }

        var tail = line[start..end];
        return tail.Contains('$') || tail.Contains('{') ? null : tail;
    }

    private static int Indent(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == ' ')
        {
            count++;
        }

        return count;
    }

    private static bool IsBlankOrComment(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith('#');
    }

    /// <summary>
    /// Whether <paramref name="line"/> still belongs to a block whose key sits at <paramref name="keyColumn"/>.
    /// A YAML sequence may sit at the same indentation as its parent key.
    /// </summary>
    private static bool InBlock(string line, int keyColumn)
    {
        var indent = Indent(line);
        return indent > keyColumn || (indent == keyColumn && line.AsSpan(indent).StartsWith("- "));
    }

    private static int KeyColumn(Match blockStart)
    {
        return blockStart.Groups[1].Length + blockStart.Groups[2].Length;
    }

    /// <summary>
    /// Keys of compose <c>environment:</c> blocks, both <c>- X=…</c> and <c>X: …</c>.
    /// Values are never taken.
    /// </summary>
    private static void ScanComposeEnvironmentBlocks(string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var start = EnvironmentBlockStart.Match(lines[i]);
            if (!start.Success)
            {
                continue;
            }

            var keyColumn = KeyColumn(start);
            int? entryIndent = null;
            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var line = lines[j];
                if (IsBlankOrComment(line))
                {
                    continue;
                }

                if (!InBlock(line, keyColumn))
                {
                    break;
                }

                var indent = Indent(line);
                entryIndent ??= indent;
                if (indent != entryIndent)
                {
                    continue;
                }

                var listEntry = ComposeListEntry.Match(line);
                if (listEntry.Success)
                {
                    AddHit(hits, listEntry.Groups[1].Value, SourceCompose, path, j + 1);
                    continue;
                }

                var mapEntry = ComposeMapEntry.Match(line);
                if (mapEntry.Success)
                {
                    AddHit(hits, mapEntry.Groups[1].Value, SourceCompose, path, j + 1);
                }
            }

            i = j - 1;
        }
    }

    /// <summary>
    /// <c>- name: X</c> items directly inside an <c>env:</c> block; <c>secretKeyRef</c> inside
    /// such an item marks it as a secret.
    /// </summary>
    private static void ScanKubernetesEnvBlocks(string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var start = EnvBlockStart.Match(lines[i]);
            if (!start.Success)
            {
                continue;
            }

            var keyColumn = KeyColumn(start);
            int? itemIndent = null;
            string? itemName = null;
            var itemLine = 0;
            var itemIsSecret = false;

            void FlushItem()
            {
                if (itemName is not null)
                {
                    AddHit(
                        hits,
                        itemName,
                        itemIsSecret ? SourceKubernetesSecret : SourceKubernetes,
                        path,
                        itemLine,
                        secretSourceReason: itemIsSecret ? "secretKeyRef" : null);
                }

                itemName = null;
                itemIsSecret = false;
            }

            var j = i + 1;
            for (; j < lines.Count; j++)
            {
                var line = lines[j];
                if (IsBlankOrComment(line))
                {
                    continue;
                }

                if (!InBlock(line, keyColumn))
                {
                    break;
                }

                var indent = Indent(line);
                var isItemStart = line.AsSpan(indent).StartsWith("-");
                if (isItemStart && (itemIndent is null || indent == itemIndent))
                {
                    FlushItem();
                    itemIndent ??= indent;
                    var nameItem = KubernetesNameItem.Match(line);
                    if (nameItem.Success)
                    {
                        itemName = nameItem.Groups[2].Value;
                        itemLine = j + 1;
                    }

                    continue;
                }

                if (itemName is not null && line.Contains("secretKeyRef", StringComparison.Ordinal))
                {
                    itemIsSecret = true;
                }
            }

            FlushItem();
            i = j - 1;
        }
    }

    private static void ScanWorkflow(string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith('#'))
            {
                continue;
            }

            foreach (Match expression in WorkflowExpression.Matches(lines[i]))
            {
                foreach (Match reference in WorkflowContextRef.Matches(expression.Groups[1].Value))
                {
                    var isSecret = reference.Groups[1].Value == "secrets";
                    AddHit(
                        hits,
                        reference.Groups[2].Value,
                        isSecret ? SourceCiSecret : SourceCiVariable,
                        path,
                        i + 1,
                        secretSourceReason: isSecret ? "GitHub Actions secrets" : null);
                }
            }
        }
    }

    private static void ScanDockerfile(string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        string? continuedInstruction = null;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            string instruction;
            string rest;

            if (continuedInstruction is not null)
            {
                instruction = continuedInstruction;
                rest = line;
            }
            else
            {
                var match = DockerInstruction.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                instruction = match.Groups[1].Value.ToUpperInvariant();
                rest = match.Groups[2].Value;
            }

            rest = rest.TrimEnd();
            var continues = rest.EndsWith('\\');
            if (continues)
            {
                rest = rest[..^1].TrimEnd();
            }

            continuedInstruction = continues ? instruction : null;
            if (rest.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var pairs = DockerPair.Matches(rest);
            if (pairs.Count > 0)
            {
                foreach (Match pair in pairs)
                {
                    var defaultValue = instruction == "ENV" ? Unquote(pair.Groups[2].Value) : null;
                    AddHit(hits, pair.Groups[1].Value, SourceDockerfile, path, i + 1, defaultValue);
                }

                continue;
            }

            // Legacy "ENV NAME value" and plain "ARG NAME": the name only.
            var firstToken = rest.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (firstToken is not null)
            {
                AddHit(hits, firstToken, SourceDockerfile, path, i + 1);
            }
        }
    }

    private static void ScanEnvExample(string path, IReadOnlyList<string> lines, List<Hit> hits)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = EnvExampleLine.Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            AddHit(hits, match.Groups[1].Value, SourceEnvExample, path, i + 1, ParseEnvValue(match.Groups[2].Value));
        }
    }

    private static string? ParseEnvValue(string raw)
    {
        var value = raw.Trim();
        if (value.Length == 0)
        {
            return null;
        }

        if (value[0] is '"' or '\'')
        {
            var close = value.IndexOf(value[0], 1);
            return close > 0 ? value[1..close] : value[1..];
        }

        var comment = value.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            value = value[..comment].TrimEnd();
        }

        return value.StartsWith('#') ? null : value;
    }

    private static string? Unquote(string value)
    {
        if (value.Length >= 2 && value[0] is '"' or '\'' && value[^1] == value[0])
        {
            value = value[1..^1];
        }

        return value;
    }

    private static void AddHit(
        List<Hit> hits,
        string name,
        string source,
        string path,
        int line,
        string? defaultValue = null,
        string? secretSourceReason = null,
        bool allowDotNetKey = false)
    {
        name = name.Trim();
        var valid = EnvNameRegex.IsMatch(name) || (allowDotNetKey && DotNetKeyRegex.IsMatch(name));
        if (!valid || ShellVariables.Contains(name))
        {
            return;
        }

        hits.Add(new Hit(name, source, path, line, string.IsNullOrEmpty(defaultValue) ? null : defaultValue, secretSourceReason));
    }

    internal static IReadOnlyList<EnvironmentVariableReference> Aggregate(IEnumerable<Hit> hits)
    {
        var ordered = hits
            .OrderBy(h => h.Path, StringComparer.Ordinal)
            .ThenBy(h => h.Line)
            .ThenBy(h => h.Name, StringComparer.Ordinal)
            .ThenBy(h => h.Source, StringComparer.Ordinal)
            .ToList();

        var result = new List<EnvironmentVariableReference>();
        foreach (var group in ordered.GroupBy(h => DoubleUnderscore.Replace(h.Name, ":"), StringComparer.Ordinal))
        {
            var groupHits = group.ToList();
            var colonSpelling = groupHits
                .Select(h => h.Name)
                .Where(n => n.Contains(':'))
                .OrderBy(n => n, StringComparer.Ordinal)
                .FirstOrDefault();

            string name;
            string? envName = null;
            if (colonSpelling is not null)
            {
                name = colonSpelling;
                envName = colonSpelling.Replace(":", "__", StringComparison.Ordinal);
            }
            else
            {
                name = groupHits.Select(h => h.Name).OrderBy(n => n, StringComparer.Ordinal).First();
            }

            var secretSource = groupHits.Select(h => h.SecretSourceReason).FirstOrDefault(r => r is not null);
            var (isSecret, reason) = secretSource is not null
                ? (true, secretSource)
                : ClassifySecret(name);

            var defaultValue = isSecret
                ? null
                : groupHits.Select(h => h.DefaultValue).FirstOrDefault(v => !string.IsNullOrEmpty(v));

            result.Add(new EnvironmentVariableReference
            {
                Name = name,
                EnvName = envName,
                Sources = groupHits.Select(h => h.Source).Distinct(StringComparer.Ordinal).OrderBy(s => s, StringComparer.Ordinal).ToList(),
                Locations = groupHits.Select(h => $"{h.Path}:{h.Line}").Distinct(StringComparer.Ordinal).Take(MaxLocations).ToList(),
                IsLikelySecret = isSecret,
                SecretReason = reason,
                DefaultValue = defaultValue
            });
        }

        result.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
        return result;
    }

    internal static IReadOnlyList<string> Tokenize(string name)
    {
        return TokenSeparators.Split(name)
            .SelectMany(part => CamelBoundary.Split(part))
            .Where(token => token.Length > 0)
            .Select(token => token.ToUpperInvariant())
            .ToList();
    }

    /// <summary>
    /// Classifies a name by its tokens (split on <c>_ - : .</c> and camelCase boundaries).
    /// </summary>
    internal static (bool IsSecret, string? Reason) ClassifySecret(string name)
    {
        if (string.Equals(name, "PWD", StringComparison.OrdinalIgnoreCase))
        {
            return (false, null);
        }

        var tokens = Tokenize(name);
        if (tokens.Any(t => t is "AUTHOR" or "AUTHORITY"))
        {
            return (false, null);
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            string? match = null;
            var end = i;
            if (i + 1 < tokens.Count)
            {
                var pair = (tokens[i], tokens[i + 1]);
                match = pair switch
                {
                    ("CONNECTION", "STRING" or "STRINGS") => "CONNECTION STRING",
                    ("AUTH", "TOKEN") => "AUTH TOKEN",
                    ("AUTH", "KEY") => "AUTH KEY",
                    ("ACCESS", "KEY") => "ACCESS KEY",
                    _ => null
                };
                if (match is not null)
                {
                    end = i + 1;
                }
            }

            if (match is null && SecretTokens.Contains(tokens[i]))
            {
                match = tokens[i];
            }

            if (match is null)
            {
                continue;
            }

            var followedByNonSecret = false;
            for (var k = end + 1; k < tokens.Count; k++)
            {
                if (NonSecretSuffixTokens.Contains(tokens[k]))
                {
                    followedByNonSecret = true;
                    break;
                }
            }

            if (!followedByNonSecret)
            {
                return (true, $"name token {match}");
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Markdown for the agent message: a table of all names within <paramref name="maxChars"/>.
    /// Rows that do not fit are listed as a compact line of names so every name reaches the model;
    /// if even that does not fit, the tail is summarised as "…and N more".
    /// </summary>
    internal static string Render(EnvironmentScanResult result, int maxChars = MaxRenderedChars)
    {
        if (result.Failed)
        {
            return "The deterministic environment scanner did not complete, so no list of names is available. " +
                   "Find environment variables and secrets with the source tools.";
        }

        var variables = result.Variables;
        if (variables.Count == 0)
        {
            return "No environment variables or secrets were detected by the deterministic scanner.";
        }

        var secretCount = variables.Count(v => v.IsLikelySecret);
        var header = new StringBuilder()
            .Append($"The scanner found {variables.Count} names: {secretCount} likely secrets and {variables.Count - secretCount} other variables. ")
            .Append("Secret values are never shown.\n\n")
            .Append("| Name | Secret? | Sources | Default | Locations |\n")
            .Append("|---|---|---|---|---|\n")
            .ToString();

        var rows = variables.Select(RenderRow).ToList();
        var nameTokens = variables.Select(v => $"`{v.Name}`").ToList();
        const string compactPrefix = "\nNot shown in the table (names only; each one must still appear on the page): ";

        var count = variables.Count;
        var suffixLength = new int[count + 1];
        for (var i = count - 1; i >= 0; i--)
        {
            suffixLength[i] = suffixLength[i + 1] + nameTokens[i].Length + (i == count - 1 ? 0 : 2);
        }

        int Total(int rowCount, int rowsLength) =>
            header.Length + rowsLength + (rowCount < count ? compactPrefix.Length + suffixLength[rowCount] : 0);

        var bestRows = -1;
        var length = 0;
        for (var k = 0; k <= count; k++)
        {
            if (Total(k, length) <= maxChars)
            {
                bestRows = k;
            }

            if (k < count)
            {
                length += rows[k].Length;
                if (header.Length + length > maxChars)
                {
                    break;
                }
            }
        }

        var builder = new StringBuilder(header);
        if (bestRows >= 0)
        {
            for (var k = 0; k < bestRows; k++)
            {
                builder.Append(rows[k]);
            }

            if (bestRows < count)
            {
                builder.Append(compactPrefix).Append(string.Join(", ", nameTokens.Skip(bestRows)));
            }

            return builder.ToString();
        }

        // Even the bare list of names does not fit: list as many as possible, then the count.
        builder.Append(compactPrefix);
        var listed = 0;
        for (; listed < count; listed++)
        {
            var piece = (listed == 0 ? string.Empty : ", ") + nameTokens[listed];
            var reserve = $", …and {count - listed - 1} more".Length;
            if (builder.Length + piece.Length + reserve > maxChars)
            {
                break;
            }

            builder.Append(piece);
        }

        if (listed < count)
        {
            builder.Append(listed == 0 ? string.Empty : ", ").Append($"…and {count - listed} more");
        }

        return builder.ToString();
    }

    private static string RenderRow(EnvironmentVariableReference variable)
    {
        var name = variable.EnvName is null
            ? $"`{variable.Name}`"
            : $"`{variable.Name}` (env: `{variable.EnvName}`)";
        var secret = variable.IsLikelySecret
            ? $"yes ({variable.SecretReason})"
            : "no";
        var defaultValue = variable.DefaultValue is null
            ? "—"
            : $"`{variable.DefaultValue.Replace('`', '\'')}`";
        return $"| {Cell(name)} | {Cell(secret)} | {Cell(string.Join(", ", variable.Sources))} | {Cell(defaultValue)} | {Cell(string.Join(", ", variable.Locations))} |\n";
    }

    private static string Cell(string value)
    {
        return value.Replace("|", "\\|", StringComparison.Ordinal).Replace('\n', ' ').Replace('\r', ' ');
    }
}
