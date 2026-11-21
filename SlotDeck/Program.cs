
namespace SlotDeck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "list" or "dump" or "export" or "show" or "replace" or "add" or "remove" or "screenlist")
        {
            AttachConsole(-1);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            try
            {
                return cmd switch
                {
                    "list" => List().GetAwaiter().GetResult(),
                    "export" => Export(args.ElementAtOrDefault(1) ?? "slots").GetAwaiter().GetResult(),
                    "show" => Show(int.Parse(args[1])).GetAwaiter().GetResult(),
                    "screenlist" => ScreenList(args.ElementAtOrDefault(1)).GetAwaiter().GetResult(),
                    "replace" => Replace(int.Parse(args[1]), args[2], args).GetAwaiter().GetResult(),
                    "add" => Add(args[1], args).GetAwaiter().GetResult(),
                    "remove" => Remove(int.Parse(args[1])).GetAwaiter().GetResult(),
                    _ => Dump(args.Skip(1).Select(ushort.Parse).ToArray()).GetAwaiter().GetResult(),
                };
            }
            catch (BragiException e) { Console.WriteLine(e.Message); return 1; }
        }

        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, "SlotDeck.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("SlotDeck is already open.", "SlotDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.Run(new MainForm());
        return 0;
    }

    /// <summary>The slots as the window lists them (TrackDeck's card left out), numbered from 1.</summary>
    private static List<Slot> Listed(ScanResult r) => r.Slots.Where(s => !s.IsTrackDeck).ToList();

    /// <summary>Prints the slots on the keyboard. Read-only.</summary>
    private static async Task<int> List()
    {
        using var store = new SlotStore();
        var r = await store.ScanAsync(null);
        var slots = Listed(r);
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            Console.WriteLine($"{i + 1,2}. file {s.Id}  {s.Kind,-22} {(s.IsGif ? s.DelayMs + " ms" : ""),-7}" +
                $" pictures {string.Join(",", s.Resources.Take(4))}{(s.Resources.Length > 4 ? ",…" : "")}" +
                (s.Id == r.ActiveId ? "   <- on screen" : ""));
        }
        await store.CloseAsync();
        return 0;
    }

    /// <summary>Puts slot n (1-based, as in `list`) on the screen.</summary>
    private static async Task<int> Show(int n)
    {
        using var store = new SlotStore();
        var r = await store.ScanAsync(null);
        await store.ShowAsync(Listed(r)[n - 1]);
        await store.CloseAsync();
        Console.WriteLine($"Slot {n} is on the screen.");
        return 0;
    }

    /// <summary>
    /// Replaces slot n (1-based, as in `list`) with an image or GIF file: the whole picture with black bars,
    /// or --fill to crop it to the screen. GIFs: --slow &lt;percent&gt; or --frame-ms &lt;ms&gt;.
    /// </summary>
    private static async Task<int> Replace(int n, string file, string[] args)
    {
        var content = Content(file, args);
        using var store = new SlotStore { Log = s => Console.WriteLine("  · " + s) };
        var r = await store.ScanAsync(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var updated = await store.ReplaceAsync(Listed(r)[n - 1], content, r.Slots, null);
        await store.CloseAsync();
        Console.WriteLine($"Slot {n} is now {updated.Kind} ({sw.Elapsed.TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>The file as the keyboard will get it, with the --fill / --slow / --frame-ms options.</summary>
    private static ScreenContent Content(string file, string[] args)
    {
        int? Option(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? int.Parse(args[i + 1]) : null;
        System.Drawing.RectangleF? area = null;
        if (args.Contains("--fill")) using (var img = ImageCodec.Open(file)) area = ImageCodec.FillScreen(img);
        return ImageCodec.Load(file, area, new GifTiming(Option("--slow") ?? 0, Option("--frame-ms")));
    }

    /// <summary>Adds a new slot with an image or GIF file (same options as replace) and shows it.</summary>
    private static async Task<int> Add(string file, string[] args)
    {
        var content = Content(file, args);
        using var store = new SlotStore { Log = s => Console.WriteLine("  · " + s) };
        var r = await store.ScanAsync(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var added = await store.AddAsync(content, r.Slots, null);
        await store.CloseAsync();
        Console.WriteLine($"Added slot {Listed(r).Count + 1}: file {added.Id}, {added.Kind} ({sw.Elapsed.TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>Removes slot n (1-based, as in `list`) and its pictures.</summary>
    private static async Task<int> Remove(int n)
    {
        using var store = new SlotStore { Log = s => Console.WriteLine("  · " + s) };
        var r = await store.ScanAsync(null);
        var slots = Listed(r);
        var slot = slots[n - 1];
        var others = slots.Where(s => s.Id != slot.Id).ToList();
        await store.RemoveAsync(slot, r.Slots, others.Count == 0 ? null : others[Math.Min(n - 1, others.Count - 1)]);
        await store.CloseAsync();
        Console.WriteLine($"Removed slot {n} (file {slot.Id}).");
        return 0;
    }

    /// <summary>Shows the profile's screen list; "create" makes one from the slots, "remove" takes it away again.</summary>
    private static async Task<int> ScreenList(string? action)
    {
        using var store = new SlotStore { Log = s => Console.WriteLine("  · " + s) };
        var r = await store.ScanAsync(null);
        if (action == "create") Console.WriteLine($"Screen list created: file {await store.CreateScreenListAsync(r.Slots)}.");
        else if (action == "remove") Console.WriteLine($"Screen list removed (was file {await store.RemoveScreenListAsync()}).");
        var list = await store.ReadScreenListAsync();
        Console.WriteLine(list == null ? "The profile has no screen list." : "Screen list: " + string.Join(", ", list));
        await store.CloseAsync();
        return 0;
    }

    /// <summary>Saves each slot's picture (first frame for GIFs) as a PNG. Read-only.</summary>
    private static async Task<int> Export(string dir)
    {
        using var store = new SlotStore();
        var r = await store.ScanAsync(null);
        Directory.CreateDirectory(dir);
        var slots = Listed(r);
        for (int i = 0; i < slots.Count; i++)
        {
            var s = slots[i];
            var data = await store.ReadFileAsync(s.Resources[0]);
            using var bmp = data == null ? null : ImageCodec.Decode(data);
            if (bmp == null) { Console.WriteLine($"slot {i + 1}: can't decode"); continue; }
            string path = Path.Combine(dir, $"slot-{i + 1}-{s.Id}.png");
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"slot {i + 1}: {Path.GetFullPath(path)}");
        }
        await store.CloseAsync();
        return 0;
    }

    /// <summary>Prints files from the keyboard as hex and saves them to the dumps folder. Read-only.</summary>
    private static async Task<int> Dump(ushort[] ids)
    {
        using var store = new SlotStore();
        foreach (var id in ids)
        {
            var d = await store.ReadFileAsync(id);
            Console.WriteLine($"{id}: {(d == null ? "(none)" : d.Length + " bytes " + Convert.ToHexString(d.AsSpan(0, Math.Min(200, d.Length))))}");
            if (d != null)
            {
                var dir = Path.Combine(SlotStore.DataDir, "dumps");
                Directory.CreateDirectory(dir);
                File.WriteAllBytes(Path.Combine(dir, $"file-{id}.bin"), d);
            }
        }
        await store.CloseAsync();
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
}
