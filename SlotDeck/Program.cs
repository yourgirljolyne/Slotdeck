
namespace SlotDeck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "list" or "dump" or "export" or "show" or "replace")
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
                    "replace" => Replace(int.Parse(args[1]), args[2], args.Contains("--fit")).GetAwaiter().GetResult(),
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

    /// <summary>Prints the slots on the keyboard. Read-only.</summary>
    private static async Task<int> List()
    {
        using var store = new SlotStore();
        var r = await store.ScanAsync(null);
        for (int i = 0; i < r.Slots.Count; i++)
        {
            var s = r.Slots[i];
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
        await store.ShowAsync(r.Slots[n - 1]);
        await store.CloseAsync();
        Console.WriteLine($"Slot {n} is on the screen.");
        return 0;
    }

    /// <summary>Replaces slot n (1-based, as in `list`) with an image or GIF file.</summary>
    private static async Task<int> Replace(int n, string file, bool fit)
    {
        var content = ImageCodec.Load(file, fit);
        using var store = new SlotStore { Log = s => Console.WriteLine("  · " + s) };
        var r = await store.ScanAsync(null);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var updated = await store.ReplaceAsync(r.Slots[n - 1], content, r.Slots, null);
        await store.CloseAsync();
        Console.WriteLine($"Slot {n} is now {updated.Kind} ({sw.Elapsed.TotalSeconds:0.0} s).");
        return 0;
    }

    /// <summary>Saves each slot's picture (first frame for GIFs) as a PNG. Read-only.</summary>
    private static async Task<int> Export(string dir)
    {
        using var store = new SlotStore();
        var r = await store.ScanAsync(null);
        Directory.CreateDirectory(dir);
        for (int i = 0; i < r.Slots.Count; i++)
        {
            var s = r.Slots[i];
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
