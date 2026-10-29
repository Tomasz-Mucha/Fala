using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Fala.Helpers;

public static class Artwork
{
    public static ImageSource? FromUrl(string? url) => Create(url, 160);

    public static ImageSource? FromUrlLarge(string? url) => Create(url, 280);

    private static ImageSource? Create(string? url, int decodePx)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return null;
        if (uri.Scheme is not ("http" or "https" or "file"))
            return null;
        return new BitmapImage(uri) { DecodePixelWidth = decodePx };
    }
}

public static class Vis
{
    public static Visibility If(bool value)
        => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility IfNot(bool value)
        => value ? Visibility.Collapsed : Visibility.Visible;
}
