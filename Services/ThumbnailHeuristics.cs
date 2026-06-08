using System.Collections.Generic;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VNotch.Services;

/// <summary>
/// Pure image-shape/quality heuristics extracted from <see cref="MediaDetectionService"/>, used to
/// distinguish real SoundCloud artwork from placeholder/avatar thumbnails. The logic is deterministic
/// from the supplied <see cref="BitmapImage"/> (dimensions + pixels), so it is unit-testable with
/// in-memory bitmap fixtures.
/// </summary>
internal static class ThumbnailHeuristics
{
    /// <summary>True when the thumbnail looks like a SoundCloud placeholder (missing, tiny square, or low-entropy monochrome square).</summary>
    public static bool IsLikelyPlaceholderThumbnail(BitmapImage? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return true;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.06;
        if (!isSquare)
        {
            return false;
        }

        if (thumbnail.PixelWidth <= 320 || thumbnail.PixelHeight <= 320)
        {
            return true;
        }

        return HasLowEntropyMonochromeProfile(thumbnail);
    }

    /// <summary>True when the thumbnail is a plausible real artwork candidate (large enough square that is not a placeholder).</summary>
    public static bool IsLikelyArtworkCandidate(BitmapImage? thumbnail)
    {
        if (thumbnail == null || thumbnail.PixelWidth <= 0 || thumbnail.PixelHeight <= 0)
        {
            return false;
        }

        double aspect = (double)thumbnail.PixelWidth / thumbnail.PixelHeight;
        bool isSquare = Math.Abs(aspect - 1.0) < 0.08;
        if (!isSquare)
        {
            return false;
        }

        return thumbnail.PixelWidth >= 360 &&
               thumbnail.PixelHeight >= 360 &&
               !IsLikelyPlaceholderThumbnail(thumbnail);
    }

    /// <summary>
    /// Samples the image (downscaled) and reports whether it is low-entropy and near-monochrome —
    /// the signature of a flat placeholder tile rather than real cover art.
    /// </summary>
    public static bool HasLowEntropyMonochromeProfile(BitmapImage thumbnail)
    {
        try
        {
            int sampleSize = 24;
            double scale = Math.Min(
                1.0,
                (double)sampleSize / Math.Max(thumbnail.PixelWidth, thumbnail.PixelHeight));

            BitmapSource source = thumbnail;
            if (scale < 1.0)
            {
                var transformed = new TransformedBitmap(thumbnail, new ScaleTransform(scale, scale));
                transformed.Freeze();
                source = transformed;
            }

            var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            formatted.Freeze();

            int width = Math.Max(1, formatted.PixelWidth);
            int height = Math.Max(1, formatted.PixelHeight);
            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            formatted.CopyPixels(pixels, stride, 0);

            var quantizedBins = new HashSet<int>();
            double saturationSum = 0;
            int nearGrayCount = 0;
            int brightCount = 0;
            int pixelCount = width * height;

            for (int i = 0; i < pixels.Length; i += 4)
            {
                byte b = pixels[i];
                byte g = pixels[i + 1];
                byte r = pixels[i + 2];

                int max = Math.Max(r, Math.Max(g, b));
                int min = Math.Min(r, Math.Min(g, b));
                int delta = max - min;

                if (delta <= 12) nearGrayCount++;
                if (max >= 220 && min >= 220) brightCount++;

                quantizedBins.Add(((r >> 5) << 6) | ((g >> 5) << 3) | (b >> 5));
                saturationSum += max == 0 ? 0 : (double)delta / max;
            }

            double avgSaturation = saturationSum / pixelCount;
            double grayRatio = (double)nearGrayCount / pixelCount;
            double brightRatio = (double)brightCount / pixelCount;

            return quantizedBins.Count <= 18 &&
                   avgSaturation <= 0.08 &&
                   grayRatio >= 0.84 &&
                   brightRatio >= 0.02;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// What the caller should do with an SMTC-provided thumbnail bitmap on a given pass:
    /// reject it as stale, accept it (crop + cache), or skip it (leave the current thumbnail untouched).
    /// </summary>
    public enum SmtcThumbnailDecision
    {
        /// <summary>Stale/wrong frame for a fresh track — discard so a verified lookup can fill it in.</summary>
        Reject,
        /// <summary>Good enough to display — caller should crop, cache and apply it.</summary>
        Accept,
        /// <summary>Generic icon or a case where a verified lookup is preferred — leave things as they are.</summary>
        Skip,
    }

    /// <summary>
    /// Resolved inputs for <see cref="DecideSmtcThumbnail"/>. The caller resolves platform flags,
    /// cached-thumbnail state, the SoundCloud-artwork heuristic and timing into plain values; the
    /// decision itself is pure and deterministic from the bitmap dimensions plus these flags.
    /// </summary>
    public readonly struct SmtcThumbnailInputs
    {
        public bool IsYouTubeLikeSource { get; init; }
        public bool IsSoundCloudSource { get; init; }
        /// <summary>Platform is YouTube or Browser (the generic-icon candidates, alongside a SoundCloud placeholder).</summary>
        public bool IsBrowserOrYouTubePlatform { get; init; }
        public bool TrackChanged { get; init; }
        public bool HasVerifiedYouTubeThumb { get; init; }
        public bool HasVerifiedSoundCloudThumb { get; init; }
        public bool LikelySoundCloudArtwork { get; init; }
        /// <summary>The metadata changed within the last few seconds (SMTC thumbnails can lag a track change by multiple cycles).</summary>
        public bool RecentTrackChange { get; init; }
        public bool CachedThumbnailIsNull { get; init; }
        public int PixelWidth { get; init; }
        public int PixelHeight { get; init; }
    }

    /// <summary>
    /// Decides what to do with a freshly-decoded SMTC thumbnail. Extracted verbatim from
    /// <see cref="MediaDetectionService"/>'s session-thumbnail handler so the rule is unit-testable:
    /// <list type="bullet">
    /// <item>Reject a wide video-frame (or non-artwork SoundCloud) thumbnail on/just after a track change.</item>
    /// <item>Skip a small square "generic icon" (browser favicon / SoundCloud placeholder), or when a
    /// verified YouTube lookup is preferred for an unchanged track.</item>
    /// <item>Otherwise accept it.</item>
    /// </list>
    /// </summary>
    public static SmtcThumbnailDecision DecideSmtcThumbnail(in SmtcThumbnailInputs x)
    {
        bool skipSmtcThumbForFreshSoundCloudTrack = x.IsSoundCloudSource &&
                                                    x.TrackChanged &&
                                                    !x.HasVerifiedSoundCloudThumb &&
                                                    !x.LikelySoundCloudArtwork;

        double aspect = x.PixelHeight == 0 ? 0 : (double)x.PixelWidth / x.PixelHeight;
        bool isWideVideoFrame = aspect > 1.3;
        bool skipSmtcThumbForFreshYouTubeTrack = x.IsYouTubeLikeSource &&
                                                 isWideVideoFrame &&
                                                 (x.TrackChanged || (x.RecentTrackChange && x.CachedThumbnailIsNull));

        if (skipSmtcThumbForFreshSoundCloudTrack || skipSmtcThumbForFreshYouTubeTrack)
        {
            return SmtcThumbnailDecision.Reject;
        }

        bool isSquare = Math.Abs(aspect - 1.0) < 0.05;
        bool isLikelySoundCloudPlaceholder = x.IsSoundCloudSource &&
                                             isSquare &&
                                             (x.PixelWidth <= 320 || x.PixelHeight <= 320);
        bool isGenericIcon = (x.IsBrowserOrYouTubePlatform || isLikelySoundCloudPlaceholder) &&
                             isSquare &&
                             x.PixelWidth <= 300;
        bool shouldPreferVerifiedYouTubeLookup = x.IsYouTubeLikeSource &&
                                                 !x.HasVerifiedYouTubeThumb &&
                                                 !x.TrackChanged;

        if (!(isSquare && isGenericIcon) && !shouldPreferVerifiedYouTubeLookup)
        {
            return SmtcThumbnailDecision.Accept;
        }

        return SmtcThumbnailDecision.Skip;
    }
}
