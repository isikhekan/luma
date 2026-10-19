Luma 1.6.0

- Adds an RTSS notification backend for supported fullscreen games. An existing standard RTSS installation is detected and started in the background; RTSS itself is not bundled.
- Renders the four-language notification card inside the graphics overlay with a short slide animation and four-second hold, without activating a desktop window or changing game display mode.
- Caches one image per notification, stops updates during the hold and while idle, compensates for the target OSD zoom and preserves other RTSS clients and user profiles.
- Waits for the renderer during game launch and clears the card on foreground changes, expiry, cancellation and shutdown.
- Adds a translated Settings toggle and connection status, plus shared-memory compatibility, ownership, bounds, lock and timing tests.

RTSS/game/anti-cheat compatibility is required. Zero FPS impact is not guaranteed; bridge-publish timing is not an in-game FPS measurement. Live validation covers the local RTSS bridge and GPU rendering, not every game's fullscreen mode. No game files, frame limits or anti-cheat settings are changed.

Windows x64, self-contained, unsigned. Personal settings, monitor IDs, game paths, overlay cache images and local diagnostic screenshots are excluded from the public source and package.
