using System.Diagnostics;
using System.Drawing;

namespace SlotDeck;

/// <summary>Lists the keyboard's screen slots with previews; shows, cycles and replaces them.</summary>
internal sealed class MainForm : Form
{
    private readonly SlotStore _store = new();
    private readonly ListView _list = new()
    {
        Dock = DockStyle.Fill, View = View.LargeIcon, MultiSelect = false, HideSelection = false, AllowDrop = true,
    };
    private readonly ImageList _thumbs = new() { ImageSize = new Size(124, 85), ColorDepth = ColorDepth.Depth32Bit };
    private readonly PictureBox _preview = new()
    {
        Size = new Size(496, 340), SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, AllowDrop = true,
    };
    private readonly Label _info = new() { AutoSize = true, MaximumSize = new Size(496, 0), Padding = new Padding(0, 8, 0, 0) };
    private readonly Button _refresh = Btn("Refresh"), _show = Btn("Show on keyboard"), _prev = Btn("◀ Previous"),
        _next = Btn("Next ▶"), _replace = Btn("Replace…");
    private readonly CheckBox _fit = new() { Text = "Fit whole picture (black bars) instead of filling the screen", AutoSize = true };
    private readonly ToolStripStatusLabel _status = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Visible = false, Size = new Size(160, 16) };
    private readonly System.Windows.Forms.Timer _anim = new();
    private readonly ToolTip _tips = new();

    private List<Slot> _slots = [];
    private ushort? _activeId;
    private readonly Dictionary<ushort, Bitmap?> _frames = []; // picture file id -> decoded frame (null = unreadable)
    private bool _busy, _closing;
    private Task? _pump;
    private int _animIndex;

    public MainForm()
    {
        Text = "SlotDeck";
        Font = new Font("Segoe UI", 9.5f);
        ClientSize = new Size(1060, 620);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        AllowDrop = true;

        _list.LargeImageList = _thumbs;
        _thumbs.Images.Add("loading", Placeholder("…"));

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Dock = DockStyle.Top, MaximumSize = new Size(520, 0) };
        buttons.Controls.AddRange([_show, _prev, _next, _replace, _refresh]);
        var right = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(12), WrapContents = false };
        right.Controls.AddRange([_preview, _info, buttons, _fit]);

        var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2, SplitterWidth = 6 };
        split.Panel1.Controls.Add(_list);
        split.Panel2.Controls.Add(right);
        var statusStrip = new StatusStrip();
        statusStrip.Items.AddRange([_status, _progress]);
        Controls.Add(split);
        Controls.Add(statusStrip);
        Load += (_, _) => split.SplitterDistance = Math.Max(200, ClientSize.Width - 540);

        _tips.SetToolTip(_prev, "Show the previous slot (stands in for FN + ←)");
        _tips.SetToolTip(_next, "Show the next slot (stands in for FN + →)");
        _tips.SetToolTip(_replace, "Pick an image or GIF to put in this slot. You can also drop a file onto the window.");

        _refresh.Click += async (_, _) => await ScanAsync();
        _show.Click += async (_, _) => { if (Selected is { } s) await ShowAsync(s); };
        _prev.Click += async (_, _) => await StepAsync(-1);
        _next.Click += async (_, _) => await StepAsync(+1);
        _replace.Click += async (_, _) => await ReplaceAsync(null);
        _list.SelectedIndexChanged += (_, _) => OnSelectionChanged();
        _list.DoubleClick += async (_, _) => { if (Selected is { } s) await ShowAsync(s); };
        _anim.Tick += (_, _) => Animate();
        foreach (Control c in new Control[] { this, _list, _preview })
        {
            c.DragEnter += (_, e) => e.Effect = DroppedFile(e) != null && CanReplace ? DragDropEffects.Copy : DragDropEffects.None;
            c.DragDrop += async (_, e) => { if (DroppedFile(e) is { } f) await ReplaceAsync(f); };
        }

        Shown += async (_, _) => await ScanAsync();
        FormClosing += OnClosing;
        UpdateButtons();
    }

    private static Button Btn(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2), Margin = new Padding(0, 8, 8, 0) };

    private Slot? Selected => _list.SelectedItems.Count > 0 ? _list.SelectedItems[0].Tag as Slot : null;
    private bool CanReplace => !_busy && Selected is { IsTrackDeck: false };

    // ---------------- keyboard actions ----------------

    /// <summary>Runs one keyboard action at a time, after the background preview loader has paused.</summary>
    private async Task RunAsync(string what, Func<IProgress<double>, Task> action)
    {
        if (_busy) return;
        _busy = true;
        UpdateButtons();
        SetStatus(what + "…");
        _progress.Value = 0;
        _progress.Visible = true;
        var progress = new Progress<double>(p => _progress.Value = (int)Math.Clamp(p * 100, 0, 100));
        try
        {
            if (_pump != null) await _pump;
            await action(progress);
        }
        catch (Exception e) when (e is BragiException or IOException or InvalidOperationException)
        {
            SetStatus(e.Message);
        }
        finally
        {
            _progress.Visible = false;
            _busy = false;
            UpdateButtons();
            if (!_closing) StartPump();
        }
    }

    private Task ScanAsync() => RunAsync("Looking for slots on the keyboard", async progress =>
    {
        var result = await _store.ScanAsync(progress);
        // Slots may have changed elsewhere (Web Hub, the command line), so reload every preview.
        _frames.Clear();
        foreach (var key in _thumbs.Images.Keys.Cast<string>().Where(k => k != "loading").ToList()) _thumbs.Images.RemoveByKey(key);
        _slots = result.Slots;
        _activeId = result.ActiveId;
        RebuildList();
        SetStatus(_slots.Count == 0 ? "No image or GIF slots found." : $"{_slots.Count} slots found." + TrackDeckHint());
    });

    private Task ShowAsync(Slot slot) => RunAsync($"Showing slot {slot.Id}", async _ =>
    {
        await _store.ShowAsync(slot);
        _activeId = slot.Id;
        RefreshLabels();
        SetStatus($"Slot {Index(slot)} is on the screen." + TrackDeckHint());
    });

    private async Task StepAsync(int delta)
    {
        if (_slots.Count == 0) return;
        int at = _slots.FindIndex(s => s.Id == _activeId);
        if (at < 0) at = Selected is { } sel ? _slots.IndexOf(sel) - delta : (delta > 0 ? -1 : 0);
        var next = _slots[((at + delta) % _slots.Count + _slots.Count) % _slots.Count];
        Select(next);
        await ShowAsync(next);
    }

    private async Task ReplaceAsync(string? path)
    {
        if (Selected is not { } slot || !CanReplace) return;
        if (path == null)
        {
            using var dlg = new OpenFileDialog
            {
                Title = $"Replace slot {Index(slot)}",
                Filter = "Images and GIFs|*.gif;*.png;*.jpg;*.jpeg;*.bmp|All files|*.*",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            path = dlg.FileName;
        }

        ScreenContent content;
        try
        {
            SetStatus("Preparing " + Path.GetFileName(path) + "…");
            bool fit = _fit.Checked;
            content = await Task.Run(() => ImageCodec.Load(path, fit));
        }
        catch (Exception e) when (e is OutOfMemoryException or IOException or ArgumentException or UnauthorizedAccessException)
        {
            SetStatus("Can't read that file as an image.");
            return;
        }

        string what = content.IsGif
            ? $"a GIF with {content.Packages.Count} frames ({content.DelayMs} ms each)" +
              (content.SourceFrames > content.Packages.Count ? $", cut down from {content.SourceFrames} frames" : "")
            : "a still image";
        double seconds = content.Bytes / 67_000.0; // measured write speed over USB
        var answer = MessageBox.Show(this,
            $"Replace slot {Index(slot)} ({slot.Kind}) with {Path.GetFileName(path)}?\n\n" +
            $"It will be {what}. Writing takes about {Math.Max(1, Math.Round(seconds))} s.\n" +
            "The picture currently in this slot is overwritten and can't be brought back.",
            "Replace slot", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (answer != DialogResult.OK) return;

        await RunAsync($"Writing to slot {Index(slot)}", async progress =>
        {
            var updated = await _store.ReplaceAsync(slot, content, _slots, progress);
            // A replaced still image keeps its picture file id, so the cached frame and thumbnail must both go.
            foreach (var id in slot.Resources.Concat(updated.Resources)) _frames.Remove(id);
            ForgetThumbnail(slot);
            ForgetThumbnail(updated);
            _slots[_slots.IndexOf(slot)] = updated;
            RebuildList(select: updated.Id);
            SetStatus($"Slot {Index(updated)} replaced." + (updated.Id == _activeId ? " It's on the screen now." : " Press Show to put it on the screen."));
        });
    }

    // ---------------- previews ----------------

    /// <summary>Loads previews in the background, one file at a time: the selected GIF's frames first, then thumbnails.</summary>
    private void StartPump()
    {
        if (_pump is { IsCompleted: false } || _busy || _closing) return;
        _pump = PumpAsync();
    }

    private async Task PumpAsync()
    {
        try
        {
            while (!_busy && !_closing && NextWanted() is ushort id)
            {
                var data = await _store.ReadFileAsync(id);
                _frames[id] = data == null ? null : ImageCodec.Decode(data);
                OnFrameLoaded(id);
            }
        }
        catch (Exception e) when (e is BragiException or IOException)
        {
            SetStatus("Preview loading stopped: " + e.Message);
        }
    }

    private ushort? NextWanted()
    {
        if (Selected is { IsGif: true } sel)
            foreach (var id in sel.Resources) if (!_frames.ContainsKey(id)) return id;
        foreach (var s in _slots) if (!_frames.ContainsKey(s.Resources[0])) return s.Resources[0];
        return null;
    }

    private void OnFrameLoaded(ushort id)
    {
        foreach (ListViewItem item in _list.Items)
        {
            if (item.Tag is not Slot s || s.Resources[0] != id) continue;
            string key = ThumbKey(s);
            _thumbs.Images.RemoveByKey(key); // a fresh read always wins over an older thumbnail
            _thumbs.Images.Add(key, _frames[id] is { } f ? Thumbnail(f) : Placeholder("?"));
            item.ImageKey = key;
        }
        if (Selected is { } sel && sel.Resources[0] == id && _preview.Image == null) ShowFrame(sel, 0);
        UpdateInfo();
    }

    private void OnSelectionChanged()
    {
        _anim.Stop();
        _preview.Image = null;
        _animIndex = 0;
        if (Selected is { } s)
        {
            ShowFrame(s, 0);
            if (s.IsGif) { _anim.Interval = Math.Max(20, s.DelayMs); _anim.Start(); }
        }
        UpdateInfo();
        UpdateButtons();
        StartPump();
    }

    private void Animate()
    {
        if (Selected is not { IsGif: true } s) { _anim.Stop(); return; }
        // Play only the frames loaded so far; the rest join in as they arrive.
        int loaded = s.Resources.TakeWhile(_frames.ContainsKey).Count();
        if (loaded < 2) return;
        _animIndex = (_animIndex + 1) % loaded;
        ShowFrame(s, _animIndex);
    }

    private void ShowFrame(Slot s, int index)
    {
        if (_frames.TryGetValue(s.Resources[index], out var f) && f != null) _preview.Image = f;
    }

    private void UpdateInfo()
    {
        if (Selected is not { } s) { _info.Text = _slots.Count > 0 ? "Pick a slot to preview it." : ""; return; }
        string text = $"Slot {Index(s)}  ·  {s.Kind}";
        if (s.IsGif)
        {
            int loaded = s.Resources.Count(_frames.ContainsKey);
            text += $", {s.DelayMs} ms per frame";
            if (loaded < s.Resources.Length) text += $"  ·  loading preview {loaded}/{s.Resources.Length}";
        }
        if (s.Id == _activeId) text += "\nShowing on the keyboard now.";
        if (s.IsTrackDeck) text += "\nTrackDeck rewrites this one on every track change, so it can't be replaced here.";
        _info.Text = text;
    }

    // ---------------- list ----------------

    private static string ThumbKey(Slot s) => "slot" + s.Id + "-" + s.Resources[0];

    private void ForgetThumbnail(Slot s) => _thumbs.Images.RemoveByKey(ThumbKey(s));

    private void RebuildList(ushort? select = null)
    {
        ushort? keep = select ?? Selected?.Id ?? _activeId;
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var s in _slots)
        {
            string key = ThumbKey(s);
            _list.Items.Add(new ListViewItem(Label(s)) { Tag = s, ImageKey = _thumbs.Images.ContainsKey(key) ? key : "loading" });
        }
        _list.EndUpdate();
        var target = _slots.FirstOrDefault(s => s.Id == keep) ?? _slots.FirstOrDefault();
        if (target != null) Select(target); else OnSelectionChanged();
    }

    private void RefreshLabels()
    {
        foreach (ListViewItem item in _list.Items) if (item.Tag is Slot s) item.Text = Label(s);
        UpdateInfo();
    }

    private string Label(Slot s) => $"{Index(s)}. {s.Kind}" + (s.Id == _activeId ? "  ● on screen" : "");
    private int Index(Slot s) => _slots.FindIndex(x => x.Id == s.Id) + 1;

    private void Select(Slot s)
    {
        foreach (ListViewItem item in _list.Items)
            if (item.Tag is Slot x && x.Id == s.Id) { item.Selected = item.Focused = true; item.EnsureVisible(); }
    }

    private void UpdateButtons()
    {
        bool ok = !_busy && !_closing;
        _refresh.Enabled = ok;
        _show.Enabled = ok && Selected != null;
        _prev.Enabled = _next.Enabled = ok && _slots.Count > 1;
        _replace.Enabled = ok && CanReplace;
        _fit.Enabled = ok;
    }

    // ---------------- helpers ----------------

    private void SetStatus(string text) => _status.Text = text;

    private static string TrackDeckHint() =>
        Process.GetProcessesByName("TrackDeck").Length > 0 ? "  TrackDeck is running and will put its card back on the next track change." : "";

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files ? files[0] : null;

    private Bitmap Thumbnail(Bitmap src)
    {
        var bmp = new Bitmap(_thumbs.ImageSize.Width, _thumbs.ImageSize.Height);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(src, 0, 0, bmp.Width, bmp.Height);
        return bmp;
    }

    private Bitmap Placeholder(string mark)
    {
        var bmp = new Bitmap(_thumbs.ImageSize.Width, _thumbs.ImageSize.Height);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(40, 40, 40));
        TextRenderer.DrawText(g, mark, new Font("Segoe UI", 18f), new Rectangle(Point.Empty, bmp.Size), Color.Gray,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        return bmp;
    }

    /// <summary>Lets any running write finish, ends the keyboard session, then closes.</summary>
    private async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        if (_busy && MessageBox.Show(this, "SlotDeck is still writing to the keyboard. Close when it's done?", "SlotDeck",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
        {
            e.Cancel = true;
            return;
        }
        e.Cancel = true;
        _closing = true;
        _anim.Stop();
        UpdateButtons();
        SetStatus("Closing…");
        while (_busy) await Task.Delay(100);
        if (_pump != null) await _pump;
        await _store.CloseAsync();
        Close();
    }
}
