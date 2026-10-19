Luma 1.7.0

- Detects missing RTSS and offers one-time setup approval inside Luma. Windows Package Manager then handles download, security/hash verification and silent installation of the reviewed Guru3D.RTSS 7.3.7 package; Windows may request administrator approval.
- Reuses existing/custom RTSS installations, starts the server, checks disabled OSD preferences and verifies the notification connection. New RTSS defaults are ready for Luma notifications; existing profiles, exclusions, overlays and FPS limits are preserved.
- Defers pending acquisition while a detected game or fullscreen app is active. Serializes setup requests and reports progress, cancellation and recovery in Turkish, English, Spanish and Simplified Chinese.
- Adds retry and official App Installer recovery links. Stops pending setup on notification disable or app exit, avoids background retry loops after failures and withdraws saved approval on cancellation.
- Adds dependency setup tests and offscreen UI previews for missing, installing, ready and failed states. Fixes a stale notification status after focus changes.

RTSS is a separate optional dependency, acquired through Windows Package Manager and not redistributed in the Luma source or release. An internet connection, an available WinGet/App Installer and Windows installation permission are required. A running elevated installer may finish independently after Luma exits. Game/anti-cheat compatibility remains necessary; zero FPS impact is not guaranteed.

Validation covers simulated fresh-install/error paths, a real package download with successful hash verification and the existing local RTSS connection. A clean-machine installation/UAC run has not been performed locally.

Windows x64, self-contained, unsigned. Personal settings, monitor IDs, game paths, overlay images, downloaded RTSS archives and local diagnostics are excluded from publication.
