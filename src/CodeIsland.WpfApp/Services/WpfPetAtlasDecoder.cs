using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp.PixelFormats;

namespace CodeIsland.WpfApp.Services;

internal static class WpfPetAtlasDecoder
{
    private const int AtlasWidth = 1536;
    private const int AtlasV1Height = 1872;
    private const int AtlasV2Height = 2288;

    public static WpfDecodedPetAtlas Decode(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var info = SixLabors.ImageSharp.Image.Identify(stream);
            if (info.Width != AtlasWidth || info.Height is not (AtlasV1Height or AtlasV2Height))
                throw new InvalidDataException($"精灵图尺寸必须为 {AtlasWidth}×{AtlasV1Height} 或 {AtlasWidth}×{AtlasV2Height}");

            stream.Position = 0;
            using var image = SixLabors.ImageSharp.Image.Load<Bgra32>(stream);
            var stride = checked(image.Width * 4);
            var pixels = new byte[checked(stride * image.Height)];
            image.CopyPixelDataTo(pixels);
            var bitmap = BitmapSource.Create(
                image.Width,
                image.Height,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                pixels,
                stride);
            bitmap.Freeze();
            return new WpfDecodedPetAtlas(bitmap, pixels);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidDataException($"无法解码精灵图：{ex.Message}", ex);
        }
    }
}

internal sealed record WpfDecodedPetAtlas(BitmapSource Bitmap, byte[] BgraPixels);
