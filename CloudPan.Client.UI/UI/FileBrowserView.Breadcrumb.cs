using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>FileBrowserView 部分类：面包屑导航栏重建、路径段链接与分隔符。</summary>
public partial class FileBrowserView
{
    // ================================================================
    // 面包屑
    // ================================================================

    /// <summary>重建面包屑导航：仅保留「上一级」按钮，按当前路径生成可点击的路径段。</summary>
    private void RebuildBreadcrumb(string path)
    {
        _breadcrumbBar.SuspendLayout();
        // 移除并释放「上一级」之外的全部动态面包屑控件（索引 ≥1）
        for (int i = _breadcrumbBar.Controls.Count - 1; i >= 1; i--)
        {
            Control c = _breadcrumbBar.Controls[i];
            _breadcrumbBar.Controls.RemoveAt(i);
            c.Dispose();
        }

        _breadcrumbBar.Controls.Add(_upButton);

        AddBreadcrumbLink("主目录", "/");
        string p = path.Trim('/');
        if (p.Length > 0)
        {
            string acc = "";
            foreach (string seg in p.Split('/'))
            {
                acc += "/" + seg;
                _breadcrumbBar.Controls.Add(CreateBreadcrumbSeparator());
                AddBreadcrumbLink(seg, acc);
            }
        }

        _breadcrumbBar.ResumeLayout();
    }

    /// <summary>添加一个可点击的面包屑段（Tag 存目标路径）。</summary>
    private void AddBreadcrumbLink(string text, string path)
    {
        Button link = new Button
        {
            Text = text,
            Tag = path,
            AutoSize = true,
            Height = CloudPanSpacing.MinTouchSize,
            FlatStyle = FlatStyle.Flat,
        };
        link.FlatAppearance.BorderColor = CloudPanColors.BorderLight;
        link.Click += BreadcrumbButton_Click;
        _breadcrumbBar.Controls.Add(link);
    }

    /// <summary>创建面包屑段之间的分隔符。</summary>
    private static Label CreateBreadcrumbSeparator()
    {
        return new Label
        {
            Text = "›",
            AutoSize = true,
            TextAlign = ContentAlignment.MiddleCenter,
            Height = CloudPanSpacing.MinTouchSize,
            Margin = new Padding(2, 0, 2, 0),
            ForeColor = CloudPanColors.TextMuted,
            Font = new Font((SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont).FontFamily, 12f),
        };
    }

    private void BreadcrumbButton_Click(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.Tag is string path)
        {
            DirectoryActivated?.Invoke(path);
        }
    }
}
