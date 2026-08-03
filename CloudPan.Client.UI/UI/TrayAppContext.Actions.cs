using CloudPan.Client.Core.Models;
using Microsoft.Win32;

namespace CloudPan.Client.UI;

/// <summary>TrayAppContext 部分类：应用操作——打开文件夹/日志目录、开机自启注册表、设置窗口与退出。</summary>
public partial class TrayAppContext
{
    private void OpenFolder()
    {
        try { System.Diagnostics.Process.Start("explorer.exe", Program.SyncRoot); }
        catch (Exception ex) { Console.Error.WriteLine($"打开文件夹失败: {ex.Message}"); }
    }

    private void OpenLogDir()
    {
        string logDir = Path.Combine(Program.SyncRoot, ".cloudpan", "logs");
        try
        {
            if (Directory.Exists(logDir))
            {
                System.Diagnostics.Process.Start("explorer.exe", logDir);
            }
            else
            {
                MessageBox.Show("日志目录尚不存在，将在首次同步后生成。", "CloudPan",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex) { Console.Error.WriteLine($"打开日志目录失败: {ex.Message}"); }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false);
            return key?.GetValue("CloudPan") != null;
        }
        catch { return false; }
    }

    private static void SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;
            if (enable)
            {
                key.SetValue("CloudPan", $"\"{Application.ExecutablePath}\"");
            }
            else
            {
                key.DeleteValue("CloudPan", false);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"设置开机自启失败: {ex.Message}"); }
    }

    private void OpenSettings()
    {
        try
        {
            ClientConfig cfg = ClientConfig.Load(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "CloudPan", "client-config.json"));
            SettingsForm form = new SettingsForm(
                Program.ServerUrl, Program.SyncRoot, Program.Token,
                cfg.UploadLimitBps, cfg.DownloadLimitBps, cfg.SelectedPaths);
            if (form.ShowDialog() == DialogResult.OK)
            {
                cfg.ServerUrl = form.ServerUrl;
                cfg.SyncRoot = form.SyncRoot;
                cfg.TokenEncrypted = Convert.ToBase64String(
                    System.Security.Cryptography.ProtectedData.Protect(
                        System.Text.Encoding.UTF8.GetBytes(form.Token), null,
                        System.Security.Cryptography.DataProtectionScope.CurrentUser));
                cfg.UploadLimitBps = form.UploadLimitBps;
                cfg.DownloadLimitBps = form.DownloadLimitBps;
                cfg.SelectedPaths = form.SelectedPaths;
                cfg.Save(Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "CloudPan", "client-config.json"));
                MessageBox.Show("设置已保存。部分更改需要重启客户端后生效。",
                    "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"设置保存失败:\n{ex.Message}\n\n请检查磁盘空间和写入权限。",
                "CloudPan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void Exit()
    {
        Program.IsOffline = true;
        _cts.Cancel();
        _trayIcon.Visible = false;
        Application.Exit();
    }
}
