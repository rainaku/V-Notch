using VNotch.Controls;

namespace VNotch;

public partial class MainWindow
{
    private MarqueeController? _marqueeController;

    private MarqueeController Marquee => _marqueeController ??= new MarqueeController(
        TrackTitle, TitleMarqueeTranslate, TitleMorphTranslate,
        TrackTitleNext, TitleMarqueeTranslateNext, TitleMorphTranslateNext,
        TrackArtist, ArtistMarqueeTranslate, ArtistMorphTranslate,
        TrackArtistNext, ArtistMarqueeTranslateNext, ArtistMorphTranslateNext,
        CompactTitleMarquee, CompactTitleMarqueeTranslate,
        GetVisibleMediaTextWidth);

    private void RefreshMediaMarquee() => Marquee.RefreshMediaMarquee();
    private void UpdateTitleText(string newText) => Marquee.UpdateTitleText(newText);
    private void UpdateArtistText(string newText) => Marquee.UpdateArtistText(newText);
}

