using System.ComponentModel;
using System.Diagnostics;

namespace RustDeskList;

internal sealed class AboutForm : Form
{
    private const string Repository = "github.com/ittehid/RustDeskList";
    private readonly Bitmap logo;

    public AboutForm(Icon appIcon)
    {
        Text = "О программе";
        Icon = appIcon;
        AutoScaleMode = AutoScaleMode.Dpi;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(405, 206);
        Font = new Font("Segoe UI", 10);
        BackColor = SystemColors.Window;

        using var largeIcon = new Icon(appIcon, new Size(64, 64));
        logo = largeIcon.ToBitmap();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(20, 16, 20, 14),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 13),
        };
        header.Controls.Add(new PictureBox
        {
            Image = logo,
            Size = new Size(56, 56),
            SizeMode = PictureBoxSizeMode.Zoom,
            Margin = new Padding(0, 0, 12, 0),
            TabStop = false,
        });
        var title = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            Margin = Padding.Empty,
        };
        title.Controls.Add(new Label
        {
            AutoSize = true,
            Text = Application.ProductName,
            Font = new Font(Font.FontFamily, 18, FontStyle.Bold),
            Margin = new Padding(0, 0, 0, 3),
        });
        title.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"Версия {Application.ProductVersion.Split('+', 2)[0]}",
            Margin = Padding.Empty,
        });
        header.Controls.Add(title);
        layout.Controls.Add(header, 0, 0);

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = $"Разработчик — {Application.CompanyName}",
            Margin = new Padding(0, 0, 0, 5),
        }, 0, 1);

        var link = new LinkLabel
        {
            AutoSize = true,
            Text = Repository,
            Margin = Padding.Empty,
        };
        link.LinkClicked += (_, _) => OpenRepository();
        layout.Controls.Add(link, 0, 2);

        var close = new Button
        {
            Text = "Закрыть",
            DialogResult = DialogResult.OK,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            MinimumSize = new Size(100, 32),
            Margin = Padding.Empty,
        };
        layout.Controls.Add(close, 0, 3);
        AcceptButton = close;
        CancelButton = close;
        Controls.Add(layout);
        ActiveControl = close;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            logo.Dispose();
        }

        base.Dispose(disposing);
    }

    private void OpenRepository()
    {
        try
        {
            using var browser = Process.Start(new ProcessStartInfo($"https://{Repository}") { UseShellExecute = true });
        }
        catch (Win32Exception error)
        {
            MessageBox.Show(this, error.Message, "Не удалось открыть GitHub", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
