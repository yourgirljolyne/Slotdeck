# SlotDeck (source)

C# / .NET 10 WinForms window that lists, previews, shows and replaces the screen's image/GIF slots.
`Hid.cs`, `Bragi.cs` and `ScreenFiles.cs` started life in [TrackDeck](https://github.com/yourgirljolyne/TrackDeck) and are kept in step by hand.

| File | What it does |
|---|---|
| `Hid.cs` | Finds and talks to HID interfaces via SetupAPI and hid.dll |
| `Bragi.cs` | Corsair Bragi v1.5 client: session, get/set property, read/write files |
| `ScreenFiles.cs` | File ids and the image package format |
| `SlotStore.cs` | Finds slots, reads pictures, replaces a slot (reuses its picture files, allocates or deletes extras), shows a slot |
| `ImageCodec.cs` | Image/GIF file to 248×170 RGB565 packages, and keyboard packages back to bitmaps |
| `MainForm.cs` | The window: thumbnails, animated preview, Previous/Next, replace with confirmation, drag and drop |
| `Program.cs` | Entry point and command-line tools (`list`, `export`, `show`, `replace`, `dump`) |

Files written on the keyboard: the slot's entry file, its picture files, `61` (resource map) and `62` (active screen). Never profiles, lighting, key assignments or factory reset.

App data (backups of the resource map and replaced slot entries): `%LOCALAPPDATA%\SlotDeck\`.
