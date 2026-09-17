# ArmorQuickWear (AQW)

Mount your armor on a panel in the character screen, then put all of it on or take all of it off with one hotkey.

| | |
|---|---|
| modid | `lionfoxarmorquickwear` |
| version | 1.0.0 |
| game dependency | 1.22.6 |
| type | code (C#), universal: install on the server, clients get it automatically |

## Using it

- Open the character screen (`C`) and switch to the **Quick Wear** tab.
- Pick up a piece of armor, then click a slot on the tab to mount it. The piece stays on your cursor, so put it back in your bags afterward. Click a mounted slot with an empty hand to unmount it.
- **Mount worn armor** adds everything you're wearing. **Clear panel** empties the panel.
- Bind the hotkeys in ESC > Controls. They're unbound by default.
  - *Quick Wear: put on or take off armor* puts on any mounted pieces that are in your bags. If none are, it takes everything off.
  - *Quick Wear: put on armor* and *Quick Wear: take off armor* only go one way.
- Slot colors: green means worn, plain means in your bags, and red with a cross means it isn't on you.

## How it behaves

- The panel is not an inventory. It keeps a copy of each piece's kind and look. The real items stay in your bags or on your body, and AQW never changes them.
- Any piece of the same kind can stand in for a mounted one. A piece with the exact look (such as the same color) is picked first, then the one with the most durability left.
- **Putting on:** pieces come out of the backpack or hotbar and go into the first armor slot that accepts them. If armor you're already wearing is in the way, that piece is skipped and you get a message.
- **Taking off:** each piece goes back to the slot it came from, or to any free backpack slot. If your bags are full, the piece stays on. Nothing is ever dropped on the ground.
- A piece with items inside is never moved, because Combat Overhaul would spill them on the ground.
- The server makes every move with the same slot rules a manual drag uses, so Combat Overhaul's layer and zone checks still apply. AQW never references Combat Overhaul's assembly, so a CO update can't break it.
- Loadouts are saved per player, per world.

## Building

Requires a Vintage Story install to reference the game assemblies. Set the
`VINTAGE_STORY` environment variable to your install directory, then:

```
dotnet build -c Release
```

Or override it for one build: `dotnet build -c Release -p:VS_PATH=D:\Vintagestory`

The build writes `lionfox_ArmorQuickWear.dll` to the repo root (the root is the
loadable mod folder) and packages `build/lionfoxarmorquickwear_<version>.zip`.
