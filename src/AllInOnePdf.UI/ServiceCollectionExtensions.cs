using AllInOnePdf.Core;
using AllInOnePdf.Publishing;
using AllInOnePdf.UI.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.FluentUI.AspNetCore.Components;

namespace AllInOnePdf.UI;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers Fluent UI, the Core conversion services and the UI state services.
    /// Hosts register their own <see cref="IHostServices"/> BEFORE calling this; otherwise the browser implementation is used.
    /// </summary>
    public static IServiceCollection AddAllInOnePdfUI(this IServiceCollection services, string? dataDirectory = null)
    {
        services.AddFluentUIComponents();
        services.AddAllInOnePdfCore(dataDirectory);

        services.TryAddScoped<AppState>();
        services.TryAddSingleton<IPreferencesService, FilePreferencesService>();
        services.TryAddScoped<IHostServices, BrowserHostServices>();
        // Shell and settings
        services.TryAddScoped<ThemeService>();
        services.TryAddSingleton<ConversionQueueTuner>();

        // Convert workspace
        services.TryAddScoped<ConvertWorkspace>();

        // Reader
        services.TryAddScoped<ReaderSession>();

        // Themes gallery and builder
        services.TryAddScoped<ThemeBuilderState>();
        services.TryAddScoped<ThemeBuilderSamples>();
        services.TryAddSingleton<ThemeBuilderThumbnailCache>();

        // Creator (publications)
        services.AddAllInOnePdfPublishing();
        services.TryAddScoped<CreatorState>();
        services.TryAddSingleton<CreatorDrafts>();
        services.TryAddSingleton<CreatorThumbnailCache>();
        return services;
    }
}
