namespace OpenDeepWiki.Infrastructure;

/// <summary>
/// NzrAiWiki: only admins may start wiki generation.
/// The MiniApi source generator ignores [Authorize] on service methods, so the generated
/// repository routes are guarded here by their route pattern. Hand-written minimal API
/// endpoints use .RequireAuthorization("AdminOnly") directly instead.
/// </summary>
public static class AdminOnlyGenerationRoutes
{
    private static readonly HashSet<string> GuardedPostRoutes = new(StringComparer.OrdinalIgnoreCase)
    {
        "/api/v1/repositories/submit",
        "/api/v1/repositories/submit-local",
        "/api/v1/repositories/submitarchive",
        "/api/v1/repositories/regenerate",
    };

    public static bool IsGuarded(string method, string? routePattern) =>
        HttpMethods.IsPost(method)
        && routePattern is not null
        && GuardedPostRoutes.Contains(routePattern.TrimEnd('/'));

    public static IApplicationBuilder UseAdminOnlyGenerationRoutes(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            if (IsGuarded(context.Request.Method, pattern))
            {
                if (context.User.Identity?.IsAuthenticated != true)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                if (!context.User.IsInRole("Admin"))
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    await context.Response.WriteAsJsonAsync(new
                    {
                        success = false,
                        message = "Only administrators can add or regenerate repositories."
                    });
                    return;
                }
            }

            await next();
        });
}
