using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Several Checkpoint backups of the game were found: the user picks the one to use (newest first, preselected).</summary>
public sealed class CheckpointPicker : Form
{
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None,
    };
    public CheckpointBackup? Chosen { get; private set; }

    public CheckpointPicker(string gameTitle, IReadOnlyList<CheckpointBackup> backups)
    {
        Text = "ShinyForge - choose a Checkpoint backup";
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Size = new Size(900, 480);
        MinimumSize = new Size(640, 320);
        StartPosition = FormStartPosition.CenterParent;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var head = Theme.Label($"{backups.Count} Checkpoint backups of Pokémon {gameTitle} were found. Which one should ShinyForge use?", Theme.H2);
        head.Dock = DockStyle.Top;
        head.Padding = new Padding(16, 12, 16, 8);
        head.AutoSize = false;
        head.Height = 46;

        _list.BackColor = Theme.Panel;
        _list.ForeColor = Theme.Text;
        _list.Columns.Add("Backup", 180);
        _list.Columns.Add("Saved", 140);
        _list.Columns.Add("Trainer", 140);
        _list.Columns.Add("Play time", 90);
        _list.Columns.Add("Where", 290);
        foreach (var b in backups)
        {
            var s = b.Save.Save;
            var item = new ListViewItem(new[]
            {
                b.BackupName, b.Time.ToString("yyyy-MM-dd HH:mm"), $"{s.OT} ({s.DisplayTID})", $"{s.PlayedHours}h {s.PlayedMinutes:00}m",
                Path.GetFileName(b.TitleFolder) + "  •  " + b.Drive,
            }) { Tag = b };
            _list.Items.Add(item);
        }
        if (_list.Items.Count > 0) _list.Items[0].Selected = true;
        var host = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 16, 8) };
        host.Controls.Add(_list);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 58, Padding = new Padding(12, 8, 12, 8), FlowDirection = FlowDirection.RightToLeft, BackColor = Theme.Panel };
        var cancel = new FancyButton("Cancel", "", Theme.PanelHi);
        var use = new FancyButton("Use this backup", Theme.GlyphCheck, Theme.Accent);
        bottom.Controls.AddRange(new Control[] { cancel, use });
        use.Click += (_, _) => Pick();
        _list.DoubleClick += (_, _) => Pick();
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.Add(host);
        Controls.Add(bottom);
        Controls.Add(head);
    }

    private void Pick()
    {
        if (_list.SelectedItems.Count == 0) return;
        Chosen = (CheckpointBackup)_list.SelectedItems[0].Tag!;
        DialogResult = DialogResult.OK;
        Close();
    }
}
