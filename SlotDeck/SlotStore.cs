using System.Buffers.Binary;

namespace SlotDeck;

/// <summary>
/// A screen slot: a small "entry" file that points at one image (type 56) or a list of GIF frames (type 102).
/// Showing a slot means copying its entry into the active-screen file, which is what Web Hub does.
/// </summary>
public sealed record Slot(ushort Id, bool IsGif, byte[] Config, ushort[] Resources, int DelayMs)
{
    public bool IsTrackDeck => Id == ScreenFiles.TrackDeckConfig;

    public string Kind => IsTrackDeck ? "TrackDeck card" : IsGif ? $"GIF, {Resources.Length} frames" : "Image";

    public static Slot? Parse(ushort id, byte[] d)
    {
        if (d.Length >= 4 && d[0] == ScreenFiles.ConfigTypeImage && d[1] == 0)
            return new Slot(id, false, d, [Le16(d, 2)], 0);
        if (d.Length >= 6 && d[0] == ScreenFiles.ConfigTypeGif && d[1] == 0)
        {
            int n = Le16(d, 4);
            if (n == 0 || d.Length < 6 + n * 2) return null;
            return new Slot(id, true, d, Enumerable.Range(0, n).Select(i => Le16(d, 6 + i * 2)).ToArray(), Le16(d, 2));
        }
        return null;
    }

    /// <summary>True if the active-screen file currently points at this slot.</summary>
    public bool IsShownBy(byte[]? active) =>
        active is { Length: >= 4 } && (active.AsSpan().SequenceEqual(Config) ||
            !IsGif && active[0] == ScreenFiles.ConfigTypeImage && Le16(active, 2) == Resources[0]);

    public static byte[] GifConfig(int delayMs, IReadOnlyList<ushort> frames)
    {
        var b = new byte[6 + frames.Count * 2];
        b[0] = ScreenFiles.ConfigTypeGif;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)delayMs);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4), (ushort)frames.Count);
        for (int i = 0; i < frames.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(6 + i * 2), frames[i]);
        return b;
    }

    internal static ushort Le16(byte[] b, int at) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at));
}

public sealed record ScanResult(List<Slot> Slots, ushort? ActiveId);

/// <summary>Resource map (file 61): u16 header, u16 count, then 8-byte items (u16 id, u16 address, 4-byte hash).</summary>
public static class ResourceMap
{
    public static List<byte[]> Items(byte[] map)
    {
        var items = new List<byte[]>();
        if (map.Length < 4) return items;
        int count = Slot.Le16(map, 2);
        for (int i = 0; i < count && 4 + i * 8 + 8 <= map.Length; i++) items.Add(map.AsSpan(4 + i * 8, 8).ToArray());
        return items;
    }

    public static HashSet<ushort> Ids(byte[] map) => Items(map).Select(i => Slot.Le16(i, 0)).Where(id => id != 0).ToHashSet();

    /// <summary>Returns the map with ids added/removed, or null if nothing changes.</summary>
    public static byte[]? Update(byte[] map, IEnumerable<ushort> add, IEnumerable<ushort> remove)
    {
        var items = Items(map);
        var drop = remove.ToHashSet();
        int before = items.Count;
        items.RemoveAll(i => drop.Contains(Slot.Le16(i, 0)));
        bool changed = items.Count != before;
        var have = items.Select(i => Slot.Le16(i, 0)).ToHashSet();
        foreach (var id in add.Where(id => !have.Contains(id)).Distinct())
        {
            var item = new byte[8];
            BinaryPrimitives.WriteUInt16LittleEndian(item, id);
            BinaryPrimitives.WriteUInt16LittleEndian(item.AsSpan(2), id);
            items.Add(item);
            changed = true;
        }
        if (!changed) return null;

        var result = new byte[4 + items.Count * 8];
        if (map.Length >= 2) map.AsSpan(0, 2).CopyTo(result); else result[0] = 0x44; // header as seen on the Air 99
        BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(2), (ushort)items.Count);
        for (int i = 0; i < items.Count; i++) items[i].CopyTo(result, 4 + i * 8);
        return result;
    }
}

/// <summary>
/// Reads and replaces screen slots. Only touches the slot's own entry and picture files, the resource map
/// and the active-screen file. Never profiles, lighting, macros or key settings.
/// </summary>
public sealed class SlotStore : IDisposable
{
    private const ushort FirstId = 28000;
    // New picture files are taken from the top of the user range, below TrackDeck's own two ids, so Web Hub
    // (which hands out the lowest free id from 28000) doesn't pick them. 30996-31000 are Web Hub device files.
    private const ushort AllocTop = ScreenFiles.TrackDeckConfig - 1, AllocBottom = 31001;
    private const ushort WebHubReserved = 28049;
    private const long StorageMargin = 5 * 1024 * 1024; // Web Hub keeps 5 MB free too

    private Bragi? _bragi;
    private readonly HashSet<ushort> _existing = [];
    private bool _backedUp;
    public Action<string>? Log { get; set; }

    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SlotDeck");

    private async Task<Bragi> ConnectAsync()
    {
        if (_bragi != null) return _bragi;
        _bragi = Bragi.Open() ?? throw new BragiException(NotFoundMessage());
        _bragi.Log = Log;
        await _bragi.StartSessionAsync();
        return _bragi;
    }

    private static string NotFoundMessage() =>
        Bragi.ReceiverIds.Any(pid => HidDevice.Describe(Bragi.Vid, pid).Any())
            ? "Keyboard is wireless. Plug in the USB cable to manage the screen."
            : "Keyboard not found. Plug it in with the USB cable.";

    /// <summary>For reads: if an existing connection fails (cable replugged), reconnect once and retry.</summary>
    private async Task<T> Read<T>(Func<Bragi, Task<T>> action)
    {
        bool had = _bragi != null;
        try { return await action(await ConnectAsync()); }
        catch (Exception e) when (had && e is BragiException or IOException)
        {
            Log?.Invoke("reconnecting after: " + e.Message);
            Drop();
            return await action(await ConnectAsync());
        }
    }

    /// <summary>For writes: never retried automatically (a half-done retry could leave stray files).</summary>
    private async Task<T> Write<T>(Func<Bragi, Task<T>> action)
    {
        try { return await action(await ConnectAsync()); }
        catch (Exception e) when (e is BragiException or IOException) { Drop(); throw; }
    }

    private void Drop() { _bragi?.Dispose(); _bragi = null; }

    /// <summary>Finds every image/GIF slot. Read-only.</summary>
    public Task<ScanResult> ScanAsync(IProgress<double>? progress, CancellationToken ct = default) => Read(async b =>
    {
        var mapIds = ResourceMap.Ids(await b.ReadFileAsync(ScreenFiles.ResourceMap) ?? []);
        // Slots sit near the pictures Web Hub wrote, so scan a little past the highest one in the low range.
        int highest = mapIds.Where(i => i < AllocBottom).Select(i => (int)i).DefaultIfEmpty(FirstId).Max();
        var ids = Enumerable.Range(FirstId, Math.Max(highest + 100, FirstId + 200) - FirstId + 1).Select(i => (ushort)i)
            .Append(ScreenFiles.TrackDeckConfig).ToList();

        _existing.Clear();
        _existing.UnionWith(mapIds);
        var slots = new List<Slot>();
        for (int k = 0; k < ids.Count; k++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report((k + 1) / (double)ids.Count);
            if (mapIds.Contains(ids[k])) continue; // a picture, not an entry
            var d = await b.ReadFileAsync(ids[k]);
            if (d == null) continue;
            _existing.Add(ids[k]);
            if (Slot.Parse(ids[k], d) is { } s) slots.Add(s);
        }
        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        return new ScanResult(slots, slots.FirstOrDefault(s => s.IsShownBy(active))?.Id);
    });

    public Task<byte[]?> ReadFileAsync(ushort id) => Read(b => b.ReadFileAsync(id));

    /// <summary>Puts a slot on the screen (copies its entry into the active-screen file).</summary>
    public Task ShowAsync(Slot slot) => Write(async b =>
    {
        await b.WriteFileAsync(ScreenFiles.ActiveScreen, slot.Config);
        Log?.Invoke($"showing slot {slot.Id}");
        return true;
    });

    /// <summary>
    /// Replaces a slot's picture, keeping the slot id. Reuses the slot's own picture files where it can,
    /// takes new ones for extra GIF frames and deletes the ones no longer needed.
    /// </summary>
    public Task<Slot> ReplaceAsync(Slot slot, ScreenContent content, IReadOnlyList<Slot> allSlots, IProgress<double>? progress) => Write(async b =>
    {
        if (slot.IsTrackDeck) throw new InvalidOperationException("TrackDeck manages that slot itself.");
        var map = await b.ReadFileAsync(ScreenFiles.ResourceMap) ?? [];
        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        BackupOnce(map, slot);

        // Only reuse pictures no other slot points at.
        var shared = allSlots.Where(s => s.Id != slot.Id).SelectMany(s => s.Resources).ToHashSet();
        var reusable = slot.Resources.Distinct().Where(id => !shared.Contains(id)).ToList();
        int extra = Math.Max(0, content.Packages.Count - reusable.Count);
        long free = await b.GetPropertyAsync(Bragi.PropFreeStorage);
        long need = extra * (long)content.Packages[0].Length;
        if (need > free - StorageMargin)
            throw new BragiException($"Not enough space on the keyboard ({need / 1048576.0:0.0} MB needed, {Math.Max(0, free - StorageMargin) / 1048576.0:0.0} MB free).");

        var ids = new List<ushort>(reusable.Take(content.Packages.Count));
        while (ids.Count < content.Packages.Count) ids.Add(await AllocateAsync(b));

        for (int i = 0; i < ids.Count; i++)
        {
            await b.WriteFileAsync(ids[i], content.Packages[i]);
            progress?.Report((i + 1) / (double)(ids.Count + 1));
        }
        var leftovers = reusable.Skip(ids.Count).ToList();
        var newMap = ResourceMap.Update(map, ids, leftovers);
        if (newMap != null) await b.WriteFileAsync(ScreenFiles.ResourceMap, newMap);

        var config = content.IsGif ? Slot.GifConfig(content.DelayMs, ids) : ScreenFiles.ImageConfig(ids[0]);
        await b.WriteFileAsync(slot.Id, config);
        if (slot.IsShownBy(active)) await b.WriteFileAsync(ScreenFiles.ActiveScreen, config);

        foreach (var id in leftovers) { await b.DeleteFileAsync(id); _existing.Remove(id); }
        progress?.Report(1);
        Log?.Invoke($"slot {slot.Id} replaced: {ids.Count} picture file(s), {leftovers.Count} removed");
        return Slot.Parse(slot.Id, config)!;
    });

    private async Task<ushort> AllocateAsync(Bragi b)
    {
        for (int id = AllocTop; id >= AllocBottom; id--)
        {
            var u = (ushort)id;
            if (u == WebHubReserved || _existing.Contains(u)) continue;
            if (await b.ReadFileAsync(u) != null) { _existing.Add(u); continue; } // outside what we scanned
            _existing.Add(u);
            return u;
        }
        throw new BragiException("No free file ids left on the keyboard.");
    }

    /// <summary>Keeps a copy of the small files we change (resource map, slot entry) once per run.</summary>
    private void BackupOnce(byte[] map, Slot slot)
    {
        var dir = Path.Combine(DataDir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        if (!_backedUp) File.WriteAllBytes(Path.Combine(dir, $"file-{ScreenFiles.ResourceMap}.bin"), map);
        File.WriteAllBytes(Path.Combine(dir, $"file-{slot.Id}.bin"), slot.Config);
        _backedUp = true;
    }

    /// <summary>Ends the keyboard session (never waits more than 2 s) and releases the device.</summary>
    public async Task CloseAsync()
    {
        if (_bragi == null) return;
        try { await Task.WhenAny(_bragi.StopSessionAsync(), Task.Delay(2000)); } catch { }
        Drop();
    }

    public void Dispose() => Drop();
}
