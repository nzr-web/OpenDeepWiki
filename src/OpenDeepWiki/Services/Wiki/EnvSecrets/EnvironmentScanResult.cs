namespace OpenDeepWiki.Services.Wiki.EnvSecrets;

/// <summary>
/// One environment variable or configuration key found by <see cref="EnvironmentReferenceScanner"/>.
/// Secret values are never stored here: <see cref="DefaultValue"/> is always null for secrets.
/// </summary>
internal sealed class EnvironmentVariableReference
{
    /// <summary>
    /// Name as found. A .NET configuration key is shown in its colon form (<c>AI:Endpoint</c>).
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Environment-variable spelling when it differs from <see cref="Name"/> (<c>AI__Endpoint</c>).
    /// </summary>
    public string? EnvName { get; init; }

    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>
    /// Up to <see cref="EnvironmentReferenceScanner.MaxLocations"/> places as <c>path:line</c>.
    /// </summary>
    public required IReadOnlyList<string> Locations { get; init; }

    public bool IsLikelySecret { get; init; }

    public string? SecretReason { get; init; }

    /// <summary>
    /// Default value from example files, <c>${X:-d}</c>-style expansions or <c>ENV X=d</c>.
    /// Only for non-secrets; always null for secrets.
    /// </summary>
    public string? DefaultValue { get; init; }

    /// <summary>
    /// Names this entry may appear under on a page.
    /// </summary>
    public IEnumerable<string> AllNames()
    {
        yield return Name;
        if (EnvName is not null)
        {
            yield return EnvName;
        }
    }

    /// <summary>
    /// Deliberately omits <see cref="DefaultValue"/>: nothing value-like leaves the record through logs.
    /// </summary>
    public override string ToString()
    {
        var env = EnvName is null ? string.Empty : $" ({EnvName})";
        return $"{Name}{env}, secret: {IsLikelySecret}, sources: [{string.Join(", ", Sources)}], locations: [{string.Join(", ", Locations)}]";
    }
}

/// <summary>
/// Result of a repository scan. <see cref="Failed"/> means the scan did not run to the end
/// and <see cref="Variables"/> is empty.
/// </summary>
internal sealed class EnvironmentScanResult
{
    public EnvironmentScanResult(IReadOnlyList<EnvironmentVariableReference> variables, bool failed)
    {
        Variables = variables;
        Failed = failed;
    }

    public IReadOnlyList<EnvironmentVariableReference> Variables { get; }

    public bool Failed { get; }

    public static EnvironmentScanResult FailedResult() => new([], failed: true);
}
