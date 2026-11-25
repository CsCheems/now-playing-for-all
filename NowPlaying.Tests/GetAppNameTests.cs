using ChemitaDev.NowPlaying;
using Xunit;

public class GetAppNameTests
{
    // Real Windows ids keep their original capitalisation; this is what broke before.
    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "zunemusic")]
    [InlineData("Chrome", "chrome")]
    [InlineData("MSEdge", "microsoftedge")]
    [InlineData("Brave", "brave")]
    [InlineData("Firefox", "firefox")]
    [InlineData("AppleInc.iTunes_nzyj5cx40ttqa!iTunes", "itunes")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App", "applemusic")]
    public void Known_apps_get_a_short_name(string id, string expected)
        => Assert.Equal(expected, NowPlayingReader.GetAppName(id));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_id_is_unknown(string id)
        => Assert.Equal("Unknown", NowPlayingReader.GetAppName(id));

    [Fact]
    public void Unlisted_app_returns_its_lowercase_id()
        => Assert.Equal("some.otherapp", NowPlayingReader.GetAppName("Some.OtherApp"));
}
