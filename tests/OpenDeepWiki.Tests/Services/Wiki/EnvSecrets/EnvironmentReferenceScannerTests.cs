using System.Text;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.Services.Wiki.EnvSecrets;
using Xunit;

namespace OpenDeepWiki.Tests.Services.Wiki.EnvSecrets;

public class EnvironmentReferenceScannerTests
{
    private static EnvironmentScanResult Scan(TempRepository repo, ILogger? logger = null)
    {
        var result = new EnvironmentReferenceScanner(logger).Scan(repo.Root);
        Assert.False(result.Failed);
        return result;
    }

    private static EnvironmentVariableReference Find(EnvironmentScanResult result, string name)
    {
        var variable = result.Variables.SingleOrDefault(v => v.Name == name);
        Assert.True(variable is not null, $"'{name}' not found; got: {string.Join(", ", result.Variables.Select(v => v.Name))}");
        return variable!;
    }

    private static void AssertFound(EnvironmentScanResult result, string name, string source, params string[] locations)
    {
        var variable = Find(result, name);
        Assert.Contains(source, variable.Sources);
        foreach (var location in locations)
        {
            Assert.Contains(location, variable.Locations);
        }
    }

    private static void AssertAbsent(EnvironmentScanResult result, string name)
    {
        Assert.DoesNotContain(result.Variables, v => v.Name == name || v.EnvName == name);
    }

    // ---- one fixture per source ----

    [Fact]
    public void CSharp_FindsEnvironmentAndConfigurationReads()
    {
        using var repo = new TempRepository().Write("src/App/Program.cs", """
            var a = Environment.GetEnvironmentVariable("CS_ENV_A");
            var b = GetEnvironmentVariable("CS_ENV_B");
            var c = configuration["CsConfig"];
            var d = _configuration["Section:Key"];
            var e = config["CS_CONF_E"];
            var f = settings.GetValue<int>("CS_PORT_F");
            var g = configuration.GetSection("Logging");
            // var h = Environment.GetEnvironmentVariable("CS_COMMENTED");
            """);

        var result = Scan(repo);

        AssertFound(result, "CS_ENV_A", "C#", "src/App/Program.cs:1");
        AssertFound(result, "CS_ENV_B", "C#", "src/App/Program.cs:2");
        AssertFound(result, "CsConfig", "C# configuration", "src/App/Program.cs:3");
        AssertFound(result, "Section:Key", "C# configuration", "src/App/Program.cs:4");
        AssertFound(result, "CS_CONF_E", "C# configuration", "src/App/Program.cs:5");
        AssertFound(result, "CS_PORT_F", "C# configuration", "src/App/Program.cs:6");
        Assert.Equal("Section__Key", Find(result, "Section:Key").EnvName);
        AssertAbsent(result, "Logging");
        AssertAbsent(result, "CS_COMMENTED");
    }

    [Fact]
    public void Python_FindsGetenvAndEnviron()
    {
        using var repo = new TempRepository().Write("app/settings.py", """
            import os
            a = os.getenv("PY_A")
            b = os.environ["PY_B"]
            c = os.environ.get('PY_C', 'x')
            d = environ.get("PY_D")
            """);

        var result = Scan(repo);

        AssertFound(result, "PY_A", "Python", "app/settings.py:2");
        AssertFound(result, "PY_B", "Python", "app/settings.py:3");
        AssertFound(result, "PY_C", "Python", "app/settings.py:4");
        AssertFound(result, "PY_D", "Python", "app/settings.py:5");
        Assert.Null(Find(result, "PY_C").DefaultValue);
    }

    [Fact]
    public void JavaScript_FindsProcessEnvAndImportMetaEnv()
    {
        using var repo = new TempRepository().Write("web/lib/config.ts", """
            export const a = process.env.JS_A;
            export const b = process.env["JS_B"];
            export const c = import.meta.env.VITE_C;
            """);

        var result = Scan(repo);

        AssertFound(result, "JS_A", "JS/TS", "web/lib/config.ts:1");
        AssertFound(result, "JS_B", "JS/TS", "web/lib/config.ts:2");
        AssertFound(result, "VITE_C", "JS/TS", "web/lib/config.ts:3");
    }

    [Fact]
    public void Go_FindsGetenvAndLookupEnv()
    {
        using var repo = new TempRepository().Write("cmd/main.go", """
            package main
            var a = os.Getenv("GO_A")
            var b, ok = os.LookupEnv("GO_B")
            """);

        var result = Scan(repo);

        AssertFound(result, "GO_A", "Go", "cmd/main.go:2");
        AssertFound(result, "GO_B", "Go", "cmd/main.go:3");
    }

    [Fact]
    public void Jvm_FindsGetenvValueAnnotationAndSpringPlaceholders()
    {
        using var repo = new TempRepository()
            .Write("src/main/java/App.java", """
                class App {
                  String a = System.getenv("JAVA_A");
                  @Value("${JAVA_VALUE}") String b;
                  @Value("${server.port}") String notAnEnvName;
                }
                """)
            .Write("src/main/resources/application-prod.yml", """
                spring:
                  datasource:
                    url: ${SPRING_DB_URL:jdbc:h2:mem}
                    password: ${SPRING_DB_PASSWORD:changeme}
                """)
            .Write("src/main/resources/application.properties", "server.port=${SERVER_PORT:8080}\n");

        var result = Scan(repo);

        AssertFound(result, "JAVA_A", "Java/Kotlin", "src/main/java/App.java:2");
        AssertFound(result, "JAVA_VALUE", "Java/Kotlin", "src/main/java/App.java:3");
        AssertFound(result, "SPRING_DB_URL", "Spring config", "src/main/resources/application-prod.yml:3");
        AssertFound(result, "SERVER_PORT", "Spring config", "src/main/resources/application.properties:1");
        Assert.Equal("8080", Find(result, "SERVER_PORT").DefaultValue);
        Assert.True(Find(result, "SPRING_DB_PASSWORD").IsLikelySecret);
        Assert.Null(Find(result, "SPRING_DB_PASSWORD").DefaultValue);
        AssertAbsent(result, "server.port");
    }

    [Fact]
    public void Compose_FindsExpansionsAndEnvironmentBlocks()
    {
        using var repo = new TempRepository().Write("compose.yaml", """
            services:
              api:
                image: example/api:${API_TAG:-latest}
                command: sh -c "echo $${HOME} && cd ${PWD}"
                environment:
                  - LIST_FORM=1
                  - BARE_LIST
                ports:
                  - "8080:8080"
              worker:
                environment:
                  MAP_FORM: "x"
                  OTHER_MAP: ${OTHER_SOURCE-fallback}
                depends_on:
                  - api
            """);

        var result = Scan(repo);

        AssertFound(result, "API_TAG", "compose", "compose.yaml:3");
        Assert.Equal("latest", Find(result, "API_TAG").DefaultValue);
        AssertFound(result, "LIST_FORM", "compose", "compose.yaml:6");
        AssertFound(result, "BARE_LIST", "compose", "compose.yaml:7");
        AssertFound(result, "MAP_FORM", "compose", "compose.yaml:12");
        AssertFound(result, "OTHER_MAP", "compose", "compose.yaml:13");
        AssertFound(result, "OTHER_SOURCE", "compose", "compose.yaml:13");
        Assert.Equal("fallback", Find(result, "OTHER_SOURCE").DefaultValue);
        AssertAbsent(result, "HOME");
        AssertAbsent(result, "PWD");
        AssertAbsent(result, "api");
    }

    [Fact]
    public void Dockerfile_FindsEnvAndArg()
    {
        using var repo = new TempRepository().Write("Dockerfile.prod", """
            FROM alpine
            ARG BUILD_VERSION
            ENV APP_MODE=production
            ENV LEGACY_FORM some value
            ENV FIRST=1 \
                SECOND=2
            """);

        var result = Scan(repo);

        AssertFound(result, "BUILD_VERSION", "Dockerfile", "Dockerfile.prod:2");
        AssertFound(result, "APP_MODE", "Dockerfile", "Dockerfile.prod:3");
        Assert.Equal("production", Find(result, "APP_MODE").DefaultValue);
        AssertFound(result, "LEGACY_FORM", "Dockerfile", "Dockerfile.prod:4");
        AssertFound(result, "FIRST", "Dockerfile", "Dockerfile.prod:5");
        AssertFound(result, "SECOND", "Dockerfile", "Dockerfile.prod:6");
    }

    [Fact]
    public void Kubernetes_FindsNamesOnlyInsideEnvBlocks()
    {
        using var repo = new TempRepository().Write("deploy/templates/deployment.yaml", """
            spec:
              containers:
                - name: nginx
                  image: nginx:{{ .Values.tag }}
                  env:
                    - name: K8S_PLAIN
                      value: "1"
                    - name: K8S_SECRET_REF
                      valueFrom:
                        secretKeyRef:
                          name: app-secrets
                          key: password
                    - name: {{ .Values.dynamic }}
                  ports:
                    - name: http
                      containerPort: 80
            """);

        var result = Scan(repo);

        AssertFound(result, "K8S_PLAIN", "Kubernetes/Helm env", "deploy/templates/deployment.yaml:6");
        AssertFound(result, "K8S_SECRET_REF", "Kubernetes secretKeyRef", "deploy/templates/deployment.yaml:8");
        Assert.False(Find(result, "K8S_PLAIN").IsLikelySecret);
        var secretRef = Find(result, "K8S_SECRET_REF");
        Assert.True(secretRef.IsLikelySecret);
        Assert.Equal("secretKeyRef", secretRef.SecretReason);
        AssertAbsent(result, "nginx");
        AssertAbsent(result, "http");
        AssertAbsent(result, "app-secrets");
        Assert.Equal(2, result.Variables.Count);
    }

    [Fact]
    public void Workflow_FindsSecretsAndVars_IgnoresDeploymentEnvironment()
    {
        using var repo = new TempRepository().Write(".github/workflows/deploy.yml", """
            jobs:
              deploy:
                environment: production
                steps:
                  - run: ./deploy.sh
                    env:
                      TOKEN: ${{ secrets.DEPLOY }}
                      REGION: ${{ vars.DEPLOY_REGION }}
            """);

        var result = Scan(repo);

        AssertFound(result, "DEPLOY", "GitHub Actions secret", ".github/workflows/deploy.yml:7");
        AssertFound(result, "DEPLOY_REGION", "GitHub Actions variable", ".github/workflows/deploy.yml:8");
        Assert.True(Find(result, "DEPLOY").IsLikelySecret);
        Assert.False(Find(result, "DEPLOY_REGION").IsLikelySecret);
        AssertAbsent(result, "production");
        AssertAbsent(result, "TOKEN");
        Assert.Equal(2, result.Variables.Count);
    }

    [Fact]
    public void EnvExample_FindsNamesAndDefaults()
    {
        using var repo = new TempRepository()
            .Write(".env.example", """
                # comment
                LOG_LEVEL=debug
                export DB_TYPE="sqlite" # inline comment
                EMPTY_ONE=
                OPENAI_API_KEY=sk-placeholder
                """)
            .Write("deploy/prod.env.example", "PROD_ONLY=1\n");

        var result = Scan(repo);

        AssertFound(result, "LOG_LEVEL", ".env example", ".env.example:2");
        Assert.Equal("debug", Find(result, "LOG_LEVEL").DefaultValue);
        Assert.Equal("sqlite", Find(result, "DB_TYPE").DefaultValue);
        Assert.Null(Find(result, "EMPTY_ONE").DefaultValue);
        Assert.Null(Find(result, "OPENAI_API_KEY").DefaultValue);
        AssertFound(result, "PROD_ONLY", ".env example", "deploy/prod.env.example:1");
    }

    // ---- traversal ----

    [Fact]
    public void Traversal_SkipsIgnoredDirectoriesBinaryAndLargeFiles()
    {
        var large = new StringBuilder("var x = Environment.GetEnvironmentVariable(\"LARGE_FILE_VAR\");\n");
        while (large.Length <= EnvironmentReferenceScanner.MaxFileBytes)
        {
            large.Append("// padding padding padding padding padding padding padding padding\n");
        }

        var binary = Encoding.UTF8.GetBytes("var y = Environment.GetEnvironmentVariable(\"BINARY_VAR\");\n\0\0\0");

        using var repo = new TempRepository()
            .Write("node_modules/lib/index.js", "process.env.NODE_MODULES_VAR")
            .Write("src/bin/Debug/Gen.cs", "Environment.GetEnvironmentVariable(\"BIN_VAR\")")
            .Write("src/Large.cs", large.ToString())
            .WriteBytes("src/Binary.cs", binary)
            .Write(".github/workflows/ci.yml", "x: ${{ secrets.CI_ONLY }}\n")
            .Write("src/Kept.cs", "Environment.GetEnvironmentVariable(\"KEPT_VAR\")");

        var result = Scan(repo);

        AssertAbsent(result, "NODE_MODULES_VAR");
        AssertAbsent(result, "BIN_VAR");
        AssertAbsent(result, "LARGE_FILE_VAR");
        AssertAbsent(result, "BINARY_VAR");
        Find(result, "KEPT_VAR");
        Find(result, "CI_ONLY");
    }

    [Fact]
    public void DotNetKey_AndDoubleUnderscoreForm_AreOneEntry()
    {
        using var repo = new TempRepository()
            .Write("src/Startup.cs", "var endpoint = configuration[\"AI:Endpoint\"];\n")
            .Write("compose.yaml", """
                services:
                  api:
                    environment:
                      - AI__Endpoint=http://localhost
                """);

        var result = Scan(repo);

        var entry = Assert.Single(result.Variables);
        Assert.Equal("AI:Endpoint", entry.Name);
        Assert.Equal("AI__Endpoint", entry.EnvName);
        Assert.Contains("compose.yaml:4", entry.Locations);
        Assert.Contains("src/Startup.cs:1", entry.Locations);
    }

    [Fact]
    public void Traps_AreNotReported()
    {
        using var repo = new TempRepository()
            .Write("docker-compose.yml", """
                services:
                  app:
                    command: ["sh", "-c", "echo $${HOME}; ls ${PWD}"]
                """)
            .Write("k8s/pod.yaml", """
                spec:
                  containers:
                    - name: nginx
                      image: nginx
                """)
            .Write(".github/workflows/release.yml", """
                jobs:
                  release:
                    environment: production
                    environment:
                      name: staging
                """)
            .Write("src/Program.cs", "var logging = configuration.GetSection(\"Logging\");\n")
            .Write("README.md", "Set ${README_VAR} before start.\n")
            .Write("run.sh", "echo ${SHELL_SCRIPT_VAR}\nexport SHELL_EXPORT=1\n");

        var result = Scan(repo);

        Assert.Empty(result.Variables);
    }

    // ---- classification ----

    [Theory]
    [InlineData("OPENAI_API_KEY")]
    [InlineData("apiKey")]
    [InlineData("ApiKey")]
    [InlineData("DB_PASSWORD")]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("JWT_SECRET_KEY")]
    [InlineData("APIKEY")]
    [InlineData("SENTRY_DSN")]
    public void ClassifySecret_Secrets(string name)
    {
        var (isSecret, reason) = EnvironmentReferenceScanner.ClassifySecret(name);

        Assert.True(isSecret, name);
        Assert.False(string.IsNullOrEmpty(reason));
    }

    [Theory]
    [InlineData("LOG_LEVEL")]
    [InlineData("PORT")]
    [InlineData("AUTHOR")]
    [InlineData("AUTHORITY")]
    [InlineData("CERT_PATH")]
    [InlineData("TOKEN_TTL")]
    [InlineData("KEY_VAULT_URL")]
    [InlineData("PWD")]
    [InlineData("MAX_TOKENS")]
    public void ClassifySecret_NotSecrets(string name)
    {
        var (isSecret, _) = EnvironmentReferenceScanner.ClassifySecret(name);

        Assert.False(isSecret, name);
    }

    [Fact]
    public void Classification_BySource_SecretsAndSecretKeyRef()
    {
        using var repo = new TempRepository()
            .Write(".github/workflows/ci.yml", "x: ${{ secrets.DEPLOY }}\n")
            .Write("k8s/deploy.yaml", """
                env:
                  - name: PLAIN_LOOKING
                    valueFrom:
                      secretKeyRef:
                        name: s
                        key: k
                """);

        var result = Scan(repo);

        Assert.True(Find(result, "DEPLOY").IsLikelySecret);
        Assert.True(Find(result, "PLAIN_LOOKING").IsLikelySecret);
    }

    // ---- secret values never leave the scanner ----

    private const string SecretValue = "s3cr3tVALUE42";

    public static TheoryData<string, string, string, bool> SecretValueSources() => new()
    {
        { ".env.example", ".env.example", $"API_KEY={SecretValue}\n", true },
        { ".env", ".env", $"API_KEY={SecretValue}\n", false },
        { "compose expansion", "compose.yaml", $"services:\n  a:\n    image: x/${{API_KEY:-{SecretValue}}}\n", true },
        { "Dockerfile ENV", "Dockerfile", $"FROM alpine\nENV API_KEY={SecretValue}\n", true },
        { "compose environment", "docker-compose.yml", $"services:\n  a:\n    environment:\n      - API_KEY={SecretValue}\n      API_KEY2: {SecretValue}\n", true },
    };

    [Theory]
    [MemberData(nameof(SecretValueSources))]
    public void SecretValues_AreNeverReturned(string source, string file, string content, bool expectName)
    {
        using var repo = new TempRepository().Write(file, content);
        var logger = new CapturingLogger();

        var result = new EnvironmentReferenceScanner(logger).Scan(repo.Root);
        var rendered = EnvironmentReferenceScanner.Render(result);

        Assert.False(result.Failed, source);
        if (expectName)
        {
            var entry = Find(result, "API_KEY");
            Assert.True(entry.IsLikelySecret, source);
            Assert.Null(entry.DefaultValue);
        }

        Assert.All(result.Variables, v =>
        {
            Assert.DoesNotContain(SecretValue, v.DefaultValue ?? string.Empty);
            Assert.DoesNotContain(SecretValue, v.ToString());
        });
        Assert.DoesNotContain(SecretValue, rendered);
        Assert.DoesNotContain(SecretValue, logger.AllText);
    }

    [Fact]
    public void DefaultValue_ForNonSecrets_FromExampleAndExpansion_NotFromDotEnv()
    {
        using var repo = new TempRepository()
            .Write(".env.example", "LOG_LEVEL=debug\n")
            .Write(".env", "TIMEOUT=30\nLOG_LEVEL=trace\n")
            .Write("compose.yaml", """
                services:
                  a:
                    image: "x:${PORT:-8080}"
                    command: run --timeout ${TIMEOUT}
                """);

        var result = Scan(repo);

        Assert.Equal("debug", Find(result, "LOG_LEVEL").DefaultValue);
        Assert.Equal("8080", Find(result, "PORT").DefaultValue);
        Assert.Null(Find(result, "TIMEOUT").DefaultValue);
        Assert.DoesNotContain(".env:1", Find(result, "TIMEOUT").Locations);
    }

    // ---- determinism ----

    [Fact]
    public void Output_IsDeterministic_AcrossCreationOrderCaseAndCyrillicPaths()
    {
        var files = new (string Path, string Content)[]
        {
            // Distinct directory names only: a case-insensitive file system would merge "src" and "SRC".
            ("lib/b.py", "x = os.getenv(\"SHARED\")\ny = os.getenv(\"PY_ONLY\")\n"),
            ("SRC/A.py", "x = os.getenv(\"SHARED\")\n"),
            ("Сервис/Настройки.cs", "var a = Environment.GetEnvironmentVariable(\"SHARED\");\nvar b = config[\"Кириллица\"];\n"),
            ("клиент/App.ts", "process.env.SHARED; process.env.TS_ONLY;\n"),
            ("compose.yaml", "services:\n  a:\n    image: \"x:${TAG:-1}\"\n"),
            ("Compose.override.yaml", "services:\n  a:\n    image: \"x:${TAG:-2}\"\n"),
        };

        using var first = new TempRepository();
        using var second = new TempRepository();
        foreach (var (path, content) in files)
        {
            first.Write(path, content);
        }

        foreach (var (path, content) in files.Reverse())
        {
            second.Write(path, content);
        }

        var a = Scan(first);
        var b = Scan(second);

        Assert.Equal(a.Variables.Select(v => v.ToString()), b.Variables.Select(v => v.ToString()));
        Assert.Equal(a.Variables.Select(v => v.DefaultValue), b.Variables.Select(v => v.DefaultValue));
        Assert.Equal(EnvironmentReferenceScanner.Render(a), EnvironmentReferenceScanner.Render(b));
        Assert.Equal(a.Variables.Select(v => v.Name).OrderBy(n => n, StringComparer.Ordinal), a.Variables.Select(v => v.Name));

        var shared = Find(a, "SHARED");
        var sorted = shared.Locations.OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, shared.Locations);
        Assert.Equal(4, shared.Locations.Count);
    }

    // ---- rendering ----

    [Fact]
    public void Render_OverLimit_TruncatesTableAndListsRemainingNames()
    {
        var hits = new List<EnvironmentReferenceScanner.Hit>();
        for (var i = 0; i < 400; i++)
        {
            for (var line = 1; line <= 5; line++)
            {
                hits.Add(new EnvironmentReferenceScanner.Hit(
                    $"VARIABLE_NUMBER_{i:D4}",
                    "C#",
                    $"src/some/rather/long/directory/structure/File{i:D4}.cs",
                    line));
            }
        }

        var result = new EnvironmentScanResult(EnvironmentReferenceScanner.Aggregate(hits), failed: false);
        var rendered = EnvironmentReferenceScanner.Render(result);

        Assert.True(rendered.Length <= EnvironmentReferenceScanner.MaxRenderedChars, $"length {rendered.Length}");
        Assert.Contains("Not shown in the table", rendered);
        Assert.Contains("| `VARIABLE_NUMBER_0000` |", rendered);
        Assert.DoesNotContain("| `VARIABLE_NUMBER_0399` |", rendered);
        for (var i = 0; i < 400; i++)
        {
            Assert.Contains($"`VARIABLE_NUMBER_{i:D4}`", rendered);
        }
    }

    [Fact]
    public void Render_WhenNamesDoNotFitEither_EndsWithCount()
    {
        var hits = Enumerable.Range(0, 100)
            .Select(i => new EnvironmentReferenceScanner.Hit($"NAME_{i:D3}", "C#", "a.cs", i + 1))
            .ToList();
        var result = new EnvironmentScanResult(EnvironmentReferenceScanner.Aggregate(hits), failed: false);

        var rendered = EnvironmentReferenceScanner.Render(result, maxChars: 600);

        Assert.True(rendered.Length <= 600, $"length {rendered.Length}");
        Assert.Matches(@"…and \d+ more$", rendered);
    }

    [Fact]
    public void Render_EmptyRepository_SaysNothingDetected()
    {
        using var repo = new TempRepository().Write("README.md", "# nothing here\n");

        var result = Scan(repo);

        Assert.Empty(result.Variables);
        Assert.Contains("No environment variables or secrets were detected", EnvironmentReferenceScanner.Render(result));
    }

    [Fact]
    public void Scan_MissingDirectory_IsFailedWithoutThrowing()
    {
        var result = new EnvironmentReferenceScanner().Scan(Path.Combine(Path.GetTempPath(), "missing-" + Guid.NewGuid()));

        Assert.True(result.Failed);
        Assert.Empty(result.Variables);
        Assert.Contains("did not complete", EnvironmentReferenceScanner.Render(result));
    }

    [Fact]
    public void Scan_UnreadableFile_IsSkippedWithWarning()
    {
        using var repo = new TempRepository()
            .Write("src/Good.cs", "Environment.GetEnvironmentVariable(\"GOOD_VAR\")");
        var logger = new CapturingLogger();
        var unreadable = Path.Combine(repo.Root, "src", "Broken.cs");

        EnvironmentScanResult result;
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(unreadable, "Environment.GetEnvironmentVariable(\"BROKEN_VAR\")");
            using var exclusive = new FileStream(unreadable, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            result = new EnvironmentReferenceScanner(logger).Scan(repo.Root);
        }
        else
        {
            // A dangling symbolic link is listed but cannot be read, even by root.
            File.CreateSymbolicLink(unreadable, Path.Combine(repo.Root, "does-not-exist.cs"));
            result = new EnvironmentReferenceScanner(logger).Scan(repo.Root);
        }

        Assert.False(result.Failed);
        Find(result, "GOOD_VAR");
        AssertAbsent(result, "BROKEN_VAR");
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("src/Broken.cs"));
    }
}
