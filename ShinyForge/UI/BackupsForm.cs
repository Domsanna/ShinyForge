using ShinyForge.Services;

namespace ShinyForge.UI;

/// <summary>Lists the automatic backups of saves and spreadsheets and restores one.</summary>
public sealed class BackupsForm : Form
{
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly Label _info = Theme.Label("", Theme.Small, Theme.Muted);

    public BackupsForm()
    {
        Text = "ShinyForge - backups";
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1000, 560);
        _list.BackColor = Theme.Bg;
        _list.ForeColor = Theme.Text;
        _list.Font = Theme.Body;
        _list.Columns.Add("When", 150);
        _list.Columns.Add("Type", 100);
        _list.Columns.Add("File", 260);
        _list.Columns.Add("Size", 90);
        _list.Columns.Add("Original location", 380);

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 54, Padding = new Padding(10, 8, 10, 6), BackColor = Theme.Panel };
        var restore = new FancyButton("Restore selected", Theme.GlyphSync, Theme.Accent);
        restore.Click += (_, _) => Restore();
        var open = new FancyButton("Open backup folder", Theme.GlyphFolder, Theme.PanelHi);
        open.Click += (_, _) =>
        {
            Directory.CreateDirectory(BackupService.Root);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{BackupService.Root}\"") { UseShellExecute = true });
        };
        _info.Margin = new Padding(14, 12, 0, 0);
        bottom.Controls.AddRange(new Control[] { restore, open, _info });
        var top = Theme.Label("ShinyForge copies your save files and tracker spreadsheets automatically before it reads or changes them " +
                              "(tracker sync, Pokémon HOME export).\nRestoring puts the copy back - the current file is backed up first, so a restore can be undone.",
                              Theme.Small, Theme.Muted);
        top.Dock = DockStyle.Top;
        top.Padding = new Padding(12, 10, 12, 10);
        Controls.Add(_list);
        Controls.Add(bottom);
        Controls.Add(top);
        Fill();
    }

    private void Fill()
    {
        _list.Items.Clear();
        foreach (var b in BackupService.List())
        {
            var row = new ListViewItem(b.When.ToString("yyyy-MM-dd HH:mm:ss")) { Tag = b };
            row.SubItems.Add(b.Kind);
            row.SubItems.Add(Path.GetFileName(b.Original));
            row.SubItems.Add(b.Size > 1_000_000 ? $"{b.Size / 1e6:0.0} MB" : $"{b.Size / 1e3:0} KB");
            row.SubItems.Add(b.Original);
            _list.Items.Add(row);
        }
        _info.Text = $"{_list.Items.Count} backup(s), {BackupService.TotalSize() / 1e6:0} MB in {BackupService.Root}";
    }

    private void Restore()
    {
        if (_list.SelectedItems.Count == 0 || _list.SelectedItems[0].Tag is not BackupEntry b) return;
        if (MessageBox.Show(this, $"Restore the backup from {b.When:yyyy-MM-dd HH:mm:ss} over\n{b.Original}?\n\n" +
                                  "Close the game / Excel / PKHeX if they have this file open. The current file is backed up first.",
                "Restore backup", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            BackupService.Restore(b);
            MessageBox.Show(this, "Restored.", "Restore backup");
            Fill();
        }
        catch (Exception e) { MessageBox.Show(this, e.Message, "Restore backup", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
