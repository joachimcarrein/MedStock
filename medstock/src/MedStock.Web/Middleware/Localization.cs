using System.Globalization;
using MedStock.Web.Data;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace MedStock.Web.Middleware;

public static class LocalizationSetup
{
    public static readonly CultureInfo[] UiCultures = [new("en"), new("nl")];

    public static string LanguageCacheKey(string userId) => "lang:" + userId;

    public static RequestLocalizationOptions Options()
    {
        var o = new RequestLocalizationOptions
        {
            // Numbers are always parsed/formatted invariant; only the UI culture (texts, dates) varies.
            SupportedCultures = [CultureInfo.InvariantCulture],
            SupportedUICultures = UiCultures,
            FallBackToParentUICultures = true,
            FallBackToParentCultures = true,
            DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture, new CultureInfo("en")),
        };
        o.RequestCultureProviders =
        [
            new CustomRequestCultureProvider(async ctx =>
            {
                var user = ctx.RequestServices.GetService<CurrentUser>();
                if (user is null) return null;
                var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
                var lang = await cache.GetOrCreateAsync(LanguageCacheKey(user.Id), async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
                    var db = ctx.RequestServices.GetRequiredService<MedStockDb>();
                    return await db.UserSettings.Where(s => s.UserId == user.Id).Select(s => s.Language).FirstOrDefaultAsync();
                });
                return string.IsNullOrEmpty(lang) ? null : new ProviderCultureResult("", lang);
            }),
            new AcceptLanguageHeaderRequestCultureProvider(),
        ];
        return o;
    }
}

public static class LastCultureMiddleware
{
    /// <summary>
    /// Remembers the UI culture of each user's requests (at most one write per user per change) so background notifications
    /// use the language the user actually sees when their language setting is "automatic".
    /// </summary>
    public static IApplicationBuilder UseLastCulture(this IApplicationBuilder app) => app.Use(async (ctx, next) =>
    {
        var user = ctx.RequestServices.GetService<CurrentUser>();
        if (user is not null && !string.IsNullOrEmpty(ctx.Request.Headers.AcceptLanguage) && ctx.Request.Method == "GET")
        {
            var culture = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            var cache = ctx.RequestServices.GetRequiredService<IMemoryCache>();
            var key = "seen-culture:" + user.Id;
            if (cache.Get<string>(key) != culture)
            {
                var db = ctx.RequestServices.GetRequiredService<MedStockDb>();
                await db.UserSettings.Where(s => s.UserId == user.Id && s.LastCulture != culture)
                    .ExecuteUpdateAsync(u => u.SetProperty(s => s.LastCulture, culture), ctx.RequestAborted);
                cache.Set(key, culture, TimeSpan.FromHours(12));
            }
        }
        await next();
    });
}

/// <summary>Accepts both "0.5" and "0,5" for decimals, regardless of the UI language.</summary>
public sealed class InvariantDecimalBinder : IModelBinder
{
    public Task BindModelAsync(ModelBindingContext ctx)
    {
        var value = ctx.ValueProvider.GetValue(ctx.ModelName);
        if (value == ValueProviderResult.None) return Task.CompletedTask;
        ctx.ModelState.SetModelValue(ctx.ModelName, value);
        var raw = value.FirstValue?.Trim();
        if (string.IsNullOrEmpty(raw))
        {
            if (Nullable.GetUnderlyingType(ctx.ModelType) is not null) ctx.Result = ModelBindingResult.Success(null);
            return Task.CompletedTask;
        }
        if (decimal.TryParse(raw.Replace(',', '.'), NumberStyles.Number & ~NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var d))
            ctx.Result = ModelBindingResult.Success(d);
        else
            ctx.ModelState.TryAddModelError(ctx.ModelName, ctx.ModelMetadata.ModelBindingMessageProvider.AttemptedValueIsInvalidAccessor(raw, ctx.ModelMetadata.GetDisplayName()));
        return Task.CompletedTask;
    }
}

public sealed class InvariantDecimalBinderProvider : IModelBinderProvider
{
    public IModelBinder? GetBinder(ModelBinderProviderContext context) =>
        context.Metadata.ModelType == typeof(decimal) || context.Metadata.ModelType == typeof(decimal?)
            ? new InvariantDecimalBinder() : null;
}
