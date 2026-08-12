# pixelReel for Vintage Story

Place projectors in your world and watch **Jellyfin** movies and TV together with
friends. Each projector throws a floating screen, several can play at once, and
audio falls off with distance so a cinema screen carries across a room while a
small projector doesn't bleed into the next building.

A port of [pixelReel]([https://github.com/Samarth-programming/PixelReel_1.21.1]),
the Fabric mod for Minecraft 1.21.1. Rewritten in C# against the Vintage Story API —
no Java code carried over — but the design, the display dimensions, and the curved
screen maths come straight from the original.

## Requirements

| Requirement | Notes |
| --- | --- |
| **Vintage Story 1.22+** | Built against the current stable line. |
| **.NET 10** | Required to build. Vintage Story 1.22 moved to it from .NET 8. |
| **VLC (64-bit)** | Needed on each **client** for video and audio. Get it from [videolan.org](https://www.videolan.org). |
| **A Jellyfin server** | Anything reachable from the game server. |

The 64-bit requirement is not optional: Vintage Story is a 64-bit process and cannot
load a 32-bit `libvlc.dll`. If you have VLC in `Program Files (x86)`, that's the
32-bit build and it won't work.

Without VLC the mod still loads, menus and commands still work, and projectors
report their state — you just get no picture. Run `.tv status` to see why.

If VLC is somewhere unusual, set `VlcPath` in the config to the folder containing
`libvlc.dll`.

## Building

```bat
dotnet build -c Release
```

The project locates your Vintage Story install automatically. If it can't:

```bat
dotnet build -c Release -p:GameDir="C:\Users\you\AppData\Roaming\Vintagestory"
```

That's the folder containing `VintagestoryAPI.dll`, not `VintagestoryData`.

The build packages itself straight into your mods folder as `pixelreel.zip`. There's
no separate install step — `LibVLCSharp.dll` comes from NuGet and is bundled in.

## Configuration

Written on first launch to `ModConfig/pixelreel.json`.

### Jellyfin (server side)

| Key | Default | Meaning |
| --- | --- | --- |
| `JellyfinUrl` | `""` | Server URL, e.g. `https://watch.example.com` or `http://192.168.1.50:8096`. No trailing slash. |
| `JellyfinApiKey` | `""` | From Jellyfin's Dashboard → API Keys. |
| `JellyfinUserId` | `""` | Dashboard → Users → click a user → copy `userId=` from the address bar. |
| `RequestTimeoutSeconds` | `15` | How long to wait on Jellyfin before giving up. |
| `AutoplayNextEpisode` | `true` | Roll into the next episode when one finishes. |
| `SubtitlesEnabled` | `true` | Fetch and display subtitles when available. |
| `SubtitleLanguage` | `"eng"` | Three-letter code. Falls back to the default or first track. |

**Make a dedicated Jellyfin user** with access only to the libraries you want in
game, and use that user's key. Not just tidier — see the security note below.

### Video and audio (client side)

| Key | Default | Meaning |
| --- | --- | --- |
| `VlcPath` | `""` | Folder holding `libvlc.dll`. Empty means autodetect. |
| `VlcOptions` | *(see file)* | Passed straight to libvlc. `--network-caching` matters most; raise it for remote servers. |
| `MaxDecodeHeight` | `1080` | Decode cap. 4K costs ~33 MB of VRAM per screen. |
| `HardwareDecoding` | `false` | Leave off. See the note below. |
| `ScreenBrightness` | `1.0` | 1.0 shows the video's own colours. Lower to dim. |
| `VerticalOffsetBlocks` | `1.5` | How far above the projector the image starts. |
| `ForwardOffsetBlocks` | `1.0` | How far in front the image hangs. |
| `MasterVolume` | `1.0` | Overall gain for pixelReel audio. |
| `FullscreenHidesHud` | `true` | Cover the HUD in theatre mode. |
| `FullscreenFullVolume` | `true` | Ignore distance falloff while in theatre mode. |

On a dedicated server the Jellyfin keys matter and the video keys are ignored. On a
client it's the reverse. Singleplayer uses both.

## Projectors

| Block | Screen size | Audio range |
| --- | --- | --- |
| Compact Projector | 3 × 2 | 16 |
| Wall Projector | 6 × 4 | 24 |
| Ultrawide Projector | 8 × 4 | 24 |
| Cinema Projector | 14 × 8 | 80 |
| Curved Cinema Projector | 16 × 7 | 80 |

All five appear in creative and are findable by searching *projector*, *cinema*, or
*television*. Each is a single 1×1 block that projects a floating screen in front of
itself — build your own frames, walls, and seating around the picture.

The curved projector's screen is concave, wrapping toward the viewer at the edges.
It's the one to use for a proper home cinema.

Video is letterboxed or pillarboxed to fit, never stretched. On the curved screen the
fit is solved against arc length rather than chord length, so the curve doesn't
squash the picture horizontally.

## Using it

- **Right-click** a projector — opens the media menu. Now Playing if something's
  loaded, otherwise the library browser.
- **Sneak + right-click** — toggle power.
- **F6** — theatre mode, while looking at a playing projector. Escape or F6 exits.

### Browsing

Libraries → series → seasons → episodes, or **Recently Added** for a flat list of
the newest movies and episodes. Anything you're part way through shows its resume
position, and picking it carries on from there.

### Playback controls

Pause/resume, −30s, +30s, restart, next episode, volume, and subtitle track.

Controls go through the server, so a pause is a pause for everyone watching. Seeking
moves every client's playhead together rather than re-fetching the stream — nobody
drifts out of step, and the film doesn't restart.

Subtitles are the deliberate exception: they cycle locally, because one viewer
wanting captions shouldn't force them on the whole room.

### Theatre mode

F6 fills the screen with the video and covers the HUD. It reuses the texture the
in-world screen already uploads, so there's no second decoder and no extra VRAM.
Audio ignores distance while you're watching, so you can sit anywhere.

## Commands

Vintage Story uses a dot prefix for client commands and a slash for server commands.

```text
.tv status              VLC availability, process bitness, decode settings
.tv jellyfin            ask the server to ping Jellyfin, report the server name
.tv reload              re-read the client config

/pixelreel status       whether Jellyfin credentials are present   (admin)
/pixelreel reload       re-read server config and reconnect        (admin)
```

`.tv status` is the first thing to run when there's no picture — it reports whether
libvlc loaded, and if not, every folder it searched.

## Multiplayer

Display state — power, media, playback position, volume, pause, subtitles — is
server-authoritative and synced to every client, including players who join mid-film.
Each client decodes the stream locally with its own VLC.

Credentials live in the server's config and are never written into world data.

**One caveat worth understanding.** Jellyfin playback URLs have to carry the API key,
because each client's VLC authenticates directly against Jellyfin. Any player who can
open a projector can therefore read that key from their own logs. On a private server
among friends this is a non-issue; on a public one, use a dedicated playback-only
Jellyfin user rather than an admin key.

## Troubleshooting

| Symptom | Cause |
| --- | --- |
| `.tv status` says VLC unavailable | No 64-bit VLC, or it's somewhere unusual — set `VlcPath`. |
| Picture plays close up, freezes at distance | Mipmap filtering. Fixed in current builds; check the block's info panel for `Mip filter: FAILED`. |
| Picture washed out or fading with distance | Fog or bloom leaking onto the screen quad. Fixed; report if it returns. |
| Video starts then freezes on frame one | `HardwareDecoding` is on. Turn it off — hardware decode routes frames to the GPU where libvlc's callbacks never see them. |
| Stuttering on a remote server | Raise `--network-caching` in `VlcOptions` to 5000 or higher. |
| Browsing returns nothing | The Jellyfin user can't see any libraries, or they're not movie/TV collections. |
| No subtitles | Check the log for `no external subtitles for <id>` — that distinguishes a fetch problem from a rendering one. |

## Differences from the Minecraft mod

Same idea, several deliberate departures.

**Projectors, not multiblock displays.** The original builds screens out of filler
panel blocks. An early version of this port did too, but Vintage Story's built-in
multiblock behaviour caps at 5×6×5 and three of the five displays are larger, so it
meant hand-rolling the whole panel system. Replacing it with 1×1 projectors deleted
several hundred lines along with an entire class of orientation bugs, and left
players free to build whatever backdrop they like.

**Jellyfin only.** No Emby, Plex, or Tunarr live TV. Emby would be a small addition
since it shares Jellyfin's API lineage; Plex is a different shape entirely (XML, its
own auth and library-key scheme) and isn't worth carrying untested.

**No hand-written state packets.** Display state lives in block entity tree
attributes, which Vintage Story already replicates and replays for late joiners. The
Fabric version needs around 1,500 lines of codec boilerplate for this; here it's
about twenty. The network channel only carries what sync can't express — browse
requests and playback commands.

**Subtitles aren't burned in.** The original rasterises subtitle text into each video
frame using Minecraft's font atlas. libvlc already blends subtitles into the picture
before handing frames over, so this port just picks the right track and lets it.

**Seeking moves the playhead.** Jellyfin ignores `startTimeTicks` on a static
direct-play URL, so asking it to start mid-film just replays from the top. Instead the
server broadcasts a seek and every client jumps locally, which is both faster and
keeps the room in sync.

## Not yet

- Poster art in the browse menu (it's a text list today)
- HDR tone mapping
- Crafting recipes — creative inventory only so far
- Movie scheduler, admin remote, per-player private viewing

## License

[CC0 1.0](LICENSE), same as the original.
