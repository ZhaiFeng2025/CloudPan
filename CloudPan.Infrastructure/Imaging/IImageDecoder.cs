namespace CloudPan.Infrastructure.Imaging;

/// <summary>
/// 解码后的位图数据（BGRA8 像素 + 尺寸），供领域层（Server.Core）构造缩略图。
/// 领域层只依赖 <see cref="IImageDecoder"/> 抽象，不依赖具体解码后端（WIC/Skia）。
/// </summary>
public sealed record DecodedBitmap(int Width, int Height, byte[] BgraPixels);

/// <summary>
/// 图片解码器抽象。解码后端（Windows 系统 WIC）在 Infrastructure 实现，
/// 保持 Server.Core 不依赖 WPF/WinRT/System.Drawing 等具体类型（T-102 解码器落 Infrastructure）。
/// </summary>
public interface IImageDecoder
{
    /// <summary>解码图片文件为 BGRA8 位图像素；无法解码（文件损坏/系统无对应解码器）返回 null。</summary>
    DecodedBitmap? TryDecode(string absolutePath);
}
