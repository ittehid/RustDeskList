using System.ComponentModel;
using System.Diagnostics;

namespace RustDeskList;

internal sealed class MainForm : Form
{
    private static readonly Color Background = Color.FromArgb(243, 246, 251);
    private static readonly Color Ink = Color.FromArgb(23, 37, 60);
    private static readonly Color Muted = Color.FromArgb(72, 89, 113);
    private static readonly Color Line = Color.FromArgb(209, 220, 234);
    private static readonly Color Accent = Color.FromArgb(34, 83, 181);
    private static readonly Color AccentLight = Color.FromArgb(225, 236, 255);

    private readonly RustDeskProfile profile;
    private readonly TextBox search = new()
    {
        PlaceholderText = "Поиск по ID или имени",
        AccessibleName = "Поиск компьютеров",
        Width = 260,
    };
    private readonly Button refreshButton = CreateButton("&Обновить");
    private readonly Button exportButton = CreateButton("&Экспорт…");
    private readonly Button importButton = CreateButton("&Импорт…", primary: true);
    private readonly DataGridView grid = CreateGrid();
    private readonly Label empty = new()
    {
        Dock = DockStyle.Fill,
        BackColor = Color.White,
        ForeColor = Muted,
        TextAlign = ContentAlignment.MiddleCenter,
        Padding = new Padding(16),
    };
    private readonly Label countLabel = new() { AutoSize = true, ForeColor = Muted, Anchor = AnchorStyles.Left };
    private readonly Label status = new()
    {
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        AutoEllipsis = true,
        ForeColor = Muted,
    };
    private readonly LinkLabel backupLink = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Text = "Открыть резервную копию",
        LinkColor = Accent,
        Visible = false,
    };
    private readonly LinkLabel aboutLink = new()
    {
        AutoSize = true,
        Anchor = AnchorStyles.Right,
        Text = "О программе",
        LinkColor = Accent,
        Margin = Padding.Empty,
    };
    private readonly ToolTip hints = new();

    private ContactSnapshot snapshot = new([], 0);
    private string? backupPath;
    private bool busy;

    public MainForm(RustDeskProfile profile)
    {
        this.profile = profile;

        Text = "RustDeskList";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(920, 540);
        MinimumSize = new Size(755, 435);
        BackColor = Background;
        ForeColor = Ink;
        DoubleBuffered = true;

        BuildLayout();
        RenderContacts();
        SetBusy(false);
        ActiveControl = refreshButton;

        search.TextChanged += (_, _) => RenderContacts();
        Shown += async (_, _) => await RunAsync(RefreshAsync);
        refreshButton.Click += async (_, _) => await RunAsync(RefreshAsync);
        exportButton.Click += async (_, _) => await RunAsync(ExportAsync);
        importButton.Click += async (_, _) => await RunAsync(ImportAsync);
        aboutLink.LinkClicked += (_, _) => ShowAbout();
        backupLink.LinkClicked += async (_, _) => await RunAsync(() =>
        {
            if (backupPath is { } path)
            {
                using var explorer = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }

            return Task.CompletedTask;
        });
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.F1 && !busy)
        {
            ShowAbout();
            return true;
        }

        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (busy && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
        }

        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hints.Dispose();
        }

        base.Dispose(disposing);
    }

    private void BuildLayout()
    {
        SuspendLayout();

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(24, 18, 24, 14),
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var headerRow = CreateTwoColumnRow();
        headerRow.Margin = new Padding(0, 0, 0, 13);
        var header = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = Padding.Empty,
        };
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "RustDeskList",
            Font = new Font("Segoe UI", 22, FontStyle.Bold),
            Margin = Padding.Empty,
        });
        header.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Перенос списка ПК RustDesk",
            ForeColor = Muted,
            Margin = new Padding(1, 2, 0, 0),
        });
        headerRow.Controls.Add(header, 0, 0);
        headerRow.Controls.Add(aboutLink, 1, 0);
        hints.SetToolTip(aboutLink, "О программе (F1)");
        layout.Controls.Add(headerRow, 0, 0);

        var toolbar = CreateTwoColumnRow();
        toolbar.Margin = new Padding(0, 0, 0, 12);
        search.Anchor = AnchorStyles.Left;
        search.Margin = Padding.Empty;
        toolbar.Controls.Add(search, 0, 0);

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Right,
        };
        actions.Controls.AddRange([refreshButton, exportButton, importButton]);
        toolbar.Controls.Add(actions, 1, 0);
        layout.Controls.Add(toolbar, 0, 1);

        var border = new Panel { Dock = DockStyle.Fill, Padding = new Padding(1), BackColor = Line, Margin = Padding.Empty };
        border.Controls.Add(grid);
        border.Controls.Add(empty);
        layout.Controls.Add(border, 0, 2);

        var summary = CreateTwoColumnRow();
        summary.Margin = new Padding(0, 10, 0, 3);
        countLabel.Margin = Padding.Empty;
        backupLink.Margin = Padding.Empty;
        summary.Controls.Add(countLabel, 0, 0);
        summary.Controls.Add(backupLink, 1, 0);
        layout.Controls.Add(summary, 0, 3);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, RowCount = 2, Margin = Padding.Empty };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        footer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        status.Height = 23;
        status.Margin = Padding.Empty;
        footer.Controls.Add(status, 0, 0);
        var pathLabel = new Label
        {
            Text = profile.DirectoryPath,
            AccessibleName = "Папка локального профиля RustDesk",
            ForeColor = Muted,
            Font = new Font("Segoe UI", 9),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true,
            Height = 19,
            Margin = Padding.Empty,
        };
        hints.SetToolTip(pathLabel, profile.DirectoryPath);
        footer.Controls.Add(pathLabel, 0, 1);
        layout.Controls.Add(footer, 0, 4);

        Controls.Add(layout);
        ResumeLayout();
    }

    private static TableLayoutPanel CreateTwoColumnRow()
    {
        var row = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Dock = DockStyle.Fill, AutoSize = true };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        return row;
    }

    private static Button CreateButton(string label, bool primary = false)
    {
        var button = new Button
        {
            Text = label,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(100, 34),
            Padding = new Padding(9, 0, 9, 0),
            Margin = new Padding(8, 0, 0, 0),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Accent : Color.White,
            ForeColor = primary ? Color.White : Ink,
            UseVisualStyleBackColor = false,
        };
        button.FlatAppearance.BorderColor = primary ? Accent : Line;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(27, 67, 151) : AccentLight;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(23, 59, 131) : AccentLight;
        return button;
    }

    private static DataGridView CreateGrid()
    {
        var view = new DataGridView
        {
            Dock = DockStyle.Fill,
            AccessibleName = "Последние сеансы и избранное RustDesk",
            BackgroundColor = Color.White,
            BorderStyle = BorderStyle.None,
            GridColor = Line,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            RowHeadersVisible = false,
            ColumnHeadersHeight = 35,
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
            ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single,
            EnableHeadersVisualStyles = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText,
        };
        view.RowTemplate.Height = 31;
        view.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.FromArgb(230, 237, 248),
            ForeColor = Ink,
            SelectionBackColor = Color.FromArgb(230, 237, 248),
            SelectionForeColor = Ink,
            Font = new Font("Segoe UI", 9, FontStyle.Bold),
            Padding = new Padding(8, 0, 5, 0),
        };
        view.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Color.White,
            ForeColor = Ink,
            SelectionBackColor = AccentLight,
            SelectionForeColor = Ink,
            Padding = new Padding(8, 0, 5, 0),
        };
        view.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(248, 250, 253);

        AddColumn("Id", "ID RustDesk", 125, 20);
        AddColumn("Alias", "Псевдоним", 125, 29);
        AddColumn("Hostname", "Имя ПК", 110, 26);
        AddColumn("Platform", "ОС", 75, 12);
        AddColumn("Favorite", "Избранное", 100, 13);
        return view;

        void AddColumn(string name, string label, int minimumWidth, float weight)
        {
            view.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = name,
                HeaderText = label,
                MinimumWidth = minimumWidth,
                FillWeight = weight,
                SortMode = DataGridViewColumnSortMode.Automatic,
            });
        }
    }

    private void RenderContacts()
    {
        string query = search.Text.Trim();
        Contact[] all = snapshot.Contacts;
        Contact[] shown = query.Length == 0 ? all : all.Where(contact =>
            contact.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            contact.Alias.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            contact.Hostname.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();

        grid.SuspendLayout();
        grid.Rows.Clear();
        foreach (Contact contact in shown)
        {
            grid.Rows.Add(contact.Id, contact.Alias, contact.Hostname, contact.Platform, contact.Favorite ? "Да" : "");
        }

        grid.ClearSelection();
        grid.ResumeLayout();

        empty.Text = all.Length == 0 && snapshot.Skipped == 0
            ? "Список пуст. Импортируйте контакты из JSON."
            : "По этому запросу компьютеры не найдены.";
        if (all.Length == 0 && snapshot.Skipped > 0)
        {
            empty.Text = "Не удалось прочитать записи в профиле RustDesk.";
        }

        empty.Visible = shown.Length == 0;
        if (empty.Visible)
        {
            empty.BringToFront();
        }

        string total = query.Length == 0 ? $"{all.Length} ПК" : $"Показано {shown.Length} из {all.Length}";
        countLabel.Text = $"{total}  ·  В избранном {all.Count(contact => contact.Favorite)}";
    }

    private async Task RefreshAsync()
    {
        snapshot = await Task.Run(profile.ReadContacts);
        RenderContacts();
        SetStatus(snapshot.Skipped > 0
            ? $"Пропущено записей — {snapshot.Skipped}."
            : "Последние сеансы и избранное");
    }

    private async Task ExportAsync()
    {
        using var picker = new SaveFileDialog
        {
            Title = "Экспорт контактов RustDesk",
            Filter = "Контакты RustDesk (*.json)|*.json",
            FileName = $"RustDeskList-{DateTime.Today:yyyy-MM-dd}.json",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetStatus("Экспорт контактов…");
        snapshot = await Task.Run(() =>
        {
            ContactSnapshot current = profile.ReadContacts();
            ContactFile.Save(picker.FileName, current.Contacts);
            return current;
        });
        RenderContacts();
        string skipped = snapshot.Skipped == 0 ? "" : $" Пропущено записей — {snapshot.Skipped}.";
        SetStatus($"Экспортировано {snapshot.Contacts.Length} ПК в {Path.GetFileName(picker.FileName)}.{skipped}");
    }

    private async Task ImportAsync()
    {
        using var picker = new OpenFileDialog
        {
            Title = "Импорт контактов RustDesk",
            Filter = "Контакты RustDesk (*.json)|*.json",
            CheckFileExists = true,
        };
        if (picker.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        SetStatus("Проверка файла контактов…");
        string source = picker.FileName;
        var (contacts, preview) = await Task.Run(() =>
        {
            Contact[] loaded = ContactFile.Load(source);
            return (loaded, profile.PlanImport(loaded));
        });
        if (preview.Changes.Count == 0)
        {
            SetStatus("Список уже есть в профиле. Изменений нет.");
            return;
        }

        var decision = MessageBox.Show(this,
            $"Новых ПК — {preview.Added}\n" +
            $"ПК с заполнением пустых полей — {preview.Updated}\n" +
            $"Добавлений в избранное — {preview.AddedToFavorites}\n\n" +
            "Утилита сделает резервную копию изменяемых файлов. " +
            "Если клиент RustDesk открыт, он будет перезапущен. Исходящие подключения завершатся.\n\n" +
            "Импортировать контакты?",
            "Подтверждение импорта", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
        if (decision != DialogResult.Yes)
        {
            SetStatus("Импорт отменён.");
            return;
        }

        SetStatus("Импорт контактов…");
        string? restartWarning = null;
        string? backup;
        ImportPlan applied;
        try
        {
            (applied, backup) = await Task.Run(async () =>
            {
                using var importLock = profile.LockImport();
                var client = new RustDeskClient();
                try
                {
                    ImportPlan plan = profile.PlanImport(contacts);
                    if (plan.Changes.Count == 0)
                    {
                        return (plan, null);
                    }

                    await client.CloseAsync();
                    plan = profile.PlanImport(contacts);
                    return (plan, profile.Apply(plan, client.EnsureClosed));
                }
                finally
                {
                    restartWarning = client.TryRestart();
                }
            });
        }
        finally
        {
            if (restartWarning is not null)
            {
                MessageBox.Show(this, restartWarning, "Запуск RustDesk", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        if (backup is not null)
        {
            backupPath = backup;
            backupLink.Visible = true;
            hints.SetToolTip(backupLink, backup);
        }

        SetStatus(applied.Changes.Count == 0
            ? "Список уже есть в профиле. Изменений нет."
            : $"Импорт завершён. Добавлено ПК — {applied.Added}, обновлено — {applied.Updated}, в избранное — {applied.AddedToFavorites}.");
        snapshot = await Task.Run(profile.ReadContacts);
        RenderContacts();
    }

    private async Task RunAsync(Func<Task> action)
    {
        SetBusy(true);
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or Win32Exception or TimeoutException or ArgumentException)
        {
            SetStatus("Операция завершилась с ошибкой.");
            MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool value)
    {
        busy = value;
        refreshButton.Enabled = !value;
        importButton.Enabled = !value;
        exportButton.Enabled = !value && snapshot.Contacts.Length > 0;
        backupLink.Enabled = !value;
        aboutLink.Enabled = !value;
    }

    private void ShowAbout()
    {
        using var dialog = new AboutForm(Icon ?? SystemIcons.Application);
        dialog.ShowDialog(this);
    }

    private void SetStatus(string message)
    {
        status.Text = message;
        hints.SetToolTip(status, message);
    }
}
