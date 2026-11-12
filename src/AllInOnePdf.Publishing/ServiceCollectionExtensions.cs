using AllInOnePdf.Core.Rendering;
using AllInOnePdf.Publishing.Rendering;
using AllInOnePdf.Publishing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AllInOnePdf.Publishing;

/// <summary>DI registration for the Creator's services.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the publication store and the publication renderer. Call after <c>AddAllInOnePdfCore</c>, which
    /// provides <c>AppPaths</c>, logging and the syntax highlighter.
    /// </summary>
    public static IServiceCollection AddAllInOnePdfPublishing(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IPublicationStore, FilePublicationStore>();
        services.TryAddSingleton<IPublicationRenderer>(sp => new PublicationRenderer(sp.GetService<ISyntaxHighlighter>()));
        return services;
    }
}
