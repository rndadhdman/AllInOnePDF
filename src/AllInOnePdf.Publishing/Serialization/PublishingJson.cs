using System.Text.Json;
using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.Publishing.Serialization;

/// <summary>
/// <see cref="CoreJson.Options"/> plus <see cref="JsonSerializerOptions.AllowOutOfOrderMetadataProperties"/>, so a
/// hand-edited module whose <c>type</c> is not the first property still reads. Output is identical to CoreJson.
/// </summary>
public static class PublishingJson
{
    /// <summary>Serializer settings for publications: the shared Core settings plus out-of-order <c>type</c> discriminators.</summary>
    public static JsonSerializerOptions Options { get; } = new(CoreJson.Options) { AllowOutOfOrderMetadataProperties = true };
}
