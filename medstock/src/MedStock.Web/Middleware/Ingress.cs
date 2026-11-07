using System.Net;

namespace MedStock.Web.Middleware;

public sealed class AppMode(bool addonMode)
{
    /// <summary>Running as a Home Assistant add-on (SUPERVISOR_TOKEN present).</summary>
    public bool AddonMode { get; } = addonMode;
}

public static class IngressMiddleware
{
    public static readonly IPAddress SupervisorIp = IPAddress.Parse("172.30.32.2");
    public const string DevIngressPath = "/api/hassio_ingress/dev";

    /// <summary>In add-on mode only the Supervisor ingress proxy may talk to us (plus localhost for /healthz).</summary>
    public static IApplicationBuilder UseIngressGate(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var ip = ctx.Connection.RemoteIpAddress?.MapToIPv4();
        bool allowed = ip is not null && (ip.Equals(SupervisorIp) ||
            (IPAddress.IsLoopback(ip) && ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase)));
        if (!allowed)
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next();
    });

    /// <summary>Ingress already strips the prefix; we only need to tell ASP.NET Core the base path for generated URLs.</summary>
    public static IApplicationBuilder UseIngressPathBase(this IApplicationBuilder app, bool development) => app.Use(async (ctx, next) =>
    {
        if (development && !ctx.Request.Headers.ContainsKey("X-Ingress-Path"))
        {
            // Simulate ingress locally: accept URLs with and without the prefix so prefix bugs show up in generated links.
            ctx.Request.Headers["X-Ingress-Path"] = DevIngressPath;
            if (ctx.Request.Path.StartsWithSegments(DevIngressPath, out var rest))
                ctx.Request.Path = rest;
        }
        var prefix = ctx.Request.Headers["X-Ingress-Path"].ToString().TrimEnd('/');
        if (prefix.Length > 0 && prefix.StartsWith('/') && !prefix.Contains(".."))
            ctx.Request.PathBase = new PathString(prefix);
        await next();
    });
}
