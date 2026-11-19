using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SlotDeck;

/// <summary>
/// Shown before a replace: pick the area of the picture to use with the mouse, see it on a preview of the
/// keyboard screen (black bars included), and for GIFs pick how fast it plays.
/// </summary>
internal sealed class CropDialog : Form
{
    private const int CanvasW = 520, CanvasH = 440, Grip = 7;
    private static readonly float ScreenAspect = ImageCodec.W / (float)ImageCodec.H;

    private readonly Image _img;
    private readonly int[] _kept;            // source frame indices that go to the keyboard
    private readonly int _baseDelay;         // the GIF's own delay per kept frame
    private readonly Bitmap _canvasImage;    // first frame, scaled to the canvas
    private readonly RectangleF _imageRect;  // where the picture sits on the canvas
    private readonly float _scale;           // canvas pixels per picture pixel
    private readonly Bitmap?[] _screens;     // rendered screen frames for the current area (filled lazily)
    private RectangleF _area;                // chosen area, in picture pixels

    private readonly Canvas _canvas = new() { Size = new Size(CanvasW, CanvasH), Cursor = Cursors.Cross };
    private readonly Canvas _screen = new() { Size = new Size(ImageCodec.W * 2, ImageCodec.H * 2) };
    private readonly Label _areaInfo = new() { AutoSize = true, Margin = new Padding(0, 12, 0, 0) };
    private readonly Label _speedInfo = new() { AutoSize = true, MaximumSize = new Size(ImageCodec.W * 2 - 20, 0) };
    private readonly Label _writeInfo = new() { AutoSize = true, Anchor = AnchorStyles.Left, MaximumSize = new Size(640, 0) };
    private readonly RadioButton _slowRadio = new() { Text = "Slow down by", AutoSize = true, Checked = true };
    private readonly RadioButton _fixedRadio = new() { Text = "Time between frames", AutoSize = true };
    private readonly NumericUpDown _slow = new() { Minimum = 0, Maximum = 1000, Increment = 10, Width = 70 };
    private readonly NumericUpDown _fixed = new() { Minimum = ImageCodec.MinDelayMs, Maximum = 5000, Increment = 10, Width = 70 };
    private readonly System.Windows.Forms.Timer _anim = new();
    private int _frame;

    private enum Drag { None, New, Move, Resize }
    private Drag _drag;
    private Edges _edges;
    private PointF _anchor;      // where the drag started, in picture pixels
    private RectangleF _startArea;

    [Flags] private enum Edges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

    public RectangleF Area => _area;
    public GifTiming Timing => _fixedRadio.Checked ? new GifTiming(FrameMs: (int)_fixed.Value) : new GifTiming((int)_slow.Value);

    /// <summary>Throws if the file isn't a picture GDI+ can read.</summary>
    public CropDialog(string path, string slotName, Icon? icon)
    {
        _img = ImageCodec.Open(path);
        int count = ImageCodec.FrameCount(_img);
        _kept = ImageCodec.KeptFrames(count);
        _baseDelay = _kept.Length > 1 ? ImageCodec.BaseDelayMs(_img) : 0;
        _screens = new Bitmap?[_kept.Length];
        _area = ImageCodec.WholePicture(_img);

        _scale = Math.Min(CanvasW / (float)_img.Width, CanvasH / (float)_img.Height);
        float iw = _img.Width * _scale, ih = _img.Height * _scale;
        _imageRect = new RectangleF((CanvasW - iw) / 2, (CanvasH - ih) / 2, iw, ih);
        _canvasImage = new Bitmap(Math.Max(1, (int)Math.Round(iw)), Math.Max(1, (int)Math.Round(ih)));
        using (var g = Graphics.FromImage(_canvasImage))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(_img, 0, 0, _canvasImage.Width, _canvasImage.Height);
        }

        Text = $"Replace {slotName} with {Path.GetFileName(path)}";
        Icon = icon;
        ShowIcon = icon != null;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        // ----- left: the picture with the area to use -----
        var hint = new Label
        {
            AutoSize = true, MaximumSize = new Size(CanvasW, 0), Margin = new Padding(0, 0, 0, 8),
            Text = "Drag on the picture to choose the area to use. Drag inside it to move it, drag its edges to resize. " +
                   "Hold Shift for the screen's shape. Double-click to use the whole picture again.",
        };
        var whole = SmallButton("Whole picture");
        var fill = SmallButton("Fill the screen");
        whole.Click += (_, _) => SetArea(ImageCodec.WholePicture(_img));
        fill.Click += (_, _) => SetArea(ImageCodec.FillScreen(_img));
        var areaButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        areaButtons.Controls.AddRange([whole, fill, _areaInfo]);
        var left = Column();
        left.Controls.AddRange([hint, _canvas, areaButtons]);

        // ----- right: what the keyboard will show -----
        var screenTitle = new Label { Text = "On the keyboard", AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 0, 0, 8) };
        var right = Column();
        right.Margin = new Padding(16, 0, 0, 0);
        right.Controls.AddRange([screenTitle, _screen]);
        if (_kept.Length > 1) right.Controls.Add(SpeedBox());

        // ----- bottom: what will happen, OK / Cancel -----
        var ok = new Button { Text = "Write to " + slotName, AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(6, 2, 6, 2) };
        var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(6, 2, 6, 2) };
        AcceptButton = ok;
        CancelButton = cancel;
        var bottom = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 12, 0, 0) };
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bottom.Controls.Add(_writeInfo, 0, 0);
        bottom.Controls.Add(ok, 1, 0);
        bottom.Controls.Add(cancel, 2, 0);

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, RowCount = 2, Dock = DockStyle.Fill };
        root.Controls.Add(left, 0, 0);
        root.Controls.Add(right, 1, 0);
        root.Controls.Add(bottom, 0, 1);
        root.SetColumnSpan(bottom, 2);
        Controls.Add(root);

        _canvas.Paint += PaintCanvas;
        _canvas.MouseDown += CanvasDown;
        _canvas.MouseMove += CanvasMove;
        _canvas.MouseUp += (_, _) => _drag = Drag.None;
        _canvas.DoubleClick += (_, _) => SetArea(ImageCodec.WholePicture(_img));
        _screen.Paint += PaintScreen;
        _anim.Tick += (_, _) => { _frame = (_frame + 1) % _kept.Length; _screen.Invalidate(); };

        UpdateInfo();
        if (_kept.Length > 1) _anim.Start();
    }

    private static Button SmallButton(string text) => new() { Text = text, AutoSize = true, Margin = new Padding(0, 8, 8, 0) };
    private static FlowLayoutPanel Column() => new() { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty };

    private GroupBox SpeedBox()
    {
        _fixed.Value = Math.Clamp(_baseDelay, (int)_fixed.Minimum, (int)_fixed.Maximum);
        var grid = new TableLayoutPanel { AutoSize = true, ColumnCount = 3, Dock = DockStyle.Fill };
        grid.Controls.Add(_slowRadio, 0, 0);
        grid.Controls.Add(_slow, 1, 0);
        grid.Controls.Add(new Label { Text = "%", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 0);
        grid.Controls.Add(_fixedRadio, 0, 1);
        grid.Controls.Add(_fixed, 1, 1);
        grid.Controls.Add(new Label { Text = "ms", AutoSize = true, Anchor = AnchorStyles.Left }, 2, 1);
        grid.Controls.Add(_speedInfo, 0, 2);
        grid.SetColumnSpan(_speedInfo, 3);
        foreach (Control c in new Control[] { _slowRadio, _fixedRadio }) c.Anchor = AnchorStyles.Left;

        _slow.ValueChanged += (_, _) => { _slowRadio.Checked = true; UpdateInfo(); };
        _fixed.ValueChanged += (_, _) => { _fixedRadio.Checked = true; UpdateInfo(); };
        _slow.Enter += (_, _) => _slowRadio.Checked = true;
        _fixed.Enter += (_, _) => _fixedRadio.Checked = true;
        _slowRadio.CheckedChanged += (_, _) => UpdateInfo();

        var box = new GroupBox { Text = "GIF speed", AutoSize = true, Width = ImageCodec.W * 2, Margin = new Padding(0, 12, 0, 0), Padding = new Padding(8) };
        box.Controls.Add(grid);
        return box;
    }

    private void UpdateInfo()
    {
        _areaInfo.Text = $"Area: {Math.Round(_area.Width)} × {Math.Round(_area.Height)} px";
        string frames = "";
        if (_kept.Length > 1)
        {
            int delay = Timing.DelayFor(_baseDelay);
            _anim.Interval = Math.Max(20, delay);
            int source = ImageCodec.FrameCount(_img);
            _speedInfo.Text = $"The GIF plays at about {_baseDelay} ms per frame" +
                (source > _kept.Length ? $" (keeping {_kept.Length} of its {source} frames)" : "") +
                $". On the keyboard: {delay} ms per frame, {delay * _kept.Length / 1000.0:0.0} s per loop.";
            frames = $"{_kept.Length} frames, ";
        }
        double seconds = _kept.Length * (double)ImageCodec.PackageBytes / 67_000; // measured write speed over USB
        _writeInfo.Text = $"Writing takes about {Math.Max(1, Math.Round(seconds))} s ({frames}{_kept.Length * ImageCodec.PackageBytes / 1024} KB). " +
                          "The picture in this slot now is overwritten and can't be brought back.";
    }

    // ---------------- area ----------------

    private void SetArea(RectangleF area)
    {
        _area = area;
        for (int i = 0; i < _screens.Length; i++) { _screens[i]?.Dispose(); _screens[i] = null; }
        _canvas.Invalidate();
        _screen.Invalidate();
        UpdateInfo();
    }

    private PointF ToPicture(Point p) => new(
        Math.Clamp((p.X - _imageRect.X) / _scale, 0, _img.Width),
        Math.Clamp((p.Y - _imageRect.Y) / _scale, 0, _img.Height));

    private RectangleF ToCanvas(RectangleF r) =>
        new(_imageRect.X + r.X * _scale, _imageRect.Y + r.Y * _scale, r.Width * _scale, r.Height * _scale);

    /// <summary>Inside an area smaller than the picture, a drag moves it; on the whole picture it starts a new one.</summary>
    private bool CanMove(Point p) =>
        ToCanvas(_area).Contains(p) && (_area.Width < _img.Width - 0.5f || _area.Height < _img.Height - 0.5f);

    private Edges HitEdges(Point p)
    {
        var r = ToCanvas(_area);
        if (p.X < r.Left - Grip || p.X > r.Right + Grip || p.Y < r.Top - Grip || p.Y > r.Bottom + Grip) return Edges.None;
        var e = Edges.None;
        if (Math.Abs(p.X - r.Left) <= Grip) e |= Edges.Left; else if (Math.Abs(p.X - r.Right) <= Grip) e |= Edges.Right;
        if (Math.Abs(p.Y - r.Top) <= Grip) e |= Edges.Top; else if (Math.Abs(p.Y - r.Bottom) <= Grip) e |= Edges.Bottom;
        return e;
    }

    private void CanvasDown(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        _anchor = ToPicture(e.Location);
        _startArea = _area;
        _edges = HitEdges(e.Location);
        _drag = _edges != Edges.None ? Drag.Resize : CanMove(e.Location) ? Drag.Move : Drag.New;
    }

    private void CanvasMove(object? sender, MouseEventArgs e)
    {
        if (_drag == Drag.None)
        {
            _canvas.Cursor = HitEdges(e.Location) switch
            {
                Edges.Left or Edges.Right => Cursors.SizeWE,
                Edges.Top or Edges.Bottom => Cursors.SizeNS,
                Edges.Left | Edges.Top or Edges.Right | Edges.Bottom => Cursors.SizeNWSE,
                Edges.None => CanMove(e.Location) ? Cursors.SizeAll : Cursors.Cross,
                _ => Cursors.SizeNESW,
            };
            return;
        }

        var p = ToPicture(e.Location);
        bool lockShape = (ModifierKeys & Keys.Shift) != 0;
        float maxW = _img.Width, maxH = _img.Height;
        RectangleF r;
        switch (_drag)
        {
            case Drag.Move:
                r = _startArea;
                r.X = Math.Clamp(r.X + p.X - _anchor.X, 0, maxW - r.Width);
                r.Y = Math.Clamp(r.Y + p.Y - _anchor.Y, 0, maxH - r.Height);
                break;

            case Drag.New:
                r = FromCorner(_anchor, p, lockShape);
                break;

            default: // resize: the corner or edges opposite the grabbed ones stay put
                float left = _startArea.Left, top = _startArea.Top, right = _startArea.Right, bottom = _startArea.Bottom;
                if (_edges.HasFlag(Edges.Left)) left = p.X;
                if (_edges.HasFlag(Edges.Right)) right = p.X;
                if (_edges.HasFlag(Edges.Top)) top = p.Y;
                if (_edges.HasFlag(Edges.Bottom)) bottom = p.Y;
                if (lockShape)
                {
                    // Stretch away from the corner opposite the grabbed edges; a lone edge drags the other side along.
                    bool grabL = _edges.HasFlag(Edges.Left), grabT = _edges.HasFlag(Edges.Top);
                    var fixedCorner = new PointF(grabL ? _startArea.Right : _startArea.Left, grabT ? _startArea.Bottom : _startArea.Top);
                    float sx = (grabL ? left : right) >= fixedCorner.X ? 1 : -1, sy = (grabT ? top : bottom) >= fixedCorner.Y ? 1 : -1;
                    float w = Math.Abs(right - left), h = Math.Abs(bottom - top);
                    if ((_edges & (Edges.Top | Edges.Bottom)) == 0) h = w / ScreenAspect;
                    else if ((_edges & (Edges.Left | Edges.Right)) == 0) w = h * ScreenAspect;
                    r = FromCorner(fixedCorner, new PointF(fixedCorner.X + sx * w, fixedCorner.Y + sy * h), true);
                }
                else
                {
                    r = RectangleF.FromLTRB(Math.Min(left, right), Math.Min(top, bottom), Math.Max(left, right), Math.Max(top, bottom));
                }
                break;
        }

        if (r.Width < 2 || r.Height < 2) return; // too small to mean anything yet
        SetArea(r);
    }

    /// <summary>A rectangle from a fixed corner to the mouse, kept inside the picture (and screen-shaped if asked).</summary>
    private RectangleF FromCorner(PointF corner, PointF mouse, bool lockShape)
    {
        float dx = mouse.X - corner.X, dy = mouse.Y - corner.Y;
        float w = Math.Abs(dx), h = Math.Abs(dy);
        float roomW = dx >= 0 ? _img.Width - corner.X : corner.X, roomH = dy >= 0 ? _img.Height - corner.Y : corner.Y;
        if (lockShape)
        {
            if (w / ScreenAspect > h) h = w / ScreenAspect; else w = h * ScreenAspect;
            if (w > roomW) { w = roomW; h = w / ScreenAspect; }
            if (h > roomH) { h = roomH; w = h * ScreenAspect; }
        }
        else
        {
            w = Math.Min(w, roomW);
            h = Math.Min(h, roomH);
        }
        return new RectangleF(dx >= 0 ? corner.X : corner.X - w, dy >= 0 ? corner.Y : corner.Y - h, w, h);
    }

    // ---------------- painting ----------------

    private void PaintCanvas(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.FromArgb(32, 32, 32));
        g.DrawImage(_canvasImage, Rectangle.Round(_imageRect));

        var sel = ToCanvas(_area);
        using (var outside = new Region(_imageRect))
        using (var shade = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
        {
            outside.Exclude(sel);
            g.FillRegion(shade, outside);
        }
        using (var dark = new Pen(Color.Black))
            g.DrawRectangle(dark, sel.X - 1, sel.Y - 1, sel.Width + 1, sel.Height + 1);
        using (var line = new Pen(Color.White) { DashStyle = DashStyle.Dash })
            g.DrawRectangle(line, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
        foreach (var c in new[] { new PointF(sel.Left, sel.Top), new PointF(sel.Right, sel.Top), new PointF(sel.Left, sel.Bottom), new PointF(sel.Right, sel.Bottom) })
        {
            var handle = new RectangleF(c.X - 3, c.Y - 3, 6, 6);
            g.FillRectangle(Brushes.White, handle);
            g.DrawRectangle(Pens.Black, handle.X, handle.Y, handle.Width, handle.Height);
        }
    }

    private void PaintScreen(object? sender, PaintEventArgs e)
    {
        int i = Math.Min(_frame, _screens.Length - 1);
        if (_screens[i] == null)
        {
            if (_kept.Length > 1) _img.SelectActiveFrame(FrameDimension.Time, _kept[i]);
            _screens[i] = ImageCodec.Render(_img, _area);
        }
        e.Graphics.InterpolationMode = InterpolationMode.NearestNeighbor; // show the keyboard's real pixels
        e.Graphics.PixelOffsetMode = PixelOffsetMode.Half;
        e.Graphics.DrawImage(_screens[i]!, new Rectangle(Point.Empty, _screen.ClientSize));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _anim.Dispose();
            foreach (var b in _screens) b?.Dispose();
            _canvasImage.Dispose();
            _img.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>A flicker-free drawing surface.</summary>
    private sealed class Canvas : Control
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Margin = Padding.Empty;
        }
    }
}
