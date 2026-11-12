using AllInOnePdf.Core.Conversion;

namespace AllInOnePdf.UI.Services;

/// <summary>
/// Keeps <see cref="ConversionQueue.MaxParallel"/> in step with <see cref="UserPreferences.MaxParallelConversions"/>:
/// applied when preferences load (start-up) and whenever they change. Singleton, like the queue and preferences;
/// the shell and the Convert workspace resolve it so it exists before the first conversion.
/// </summary>
public sealed class ConversionQueueTuner : IDisposable
{
    private readonly IPreferencesService _preferences;
    private readonly ConversionQueue _queue;

    public ConversionQueueTuner(IPreferencesService preferences, ConversionQueue queue)
    {
        _preferences = preferences;
        _queue = queue;
        _preferences.Changed += Apply;
        Apply();
    }

    /// <summary>Applies the current preference to the queue (no-op when unchanged).</summary>
    public void Apply()
    {
        var wanted = Math.Clamp(_preferences.Current.MaxParallelConversions, 1, 8);
        if (_queue.MaxParallel != wanted)
        {
            _queue.MaxParallel = wanted;
        }
    }

    public void Dispose() => _preferences.Changed -= Apply;
}
