namespace RustDeskList;

internal sealed class MergeDialog : Form
{
    private static readonly (string Label, ContactFields Field)[] AvailableFields =
    [
        ("Псевдоним", ContactFields.Alias),
        ("Имя ПК", ContactFields.Hostname),
        ("Пользователь", ContactFields.Username),
        ("ОС", ContactFields.Platform),
        ("Избранное", ContactFields.Favorite),
        ("Дата записи", ContactFields.ModifiedUtc),
    ];

    private readonly Label count = new() { AutoSize = true, Anchor = AnchorStyles.Left };
    private readonly Button accept = new() { Text = "Сохранить список…", DialogResult = DialogResult.OK, AutoSize = true };
    private readonly ToolTip hints = new();
    private readonly List<(CheckBox CheckBox, ContactFields Field)> fieldChecks = [];
    private readonly SourcePane first;
    private readonly SourcePane second;
    private bool changingSelection;
    private bool highlightingMatch;

    public MergeDialog(string firstPath, Contact[] firstContacts, string secondPath, Contact[] secondContacts)
    {
        Text = "Объединение контактов";
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        MinimizeBox = false;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10);
        ClientSize = new Size(1080, 590);
        MinimumSize = new Size(900, 450);

        first = CreatePane("Первый файл", firstPath, firstContacts);
        second = CreatePane("Второй файл", secondPath, secondContacts);
        first.Grid.CurrentCellChanged += (_, _) => HighlightMatch(first, second);
        second.Grid.CurrentCellChanged += (_, _) => HighlightMatch(second, first);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16),
            ColumnCount = 1,
            RowCount = 4,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label
        {
            AutoSize = true,
            Text = "Флажки отмечают записи для общего файла. Выбор строки подсвечивает совпадающий ID в соседней панели.",
            Margin = new Padding(0, 0, 0, 12),
        }, 0, 0);

        var panes = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        panes.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        first.Layout.Margin = new Padding(0, 0, 6, 0);
        second.Layout.Margin = new Padding(6, 0, 0, 0);
        panes.Controls.Add(first.Layout, 0, 0);
        panes.Controls.Add(second.Layout, 1, 0);
        layout.Controls.Add(panes, 0, 1);

        var fields = CreateRow();
        fields.Controls.Add(new Label { Text = "Поля общего файла", AutoSize = true, Anchor = AnchorStyles.Left });
        foreach (var (label, field) in AvailableFields)
        {
            var check = new CheckBox { Text = label, AutoSize = true, Checked = true };
            fieldChecks.Add((check, field));
            fields.Controls.Add(check);
        }
        layout.Controls.Add(fields, 0, 2);

        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, AutoSize = true };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footer.Controls.Add(count, 0, 0);

        var cancel = new Button { Text = "Отмена", DialogResult = DialogResult.Cancel, AutoSize = true };
        footer.Controls.Add(cancel, 1, 0);
        footer.Controls.Add(accept, 2, 0);
        layout.Controls.Add(footer, 0, 3);
        AcceptButton = accept;
        CancelButton = cancel;
        Controls.Add(layout);
        UpdateCount();
    }

    public (Contact[] First, Contact[] Second, ContactFields Fields) ReadSelection()
    {
        first.Grid.EndEdit();
        second.Grid.EndEdit();
        ContactFields fields = 0;
        foreach (var (check, field) in fieldChecks)
        {
            if (check.Checked)
            {
                fields |= field;
            }
        }

        return (SelectedContacts(first.Grid).ToArray(), SelectedContacts(second.Grid).ToArray(), fields);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            hints.Dispose();
        }

        base.Dispose(disposing);
    }

    private SourcePane CreatePane(string caption, string path, Contact[] contacts)
    {
        var grid = new DataGridView
        {
            Dock = DockStyle.Fill,
            Margin = Padding.Empty,
            BackgroundColor = SystemColors.Window,
            RowHeadersVisible = false,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            AllowUserToResizeRows = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect,
            MultiSelect = false,
            AccessibleName = caption,
        };
        grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(225, 236, 255);
        grid.DefaultCellStyle.SelectionForeColor = SystemColors.WindowText;
        grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Вкл.", MinimumWidth = 47, FillWeight = 10 });
        AddColumn("ID RustDesk", 96, 23);
        AddColumn("Псевдоним", 93, 29);
        AddColumn("Имя ПК", 96, 21);
        AddColumn("ОС", 73, 13);
        AddColumn("Избр.", 54, 8);

        var rowsById = new Dictionary<string, DataGridViewRow>(StringComparer.Ordinal);
        foreach (Contact contact in contacts)
        {
            int index = grid.Rows.Add(true, contact.Id, contact.Alias, contact.Hostname, contact.Platform,
                contact.Favorite ? "Да" : "");
            DataGridViewRow row = grid.Rows[index];
            row.Tag = contact;
            rowsById.Add(contact.Id, row);
        }

        grid.ClearSelection();
        grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (grid.IsCurrentCellDirty && grid.CurrentCell?.ColumnIndex == 0)
            {
                grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        };
        grid.CellValueChanged += (_, e) =>
        {
            if (!changingSelection && e.RowIndex >= 0 && e.ColumnIndex == 0)
            {
                UpdateCount();
            }
        };

        var pane = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        pane.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pane.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var heading = new Label
        {
            Text = $"{caption} — {Path.GetFileName(path)}",
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Height = 24,
            Margin = Padding.Empty,
        };
        hints.SetToolTip(heading, path);
        pane.Controls.Add(heading, 0, 0);
        pane.Controls.Add(grid, 0, 1);

        var details = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            Height = 23,
            Margin = new Padding(0, 5, 0, 0),
        };
        pane.Controls.Add(details, 0, 2);

        var actions = CreateRow();
        var selectAll = new Button { Text = "Выбрать все", AutoSize = true };
        var selectNone = new Button { Text = "Снять выбор", AutoSize = true };
        selectAll.Click += (_, _) => SetSelection(grid, true);
        selectNone.Click += (_, _) => SetSelection(grid, false);
        actions.Controls.AddRange([selectAll, selectNone]);
        pane.Controls.Add(actions, 0, 3);

        return new SourcePane(pane, grid, details, rowsById);

        void AddColumn(string title, int width, float weight)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title,
                MinimumWidth = width,
                FillWeight = weight,
                ReadOnly = true,
                SortMode = DataGridViewColumnSortMode.Automatic,
            });
        }
    }

    private void HighlightMatch(SourcePane selected, SourcePane other)
    {
        SetDetails(selected, selected.Grid.CurrentRow?.Tag as Contact);
        if (highlightingMatch || selected.Grid.CurrentRow?.Tag is not Contact contact)
        {
            return;
        }

        highlightingMatch = true;
        try
        {
            other.Grid.ClearSelection();
            if (other.RowsById.TryGetValue(contact.Id, out DataGridViewRow? match))
            {
                other.Grid.CurrentCell = match.Cells[1];
                match.Selected = true;
                SetDetails(other, (Contact)match.Tag!);
            }
            else
            {
                SetDetails(other, null);
            }
        }
        finally
        {
            highlightingMatch = false;
        }
    }

    private void SetDetails(SourcePane pane, Contact? contact)
    {
        string value = contact is null
            ? ""
            : $"Пользователь — {DisplayText(contact.Username)}   ·   Дата записи — {contact.ModifiedUtc?.ToLocalTime().ToString("g") ?? "—"}";
        pane.Details.Text = value;
        hints.SetToolTip(pane.Details, value);
    }

    private void SetSelection(DataGridView grid, bool selected)
    {
        grid.EndEdit();
        changingSelection = true;
        foreach (DataGridViewRow row in grid.Rows)
        {
            row.Cells[0].Value = selected;
        }
        changingSelection = false;
        UpdateCount();
    }

    private void UpdateCount()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        int records = 0;
        foreach (Contact contact in SelectedContacts(first.Grid).Concat(SelectedContacts(second.Grid)))
        {
            records++;
            ids.Add(contact.Id);
        }

        count.Text = $"Выбрано записей — {records} · ПК в списке — {ids.Count}";
        accept.Enabled = records > 0;
    }

    private static IEnumerable<Contact> SelectedContacts(DataGridView grid)
    {
        foreach (DataGridViewRow row in grid.Rows)
        {
            if (row.Cells[0].Value is true && row.Tag is Contact contact)
            {
                yield return contact;
            }
        }
    }

    private static string DisplayText(string text) => text.Length == 0 ? "—" : text;

    private static FlowLayoutPanel CreateRow() => new() { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };

    private sealed record SourcePane(
        TableLayoutPanel Layout, DataGridView Grid, Label Details, Dictionary<string, DataGridViewRow> RowsById);
}
