using System.Threading;
using System.Threading.Tasks;

namespace CloudPan.Contract;

/// <summary>
/// IApiClient 手工端点方法补充：GET /api/thumbnails 在 shared-spec.json 中未定义 clientMethod（T-087 收敛死功能时手工接入）。
/// 签名保留接口强约束（与 IApiClient.Config.cs 手工 partial 同模式），实现见 ApiClient.GetThumbnailAsync。
/// </summary>
public partial interface IApiClient
{
    /// <summary>获取图片缩略图（GET /api/thumbnails，返回 JPEG 字节）。失败返回 null，由调用方回退字体图标。</summary>
    Task<byte[]?> GetThumbnailAsync(string path, int width, CancellationToken ct = default);
}
