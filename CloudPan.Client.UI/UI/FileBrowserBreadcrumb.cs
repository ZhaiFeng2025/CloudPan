using CloudPan.Infrastructure.Design;

namespace CloudPan.Client.UI;

/// <summary>文件浏览面包屑协作类（T-109）：按当前路径重建导航段、路径段链接与分隔符。</summary>
internal sealed class FileBrowserBreadcrumb
{
    private readonly FileBrowserView _view;

    public FileBrowserBreadcrumb(FileBrowserView view)
    {
        _view = view;
    }

    /// <summary>重建面包屑导航：仅保留「上一级」按钮，按当前路径生成可点击的路径段。</summary>
    public void Rebuild(string path)
    {
        _view._breadcrumbBar.SuspendLayout();
        // 移除并释放「上一级」之外的全部动态面包屑控件（索引 ≥1）
        for (int i = _view._breadcrumbBar.Controls.Count - 1; i >= 1; i--)
        {
            Control c = _view._breadcrumbBar.Controls[i];
            _view._breadcrumbBar.Controls.RemoveAt(i);
            c.Dispose();
        }

        _view._breadcrumbBar.Controls.Add(_view._upButton);

        AddBreadcrumbLink("主目录", "/");
        string p = path.Trim('/');
        if (p.Length > 0)
        {
            string acc = "";
            foreach (string seg in p.Split('/'))
            {
                acc += "/" + seg;
                _view._breadcrumbBar.Controls.Add(CreateBreadcrumbSeparator());
                AddBreadcrumbLink(seg, acc);
            }
        }

        _view._breadcrumbBar.ResumeLayout();
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
        _view._breadcrumbBar.Controls.Add(link);
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
            _view.RaiseDirectoryActivated(path);
        }
    }
}
