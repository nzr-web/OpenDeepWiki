using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OpenDeepWiki.EFCore;
using OpenDeepWiki.Services.Repositories;

namespace OpenDeepWiki.Services.Wiki.EnvSecrets;

/// <summary>
/// Tool budgets for one document generation run.
/// </summary>
internal readonly record struct DocumentBudgets(int SourceToolCalls, int MaxToolCalls, int AppendOperations);

/// <summary>
/// The mandatory "Environment variables and secrets" page: its catalog path and title,
/// its own agent message and budgets, and the deterministic completeness pass.
/// </summary>
internal static class EnvironmentPage
{
    public const string CatalogPath = "environment-and-secrets";
    public const int SourceToolBudget = 12;
    public const int MinimumToolCalls = 20;

    public static bool IsEnvironmentPage(string? path)
    {
        return path is not null &&
               string.Equals(path.Trim('/'), CatalogPath, StringComparison.OrdinalIgnoreCase);
    }

    public static string GetTitle(string? languageCode)
    {
        return NormalizeLanguage(languageCode) switch
        {
            "ru" => "Переменные окружения и секреты",
            "zh" or "zh-cn" or "zh-hans" => "环境变量与密钥",
            "zh-tw" or "zh-hant" or "zh-hk" => "環境變數與密鑰",
            _ => "Environment variables and secrets"
        };
    }

    public static DocumentBudgets GetDocumentBudgets(string? path, WikiGeneratorOptions options)
    {
        if (!IsEnvironmentPage(path))
        {
            return new DocumentBudgets(
                options.MaxDocumentSourceToolCalls,
                options.MaxDocumentToolCalls,
                options.MaxDocumentAppendOperations);
        }

        return new DocumentBudgets(
            SourceToolBudget,
            Math.Max(MinimumToolCalls, SourceToolBudget + options.MaxDocumentAppendOperations + 4),
            options.MaxDocumentAppendOperations);
    }

    /// <summary>
    /// Full replacement of the generic document-generation user message for this page.
    /// </summary>
    public static string BuildEnvironmentPageMessage(
        RepositoryWorkspace workspace,
        string languageCode,
        string gitBaseUrl,
        string catalogPath,
        string catalogTitle,
        string scannerTable,
        DocumentBudgets budgets)
    {
        var labels = PageLabels.For(languageCode);
        return $@"Please generate the reference page of all environment variables and expected secrets for the catalog item described in the runtime context: every name, what it means, and where it is used.

This page has its own requirements below. They replace the usual page structure: no Mermaid diagram and no code examples are needed.

## Scanner Results

A deterministic scanner has already searched the repository code, compose files, Dockerfiles, Kubernetes/Helm manifests, CI workflows and `.env` example files. Its table is the authoritative list of names for this page. Secret values are never included.

{scannerTable}

## Task Requirements

1. **Gather Source Material**
   - Source-discovery budget: {budgets.SourceToolCalls} calls in total across ListFiles, Grep, and ReadFile for this page.
   - Spend them on the scanner locations whose purpose or required status is unclear; read small excerpts only.
   - When any source tool reports SOURCE_TOOL_BUDGET_REACHED, immediately stop exploring and write the page with the evidence already collected.

2. **Page Structure** (write the whole page in the runtime target language)
   1. Title (H1): exactly the runtime catalog title.
   2. A short introduction: where the application takes its settings from, based on the sources the scanner found.
   3. Section ""{labels.VariablesHeading}"" with a table of the non-secret variables. Columns: name, purpose, required, default value, where used.
      - Required: state it only when the code shows it (no default value, a check for an empty value, `required`, `:?` in compose); otherwise write ""{labels.NotEstablished}"".
      - Default value: from the scanner table or the code; otherwise leave the cell empty.
      - Where used: links to the scanner locations built with the File Reference Base URL.
   4. Section ""{labels.SecretsHeading}"" with a table of the secrets (names the scanner marks as likely secrets). Columns: name, what it is (for example: API key for service X, password of database Y), where it is expected from (Kubernetes Secret, CI secret, `.env` file, environment), where used.
      - NEVER write secret values. Where a value would go, write `***`.
   5. Include every name from the scanner table, including the names listed without a table row. If the purpose of a name is not established, write ""{labels.NotEstablished}"" instead of guessing.
   6. If the scanner found nothing, say plainly that no environment variables or secrets were detected.
   7. No diagram is needed.

3. **Secret Values** (IMPORTANT)
   - Never copy a value of a key, token, password, certificate or connection string into the page, even if a file you read contains one. Use `***`.

4. **File Reference Links** (IMPORTANT)
   - When referencing source files, use the actual runtime File Reference Base URL shown below
   - Example for this task: [Example.cs]({gitBaseUrl}/src/Example.cs#L10-L20)
   - Use the exact runtime File Reference Base URL prefix shown in the example; never output literal placeholder text for the base URL
   - For specific line references, append #L<line_number> or #L<start>-L<end> to the real file URL
   - Do NOT add a source-file list section to the Markdown body; source files are tracked and rendered separately by the framework

5. **Output Requirements**
   - Write the page INCREMENTALLY so its length is not capped by a single response:
     * Call WriteDoc(content) first with the title, the introduction and the environment variables table
     * Then call AppendDoc(content) for the secrets table and any rows that did not fit
   - AppendDoc budget: {budgets.AppendOperations} calls for this page. When the budget is reached, stop appending and provide the final summary.
   - Keep code identifiers and variable names in their original form, do not translate them.

## Runtime Context

- Repository: {workspace.Organization}/{workspace.RepositoryName}
- Git URL: {workspace.GitUrl}
- Branch: {workspace.BranchName}
- File Reference Base URL: {gitBaseUrl}
- Target Language: {languageCode}
- Catalog Path: {catalogPath}
- Catalog Title: {catalogTitle}

Please start executing the task.";
    }

    /// <summary>
    /// Appends a section listing every scanner name the page does not mention.
    /// A name counts when it occurs as a whole word (ordinal); for a .NET key either
    /// spelling counts. Returns <paramref name="content"/> unchanged when nothing is missing.
    /// </summary>
    public static string EnsureAllVariablesListed(string content, EnvironmentScanResult scanResult, string? languageCode)
    {
        content ??= string.Empty;
        if (scanResult.Failed || scanResult.Variables.Count == 0)
        {
            return content;
        }

        var missing = scanResult.Variables
            .Where(variable => !variable.AllNames().Any(name => ContainsWholeWord(content, name)))
            .ToList();
        if (missing.Count == 0)
        {
            return content;
        }

        var labels = PageLabels.For(languageCode);
        var builder = new StringBuilder(content.TrimEnd());
        builder.Append("\n\n## ").Append(labels.MissingHeading).Append("\n\n");
        builder.Append($"| {labels.Name} | {labels.Secret} | {labels.Locations} | {labels.Purpose} |\n");
        builder.Append("|---|---|---|---|\n");
        foreach (var variable in missing)
        {
            var name = variable.EnvName is null
                ? $"`{variable.Name}`"
                : $"`{variable.Name}` / `{variable.EnvName}`";
            var locations = string.Join(", ", variable.Locations.Select(location => $"`{location}`"));
            builder.Append($"| {name} | {(variable.IsLikelySecret ? labels.Yes : labels.No)} | {locations} | {labels.PurposeNotEstablished} |\n");
        }

        return builder.ToString();
    }

    internal static bool ContainsWholeWord(string text, string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return false;
        }

        var index = 0;
        while ((index = text.IndexOf(word, index, StringComparison.Ordinal)) >= 0)
        {
            var end = index + word.Length;
            var startsWord = index == 0 || !IsWordChar(text[index - 1]);
            var endsWord = end == text.Length || !IsWordChar(text[end]);
            if (startsWord && endsWord)
            {
                return true;
            }

            index++;
        }

        return false;
    }

    private static bool IsWordChar(char c)
    {
        return c == '_' || char.IsAsciiLetterOrDigit(c);
    }

    /// <summary>
    /// Runs <see cref="EnsureAllVariablesListed"/> over the persisted page and saves it when changed.
    /// Never throws (except on cancellation): the page is already generated, so a failure here is a warning.
    /// </summary>
    public static async Task ApplyCompletenessPassAsync(
        IContextFactory contextFactory,
        string branchLanguageId,
        string catalogPath,
        EnvironmentScanResult scanResult,
        string? languageCode,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            using var context = contextFactory.CreateContext();
            var docFileId = await context.DocCatalogs
                .AsNoTracking()
                .Where(c => c.BranchLanguageId == branchLanguageId && c.Path == catalogPath && !c.IsDeleted)
                .Select(c => c.DocFileId)
                .FirstOrDefaultAsync(cancellationToken);
            if (string.IsNullOrEmpty(docFileId))
            {
                return;
            }

            var docFile = await context.DocFiles
                .FirstOrDefaultAsync(d => d.Id == docFileId && !d.IsDeleted, cancellationToken);
            if (docFile is null)
            {
                return;
            }

            var updated = EnsureAllVariablesListed(docFile.Content, scanResult, languageCode);
            if (string.Equals(updated, docFile.Content, StringComparison.Ordinal))
            {
                return;
            }

            docFile.Content = updated;
            docFile.UpdateTimestamp();
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation(
                "Environment page completeness pass appended names the model did not describe. Path: {Path}",
                catalogPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Environment page completeness pass failed. Path: {Path}", catalogPath);
        }
    }

    private static string NormalizeLanguage(string? languageCode)
    {
        return (languageCode ?? string.Empty).Trim().ToLowerInvariant();
    }

    private sealed record PageLabels(
        string VariablesHeading,
        string SecretsHeading,
        string NotEstablished,
        string MissingHeading,
        string Name,
        string Secret,
        string Locations,
        string Purpose,
        string Yes,
        string No,
        string PurposeNotEstablished)
    {
        private static readonly PageLabels Russian = new(
            "Переменные окружения",
            "Секреты",
            "не установлено",
            "Не описано при генерации",
            "Имя",
            "Секрет?",
            "Места",
            "Назначение",
            "да",
            "нет",
            "назначение не установлено");

        private static readonly PageLabels Chinese = new(
            "环境变量",
            "密钥",
            "未确定",
            "生成时未描述",
            "名称",
            "密钥？",
            "位置",
            "用途",
            "是",
            "否",
            "用途未确定");

        private static readonly PageLabels English = new(
            "Environment variables",
            "Secrets",
            "not established",
            "Not described during generation",
            "Name",
            "Secret?",
            "Locations",
            "Purpose",
            "yes",
            "no",
            "purpose not established");

        public static PageLabels For(string? languageCode)
        {
            var code = NormalizeLanguage(languageCode);
            if (code == "ru")
            {
                return Russian;
            }

            return code == "zh" || code.StartsWith("zh-", StringComparison.Ordinal) ? Chinese : English;
        }
    }
}
