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

/// <summary>Slots in screen-list order (then any not in the list), the one on screen, and the list itself (null if none).</summary>
public sealed record ScanResult(List<Slot> Slots, ushort? ActiveId, ushort[]? ScreenList);

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
/// Reads, adds, replaces and removes screen slots. Touches the slots' own entry and picture files, the resource
/// map and the active-screen file; in the active profile only its screen list, the stored screen position and the
/// cookie. Never lighting, macros or key settings.
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
        _existing.Clear();
        _existing.UnionWith(mapIds);

        // The profile's screen list names every slot FN + arrows reach, wherever its id is (SlotDeck's own are high up).
        ushort[]? screens = null;
        try
        {
            var p = await ReadProfileAsync(b);
            if (p.LayoutId != 0) { _existing.Add(p.LayoutId); screens = ParseLayout(await b.ReadFileAsync(p.LayoutId)); }
        }
        catch (BragiException e) { Log?.Invoke("screen list not read: " + e.Message); }

        // Web Hub's slots sit near its pictures, so scan a little past the highest one in the low range.
        int highest = mapIds.Where(i => i < AllocBottom).Select(i => (int)i).DefaultIfEmpty(FirstId).Max();
        var ids = Enumerable.Range(FirstId, Math.Max(highest + 100, FirstId + 200) - FirstId + 1).Select(i => (ushort)i)
            .Concat(screens ?? []).Append(ScreenFiles.TrackDeckConfig).Distinct().ToList();

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
        int Position(Slot s) => screens != null && Array.IndexOf(screens, s.Id) is int i and >= 0 ? i : int.MaxValue;
        slots = slots.OrderBy(Position).ThenBy(s => s.Id).ToList();
        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        return new ScanResult(slots, slots.FirstOrDefault(s => s.IsShownBy(active))?.Id, screens);
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

    /// <summary>
    /// Adds a new slot: writes its pictures and entry, lists them in the resource map, adds the entry to the
    /// profile's screen list (creating the list if the profile has none) and puts it on the screen.
    /// </summary>
    public Task<Slot> AddAsync(ScreenContent content, IReadOnlyList<Slot> allSlots, IProgress<double>? progress) => Write(async b =>
    {
        var map = await b.ReadFileAsync(ScreenFiles.ResourceMap) ?? [];
        long free = await b.GetPropertyAsync(Bragi.PropFreeStorage);
        long need = content.Packages.Count * (long)content.Packages[0].Length;
        if (need > free - StorageMargin)
            throw new BragiException($"Not enough space on the keyboard ({need / 1048576.0:0.0} MB needed, {Math.Max(0, free - StorageMargin) / 1048576.0:0.0} MB free).");
        var (p, screens) = await ReadScreensAsync(b);
        var list = (screens ?? [BuiltInScreen, .. allSlots.Select(s => s.Id)]).ToList();
        if (list.Count >= 255) throw new InvalidOperationException("The screen list is full (255 screens).");
        Log?.Invoke("backup: " + BackupFiles("add", (ScreenFiles.ResourceMap, map), (p.Id, p.Bytes)));

        var ids = new List<ushort>();
        for (int i = 0; i < content.Packages.Count; i++)
        {
            ids.Add(await AllocateAsync(b));
            await b.WriteFileAsync(ids[i], content.Packages[i]);
            progress?.Report((i + 1) / (double)(content.Packages.Count + 1));
        }
        var newMap = ResourceMap.Update(map, ids, []);
        if (newMap != null) await b.WriteFileAsync(ScreenFiles.ResourceMap, newMap);

        ushort entryId = await AllocateAsync(b);
        var config = content.IsGif ? Slot.GifConfig(content.DelayMs, ids) : ScreenFiles.ImageConfig(ids[0]);
        await b.WriteFileAsync(entryId, config);
        if (!(await b.ReadFileAsync(entryId) ?? []).AsSpan().SequenceEqual(config))
            throw new BragiException($"The new slot (file {entryId}) didn't read back correctly.");

        // New slots go before TrackDeck's card, which stays last.
        int at = list.IndexOf(ScreenFiles.TrackDeckConfig);
        list.Insert(at < 0 ? list.Count : at, entryId);
        await HostControlledAsync(b, () => SetScreensAsync(b, p, screens, list));

        await b.WriteFileAsync(ScreenFiles.ActiveScreen, config);
        progress?.Report(1);
        Log?.Invoke($"slot {entryId} added: {ids.Count} picture file(s)");
        return Slot.Parse(entryId, config)!;
    });

    /// <summary>
    /// Removes a slot: puts <paramref name="showInstead"/> on the screen if the slot is showing, takes it out of the
    /// profile's screen list, then deletes its entry and the pictures no other slot uses.
    /// </summary>
    public Task RemoveAsync(Slot slot, IReadOnlyList<Slot> allSlots, Slot? showInstead) => Write(async b =>
    {
        if (slot.IsTrackDeck) throw new InvalidOperationException("TrackDeck manages that slot itself.");
        var map = await b.ReadFileAsync(ScreenFiles.ResourceMap) ?? [];
        var shared = allSlots.Where(s => s.Id != slot.Id).SelectMany(s => s.Resources).ToHashSet();
        var drop = slot.Resources.Distinct().Where(id => !shared.Contains(id)).ToList();
        var (p, screens) = await ReadScreensAsync(b);
        Log?.Invoke("backup: " + BackupFiles("remove", (ScreenFiles.ResourceMap, map), (slot.Id, slot.Config), (p.Id, p.Bytes)));

        if (slot.IsShownBy(await b.ReadFileAsync(ScreenFiles.ActiveScreen)))
        {
            var instead = showInstead?.Config ?? await b.ReadFileAsync(BuiltInScreen);
            if (instead != null) await b.WriteFileAsync(ScreenFiles.ActiveScreen, instead);
        }
        if (screens != null && screens.Contains(slot.Id))
            await HostControlledAsync(b, () => SetScreensAsync(b, p, screens, screens.Where(id => id != slot.Id).ToList()));

        await b.DeleteFileAsync(slot.Id);
        _existing.Remove(slot.Id);
        var newMap = ResourceMap.Update(map, [], drop);
        if (newMap != null) await b.WriteFileAsync(ScreenFiles.ResourceMap, newMap);
        foreach (var id in drop) { await b.DeleteFileAsync(id); _existing.Remove(id); }
        Log?.Invoke($"slot {slot.Id} removed with {drop.Count} picture file(s)");
        return true;
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

    // ---------------- the profile's screen list ----------------

    private const ushort ProfileList = 15;
    private const byte LayoutHeader = 55;
    /// <summary>The keyboard's built-in screen; Web Hub always puts it first in a profile's screen list.</summary>
    public const ushort BuiltInScreen = 28007;

    /// <summary>The active profile's file, and where its screen-list pointer sits.</summary>
    private sealed record ProfileFile(ushort Id, byte[] Bytes, int LayoutAt)
    {
        public ushort LayoutId => Slot.Le16(Bytes, LayoutAt);
        public ushort PropertiesId => Slot.Le16(Bytes, 8);
    }

    private static async Task<ProfileFile> ReadProfileAsync(Bragi b)
    {
        var list = await b.ReadFileAsync(ProfileList) ?? throw new BragiException("The keyboard has no profile list.");
        int count = list.Length >= 4 ? Slot.Le16(list, 2) : 0;
        if (count == 0 || list.Length < 4 + count * 2) throw new BragiException("The keyboard has no profiles.");
        // Only ever the profile the keyboard is using right now; other profiles are left alone.
        uint active = await b.GetPropertyAsync(Bragi.PropActiveProfile);
        if (active >= count) throw new BragiException($"The keyboard reports active profile {active + 1}, but has only {count}.");
        ushort id = Slot.Le16(list, 4 + (int)active * 2);
        var p = await b.ReadFileAsync(id) ?? throw new BragiException($"Profile file {id} is missing.");
        // Profile: header, cookie (2-5), file pointers, name length (30), name + 0, then 4 pointers; the screen list is last.
        int layoutAt = p.Length >= 32 ? 33 + Slot.Le16(p, 30) + 6 : int.MaxValue;
        if (p.Length < layoutAt + 2) throw new BragiException($"Profile file {id} has a layout SlotDeck doesn't know.");
        return new ProfileFile(id, p, layoutAt);
    }

    /// <summary>Screen list file: 55 0, row count, then per row: count and u16 entry ids.</summary>
    public static byte[] LayoutFile(IReadOnlyList<ushort> ids)
    {
        if (ids.Count > 255) throw new ArgumentException("Too many screens for one row.");
        var b = new byte[4 + ids.Count * 2];
        b[0] = LayoutHeader;
        b[2] = 1;
        b[3] = (byte)ids.Count;
        for (int i = 0; i < ids.Count; i++) BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(4 + i * 2), ids[i]);
        return b;
    }

    public static ushort[]? ParseLayout(byte[]? d)
    {
        if (d is not { Length: >= 4 } || d[0] != LayoutHeader || d[2] < 1 || d.Length < 4 + d[3] * 2) return null;
        return Enumerable.Range(0, d[3]).Select(i => Slot.Le16(d, 4 + i * 2)).ToArray();
    }

    /// <summary>The profile's screen list (null if it has none). Read-only.</summary>
    public Task<ushort[]?> ReadScreenListAsync() => Read(async b =>
    {
        var p = await ReadProfileAsync(b);
        return p.LayoutId == 0 ? null : ParseLayout(await b.ReadFileAsync(p.LayoutId));
    });

    /// <summary>
    /// Gives the profile a screen list (the built-in screen, then the slots), like Web Hub does when it imports a
    /// profile. Without one the keyboard has nothing to show at power-on and FN + arrows do nothing.
    /// Changes only the list pointer and the cookie in the profile file; backs everything up first.
    /// </summary>
    public Task<ushort> CreateScreenListAsync(IReadOnlyList<Slot> slots) => Write(async b =>
    {
        bool switched = await b.EnterHostControlAsync();
        try
        {
            var p = await ReadProfileAsync(b);
            if (p.LayoutId != 0) throw new InvalidOperationException($"Profile {p.Id} already has a screen list (file {p.LayoutId}).");
            Log?.Invoke("backup: " + BackupFiles("screen-list", (ProfileList, await b.ReadFileAsync(ProfileList)), (p.Id, p.Bytes),
                (p.PropertiesId, await b.ReadFileAsync(p.PropertiesId))));

            return await SetScreensAsync(b, p, null, [BuiltInScreen, .. slots.Select(s => s.Id)]);
        }
        finally { await b.LeaveHostControlAsync(switched); }
    });

    /// <summary>Undo: sets the profile's list pointer back to 0 and deletes the list file.</summary>
    public Task<ushort> RemoveScreenListAsync() => Write(async b =>
    {
        bool switched = await b.EnterHostControlAsync();
        try
        {
            var p = await ReadProfileAsync(b);
            ushort layoutId = p.LayoutId;
            if (layoutId == 0) return (ushort)0;
            Log?.Invoke("backup: " + BackupFiles("screen-list-removed", (p.Id, p.Bytes), (layoutId, await b.ReadFileAsync(layoutId))));
            await WriteProfileAsync(b, p, 0);
            await b.DeleteFileAsync(layoutId);
            return layoutId;
        }
        finally { await b.LeaveHostControlAsync(switched); }
    });

    /// <summary>The active profile and its screen list (null if it has none).</summary>
    private static async Task<(ProfileFile Profile, ushort[]? Screens)> ReadScreensAsync(Bragi b)
    {
        var p = await ReadProfileAsync(b);
        if (p.LayoutId == 0) return (p, null);
        return (p, ParseLayout(await b.ReadFileAsync(p.LayoutId))
            ?? throw new BragiException($"The screen list (file {p.LayoutId}) has a format SlotDeck doesn't know."));
    }

    private static async Task HostControlledAsync(Bragi b, Func<Task> action)
    {
        bool switched = await b.EnterHostControlAsync();
        try { await action(); }
        finally { await b.LeaveHostControlAsync(switched); }
    }

    /// <summary>
    /// Writes the profile's screen list (to a new file if it has none), keeps the stored screen position on the
    /// same screen, and gives the profile a new cookie so the keyboard reloads it. Call in host-controlled mode.
    /// </summary>
    private async Task<ushort> SetScreensAsync(Bragi b, ProfileFile p, ushort[]? old, List<ushort> ids)
    {
        var layout = LayoutFile(ids);
        ushort layoutId = p.LayoutId != 0 ? p.LayoutId : await AllocateAsync(b);
        if (p.LayoutId != 0)
            Log?.Invoke("backup: " + BackupFiles("screen-list-change", (layoutId, await b.ReadFileAsync(layoutId)),
                (p.PropertiesId, await b.ReadFileAsync(p.PropertiesId))));
        await b.WriteFileAsync(layoutId, layout);
        if (!(await b.ReadFileAsync(layoutId) ?? []).AsSpan().SequenceEqual(layout))
            throw new BragiException($"The screen list (file {layoutId}) didn't read back correctly.");
        Log?.Invoke($"screen list {layoutId}: {Convert.ToHexString(layout)}");
        if (old != null) await KeepScreenIndexAsync(b, p, old, ids);
        await WriteProfileAsync(b, p, layoutId);
        return layoutId;
    }

    private const ushort PropScreenIndex = 263;

    /// <summary>
    /// The profile's property map stores a position in the screen list (property 263). After the list changes,
    /// points it at the same screen again, or at the first one if that screen is gone. Changes nothing else.
    /// </summary>
    private async Task KeepScreenIndexAsync(Bragi b, ProfileFile p, ushort[] old, List<ushort> ids)
    {
        var props = await b.ReadFileAsync(p.PropertiesId);
        if (props is not { Length: >= 4 }) return;
        int count = Slot.Le16(props, 2);
        for (int i = 0; i < count && 4 + i * 6 + 6 <= props.Length; i++)
        {
            int at = 4 + i * 6;
            if (Slot.Le16(props, at) != PropScreenIndex) continue;
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(props.AsSpan(at + 2));
            int same = index < old.Length ? ids.IndexOf(old[index]) : index < ids.Count ? (int)index : -1;
            uint want = same < 0 ? 0 : (uint)same;
            if (want == index) return;
            var updated = (byte[])props.Clone();
            BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(at + 2), want);
            await b.WriteFileAsync(p.PropertiesId, updated);
            if (!(await b.ReadFileAsync(p.PropertiesId) ?? []).AsSpan().SequenceEqual(updated))
                throw new BragiException($"The profile's properties (file {p.PropertiesId}) didn't read back correctly. A backup is in {Path.Combine(DataDir, "backups")}.");
            Log?.Invoke($"screen position {index} -> {want}");
            return;
        }
    }

    /// <summary>Sets the list pointer and a new cookie (so the keyboard reloads the profile), nothing else.</summary>
    private async Task WriteProfileAsync(Bragi b, ProfileFile p, ushort layoutId)
    {
        var fresh = await b.ReadFileAsync(p.Id);
        if (fresh == null || !fresh.AsSpan().SequenceEqual(p.Bytes))
            throw new BragiException($"Profile {p.Id} changed while SlotDeck was working; nothing written to it.");
        var updated = (byte[])p.Bytes.Clone();
        uint old = BinaryPrimitives.ReadUInt32LittleEndian(p.Bytes.AsSpan(2)), now = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        BinaryPrimitives.WriteUInt32LittleEndian(updated.AsSpan(2), now == old ? now + 1 : now);
        BinaryPrimitives.WriteUInt16LittleEndian(updated.AsSpan(p.LayoutAt), layoutId);
        for (int i = 0; i < updated.Length; i++)
            if (updated[i] != p.Bytes[i] && i is not (>= 2 and <= 5) && i != p.LayoutAt && i != p.LayoutAt + 1)
                throw new InvalidOperationException("Profile update would change more than the list pointer and cookie.");
        await b.WriteFileAsync(p.Id, updated);
        if (!(await b.ReadFileAsync(p.Id) ?? []).AsSpan().SequenceEqual(updated))
            throw new BragiException($"Profile {p.Id} didn't read back correctly. A backup is in {Path.Combine(DataDir, "backups")}.");
        Log?.Invoke($"profile {p.Id}: screen list -> {layoutId}");
    }

    private static string BackupFiles(string what, params (ushort Id, byte[]? Data)[] files)
    {
        var dir = Path.Combine(DataDir, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + what);
        Directory.CreateDirectory(dir);
        foreach (var (id, data) in files)
            if (data != null) File.WriteAllBytes(Path.Combine(dir, $"file-{id}.bin"), data);
        return dir;
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
