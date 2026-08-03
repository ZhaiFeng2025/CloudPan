using CloudPan.Server.Core;

namespace CloudPan.Server.UI;

/// <summary>SettingsPage 部分类：同步根变更/连接钥匙轮换的影响面确认与重配引导（F-34/T-034）。</summary>
public partial class SettingsPage
{
    /// <summary>
    /// 查询已配对设备并生成白话影响面摘要（F-34/T-034）：改同步根/轮换 Token 时，所有设备都需重配。
    /// 文案使用家庭场景白话——Token 称「连接钥匙」、服务器地址称「家庭服务器地址」。
    /// </summary>
    private async Task<string> BuildDeviceImpactAsync()
    {
        try
        {
            List<DeviceInfo> devices = await _statusService.GetDevicesAsync();
            if (devices.Count == 0)
            {
                return "暂无已配对的设备";
            }
            string names = string.Join("、", devices
                .Take(8)
                .Select(d => string.IsNullOrWhiteSpace(d.Name) ? d.Id : d.Name));
            if (devices.Count > 8)
            {
                names += " 等";
            }
            return $"已配对设备 {devices.Count} 台（{names}）";
        }
        catch (Exception ex)
        {
            _log($"读取设备清单失败: {ex.Message}");
            return "已配对设备清单读取失败";
        }
    }

    /// <summary>同步根变更确认：列出影响面 + 明确不迁移 + 重配引导。返回 DialogResult。</summary>
    private DialogResult ConfirmSyncRootChange(string impact)
    {
        return MessageBox.Show(
            $"更改同步根目录后，新目录将从空开始重新同步（全新数据库、全新连接钥匙 Token）。\n\n" +
            $"【影响面】{impact}。所有设备都需要用新的家庭服务器地址和新的连接钥匙重新配置，否则会全部静默失联。\n\n" +
            "【不迁移】旧目录中的 .cloudpan（数据库/版本历史/连接钥匙）不会被复制到新目录，也不会被删除。\n\n" +
            "【重配引导】保存并重启服务后，在本页的「连接钥匙」区域点击「复制」按钮获取新的连接钥匙（Token），" +
            "连同新的服务器地址一起填到每台设备，即可恢复同步。\n\n" +
            "确定继续吗？",
            "更改同步根目录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
    }

    /// <summary>连接钥匙轮换确认：列出影响面（所有已配对设备需重配）。返回 DialogResult。</summary>
    private DialogResult ConfirmTokenRotation(string impact)
    {
        return MessageBox.Show(
            $"将重新生成家庭共享「连接钥匙」（Token）。{impact}。\n\n" +
            "所有设备都需要用新的连接钥匙重新配置连接，否则会全部失联。\n\n" +
            "确定继续吗？",
            "轮换连接钥匙", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
    }
}
