using System.Runtime.InteropServices;

namespace CloudPan.Infrastructure.Imaging;

/// <summary>
/// 基于 Windows 系统 WIC（Windows Imaging Component，WindowsCodecs.dll）的图片解码器。
/// 直接调用 WIC COM 接口（IWICImagingFactory），Windows 10 1809+ 系统自带 HEIF/HEIC
/// 解码器（Windows 11 含 HEVC 压缩的 iPhone/现代 Android HEIC），无需第三方编解码器。
/// 解码后统一转换为 BGRA8 像素供领域层构造缩略图；失败（文件损坏/系统无对应解码器）返回 null。
/// </summary>
public sealed class WicImageDecoder : IImageDecoder
{
    // WIC 工厂线程安全（文档保证可跨线程使用），惰性单例避免每次解码重复 CoCreateInstance
    private static readonly Lazy<IWICImagingFactory> Factory =
        new(CreateFactory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <inheritdoc />
    public DecodedBitmap? TryDecode(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath))
        {
            return null;
        }

        try
        {
            IWICImagingFactory factory = Factory.Value;
            int hr = factory.CreateDecoderFromFilename(
                absolutePath, IntPtr.Zero, GenericRead, WicDecodeMetadataCacheOnDemand, out IntPtr pDecoder);
            if (hr != S_OK)
            {
                return null;
            }

            var decoder = (IWICBitmapDecoder)Marshal.GetObjectForIUnknown(pDecoder);
            try
            {
                hr = decoder.GetFrame(0, out IntPtr pFrame);
                if (hr != S_OK)
                {
                    return null;
                }

                var frame = (IWICBitmapFrameDecode)Marshal.GetObjectForIUnknown(pFrame);
                try
                {
                    hr = frame.GetSize(out int width, out int height);
                    if (hr != S_OK || width <= 0 || height <= 0)
                    {
                        return null;
                    }

                    // 统一转 BGRA8（JPEG 帧等通常输出 24bppBGR，需经 FormatConverter 转换）
                    hr = factory.CreateFormatConverter(out IntPtr pConverter);
                    if (hr != S_OK)
                    {
                        return null;
                    }

                    var converter = (IWICFormatConverter)Marshal.GetObjectForIUnknown(pConverter);
                    try
                    {
                        hr = converter.Initialize(frame, WicPixelFormat32bppBgra, WicBitmapDitherTypeNone,
                            IntPtr.Zero, 0.0, WicBitmapPaletteTypeCustom);
                        if (hr != S_OK)
                        {
                            return null;
                        }

                        int stride = width * 4;
                        byte[] pixels = new byte[stride * height];
                        // WIC CopyPixels 期望 LPBYTE（非托管缓冲），经 HGlobal 中转后拷回托管数组
                        nint buffer = Marshal.AllocHGlobal(pixels.Length);
                        try
                        {
                            hr = converter.CopyPixels(IntPtr.Zero, stride, pixels.Length, buffer);
                            if (hr != S_OK)
                            {
                                return null;
                            }

                            Marshal.Copy(buffer, pixels, 0, pixels.Length);
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(buffer);
                        }

                        return new DecodedBitmap(width, height, pixels);
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(converter);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(frame);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(decoder);
            }
        }
        catch
        {
            // WIC 解码失败（系统无对应解码器/文件损坏/COM 异常）：走失败路径返回 null
            return null;
        }
    }

    private static IWICImagingFactory CreateFactory()
    {
        // CLSID_WICImagingFactory（Windows 8+ 宏指向 WICImagingFactory2，实现老接口）
        Guid clsid = new("317d06e8-5f24-433d-bdf7-79ce68d8abc2");
        // IID_IWICImagingFactory
        Guid iid = new("ec5ec8a9-c395-4314-9c77-54d7a935ff70");
        int hr = CoCreateInstance(ref clsid, IntPtr.Zero, ClsctxInprocServer, ref iid, out IntPtr pFactory);
        if (hr != S_OK)
        {
            throw new InvalidOperationException($"WIC 工厂创建失败（HRESULT 0x{(uint)hr:X8}）");
        }

        return (IWICImagingFactory)Marshal.GetObjectForIUnknown(pFactory);
    }

    // ---- WIC 常量 ----
    private const int S_OK = 0;
    private const uint GenericRead = 0x80000000;
    private const int WicDecodeMetadataCacheOnDemand = 0;
    private const int WicBitmapDitherTypeNone = 0;
    private const int WicBitmapPaletteTypeCustom = 0;
    private const uint ClsctxInprocServer = 0x1;
    private static readonly Guid WicPixelFormat32bppBgra = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");

    [DllImport("ole32.dll")]
    private static extern int CoCreateInstance(ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext, ref Guid riid, out IntPtr ppv);

    // ---- WIC COM 接口（vtable 方法顺序与 wincodec.h 对齐；未使用的方法占位保持槽位）----

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string wzFilename, IntPtr pguidVendor, uint dwDesiredAccess, int metadataOptions, out IntPtr ppIDecoder);
        [PreserveSig] int CreateDecoderFromStream(IntPtr pIStream, IntPtr pguidVendor, int metadataOptions, out IntPtr ppIDecoder);
        [PreserveSig] int CreateDecoderFromFileHandle(IntPtr hFile, IntPtr pguidVendor, int metadataOptions, out IntPtr ppIDecoder);
        [PreserveSig] int CreateComponentInfo(IntPtr clsidComponent, out IntPtr ppIInfo);
        [PreserveSig] int CreateDecoder(IntPtr guidContainerFormat, IntPtr pguidVendor, out IntPtr ppIDecoder);
        [PreserveSig] int CreateEncoder(IntPtr guidContainerFormat, IntPtr pguidVendor, out IntPtr ppIEncoder);
        [PreserveSig] int CreatePalette(out IntPtr ppIPalette);
        [PreserveSig] int CreateFormatConverter(out IntPtr ppIFormatConverter);
        [PreserveSig] int CreateBitmapScaler(out IntPtr ppIBitmapScaler);
        [PreserveSig] int CreateBitmapClipper(out IntPtr ppIBitmapClipper);
        [PreserveSig] int CreateBitmapFlipRotator(out IntPtr ppIFlipRotator);
        [PreserveSig] int CreateStream(out IntPtr ppIWICStream);
        [PreserveSig] int CreateColorContext(out IntPtr ppIWICColorContext);
        [PreserveSig] int CreateColorTransformer(out IntPtr ppIWICColorTransform);
        [PreserveSig] int CreateBitmap(int width, int height, IntPtr pixelFormat, int option, out IntPtr ppIBitmap);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        [PreserveSig] int QueryCapability(IntPtr pIStream, out int pdwCapability);
        [PreserveSig] int Initialize(IntPtr pIStream, int cacheOptions);
        [PreserveSig] int GetContainerFormat(out Guid pguidContainerFormat);
        [PreserveSig] int GetDecoderInfo(out IntPtr ppIDecoderInfo);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr ppIMetadataQueryReader);
        [PreserveSig] int GetPreview(out IntPtr ppIBitmapSource);
        [PreserveSig] int GetColorContexts(int cCount, IntPtr ppIColorContexts, out int pcActualCount);
        [PreserveSig] int GetThumbnail(out IntPtr ppIThumbnail);
        [PreserveSig] int GetFrameCount(out int pCount);
        [PreserveSig] int GetFrame(int index, out IntPtr ppIBitmapFrame);
    }

    // IWICBitmapSource 方法 + IWICBitmapFrameDecode 追加方法（GetMetadataQueryReader/GetColorContexts/GetThumbnail）
    [ComImport, Guid("3B16811B-6A43-4ec9-A813-3D930C13B940")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode
    {
        [PreserveSig] int GetSize(out int puiWidth, out int puiHeight);
        [PreserveSig] int GetPixelFormat(out Guid pPixelFormat);
        [PreserveSig] int GetResolution(out double pDpiX, out double pDpiY);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        [PreserveSig] int CopyPixels(IntPtr prc, int cbStride, int cbBufferSize, IntPtr pbBuffer);
        [PreserveSig] int GetMetadataQueryReader(out IntPtr ppIMetadataQueryReader);
        [PreserveSig] int GetColorContexts(int cCount, IntPtr ppIColorContexts, out int pcActualCount);
        [PreserveSig] int GetThumbnail(out IntPtr ppIThumbnail);
    }

    // IWICBitmapSource 方法 + IWICFormatConverter 追加方法（Initialize/CanConvert/Convert）
    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter
    {
        [PreserveSig] int GetSize(out int puiWidth, out int puiHeight);
        [PreserveSig] int GetPixelFormat(out Guid pPixelFormat);
        [PreserveSig] int GetResolution(out double pDpiX, out double pDpiY);
        [PreserveSig] int CopyPalette(IntPtr pIPalette);
        [PreserveSig] int CopyPixels(IntPtr prc, int cbStride, int cbBufferSize, IntPtr pbBuffer);
        [PreserveSig] int Initialize(IWICBitmapFrameDecode pISource, Guid dstFormat, int dither, IntPtr pIPalette, double alphaThresholdPercent, int paletteTranslate);
        [PreserveSig] int CanConvert(Guid srcPixelFormat, Guid dstPixelFormat, out int pfCanConvert);
        [PreserveSig] int Convert(IntPtr pISource, IntPtr pIPalette, double alphaThresholdPercent, int paletteTranslate);
    }
}
