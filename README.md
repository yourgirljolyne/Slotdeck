# SlotDeck

**See and swap the pictures on your keyboard's screen. No iCUE required.**

The Corsair VANGUARD AIR 99 keeps the images and GIFs you upload in **slots** on the keyboard itself. SlotDeck is a small window that shows you what's in every slot and lets you add, change and remove them, without iCUE and without touching your lighting, macros or key assignments. In your profile it only changes the screen list (the screens FN + arrows step through).

> Not affiliated with or endorsed by Corsair. The screen protocol was worked out from Corsair's own public Web Hub app. Use at your own risk.

## What it does

- **See them all.** Every slot gets a thumbnail, and GIFs play in the preview.
- **Add a slot.** Press **Add…** and pick an image or GIF. It goes into the screen list, so FN + arrows reach it too, and it goes straight on the screen. A keyboard fresh out of the box has no slots of its own, so start here.
- **Replace a slot.** Pick an image or GIF (or drop one on the window). A preview opens: drag on the picture to choose the area you want, and see exactly what the 248×170 screen will show, black bars and all. Hold Shift for the screen's shape, or press *Fill the screen*. For GIFs, slow them down by a percentage or set your own time between frames. Long GIFs are trimmed to 100 frames. A still image takes about 2 seconds to write; a 40-frame GIF takes about a minute.
- **Remove a slot.** Select it and press **Remove**. Its pictures are deleted from the keyboard, and if it was on the screen the next slot takes its place.
- **Show a slot.** Double-click it, or step through them with **Previous** / **Next**.
- **No stray line.** Pictures uploaded with Corsair's Web Hub show a flickering 1-pixel line at the right edge of the screen. That's a bug in Web Hub (details below). Pictures written by SlotDeck don't have it.

## The Web Hub 1-pixel bug

Each picture on the keyboard is a 2-byte type tag followed by a BMP image. The BMP header says the pixels start 54 bytes in, and the keyboard counts that from the start of the BMP, so it reads from byte 56. Web Hub writes the pixels from byte 54, 2 bytes too early. The keyboard therefore shows every picture shifted one pixel to the left, and fills the last column with the first pixel of the next row, which shows up as a line that changes with every GIF frame.

SlotDeck writes the pixels where the keyboard reads them. Its previews read pictures the same way the keyboard does, so a Web Hub picture shows its shift in the app too. Replacing a slot with SlotDeck gets rid of the line.

## Get it running

1. Grab `SlotDeck.exe` from [Releases](../../releases), or build it yourself (below).
2. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
3. Plug the keyboard in with the **USB cable** and close iCUE.
4. Run `SlotDeck.exe`.

## Stuck on the Corsair logo? FN + arrows do nothing?

Same cause for both. The keyboard picks its screen at power-on from a **screen list** that belongs to the profile, and FN + arrows walk that same list. Web Hub only creates one when it imports a profile, so the default profile usually has none. With nothing to show, the keyboard spins its logo animation forever, until an app (SlotDeck, TrackDeck) sets a picture.

If that's you, SlotDeck shows a **Fix startup screen…** button. It adds a list with the built-in screen and your slots, exactly like Web Hub's. In the profile only the list pointer and its timestamp change; a backup goes to `%LOCALAPPDATA%\SlotDeck\backups` first. Replug the keyboard and it starts on your picture. Changed your mind? `SlotDeck screenlist remove` takes it out again. Details in [docs/vanguard-screen-protocol.md](docs/vanguard-screen-protocol.md).

## Plays nice with TrackDeck

If you run [TrackDeck](https://github.com/yourgirljolyne/TrackDeck) (the now-playing card), SlotDeck leaves its card out of the list, because TrackDeck rewrites it on every song. New slots go before it in the screen list, so the card stays last for FN + arrows. While TrackDeck is running, it puts its card back on the next track change.

## Command line

```
SlotDeck list                    list the slots (read-only)
SlotDeck export <folder>         save each slot's picture as a PNG (read-only)
SlotDeck show <n>                show slot n
SlotDeck add <file>              add a slot with an image or GIF, whole picture with black bars
SlotDeck replace <n> <file>      replace slot n with an image or GIF
                                 (add and replace: --fill crops to the screen; GIFs: --slow <percent> or --frame-ms <ms>)
SlotDeck remove <n>              remove slot n and its pictures
SlotDeck screenlist [create|remove]  show, add or remove the profile's screen list
SlotDeck dump <file id>...       print raw keyboard files as hex (read-only)
```

## Build it yourself

Needs the .NET 10 SDK on Windows. No NuGet packages.

```
cd SlotDeck
dotnet publish -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:SelfContained=false
```

## Known limits

- **USB cable only.** The SLIPSTREAM wireless receiver doesn't pass screen commands on (Corsair's own Web Hub can't either).
- Tested on the VANGUARD AIR 99 only.
- A picture you replace or remove is gone for good. SlotDeck backs up the small index files it changes to `%LOCALAPPDATA%\SlotDeck\backups`, not the pictures; run `SlotDeck export` first if you want copies.

## Credits

App icon: keyboard icon by [Icons8](https://icons8.com) (iOS 7 Icons set), used under the [Icons8 license](https://icons8.com/license/).

## License

[MIT](LICENSE) for the code. The app icon is under the Icons8 license above.
