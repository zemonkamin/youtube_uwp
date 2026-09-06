using System;
using System.Collections.Generic;
using System.Threading.Tasks;

// Non-critical enrichment is kept out of Config.cs and, more importantly, out of the first
// render path. The helper observes all failures so callers can safely display parsed cards first.
public static partial class Config
{
    private static async Task HydrateChannelThumbnailsSafeAsync(
        List<VideoCardItem> videos,
        string accessToken)
    {
        try
        {
            await HydrateMissingChannelThumbnailsAsync(videos, accessToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                "[Config] Deferred channel thumbnail hydration failed: " + ex.Message);
        }
    }
}
