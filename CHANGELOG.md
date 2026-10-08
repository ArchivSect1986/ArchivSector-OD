# 📋 Changelog

All notable changes to **ArchivSector-OD** will be documented in this file. This project adheres to [Semantic Versioning](https://semver.org).

---

## v2.4 - 2026-10-07

### ✨ Added
* PC game discs and other data discs the app doesn't recognize can now be dumped: the 🎮 Game Disc / Raw ISO button is no longer disabled for them, and so is the File name box. Dumps go into `Games\PC_and_Other\` (or the bay label's folder). Unrecognized discs are never auto-started, only dumped on a click.
* Music CDs can be dumped as an exact copy (`.bin` per track plus a `.cue`) into their own top-level `Music_CDs\` folder, recorded in History as "Audio CD". This isn't a music rip -- for playable FLAC/MP3 files, use a dedicated ripper. Music CDs are recognized by any `.cda` track file or the "Audio CD" label, with a backup check of Redumper's track list after the dump (all audio tracks = music CD).
* Data discs that hold music files (e.g. a burned CD of MP3s) also go into `Music_CDs\`, recorded as "Music CD (files)". A disc counts when at least 80% of its files are audio files (cover art, playlists and the like are ignored).

### 🐛 Fixed
* CD dumps were always reported as failed. For CDs, Redumper's `dump` step only writes the raw read (`.scram` and friends) and no image, while the app looked for an `.iso`. When a dump leaves a `.scram`, the app now runs Redumper's `split` step and uses the resulting `.bin`/`.cue` (the largest `.bin` is hashed and checked against Redump). This affected PS1 games and every other CD. The `.scram` is kept, as Redumper does.

---

## v2.3 - 2026-10-05

### ✨ Added
* PlayStation 4 discs are recognized as game discs (a `PS4` folder next to `app` or `bd`). Before, they were classified as Unknown and every rip button stayed disabled. The bay shows "PlayStation 4", the serial from the `app\<serial>` folder (e.g. `CUSA-36842`) and the real game name from `bd\param.sfo`, the ISO is named after the game, and dumps go into `Games\PlayStation 4\`.
* Xbox One discs are recognized as game discs (an `MSXC` folder). The real game name is read from `MSXC\Metadata\catalog.js`, with " (Disc N)" added for multi-disc games so each disc gets its own name, and dumps go into `Games\Xbox One\`. Xbox One discs have no serial like PlayStation discs, so that stays blank. Xbox Series X|S discs likely use the same layout and would show as Xbox One.
* PS4 and Xbox One dumps are encrypted exact copies of the disc, for preservation and Redump verification; they can't be played or installed directly. The README says so.

* **One-click updates.** The app checks GitHub for a newer release when it starts (a few seconds after opening, silently if there's nothing new or no internet). If there is one, it asks; on **Yes** it downloads the release zip, checks it against GitHub's SHA-256 checksum, closes, installs the new files and reopens by itself. Check for Updates in Settings offers the same. It won't update while a rip or checksum is running, backs up the app folder first and restores it if installing fails, and leaves settings, history and artwork (in `%AppData%`) untouched. If the app's folder can't be written to (e.g. Program Files), it offers the download page instead. Turn the startup check off with "Check for updates when the app starts" in Settings. Updating *to* v2.3 is still done by hand, since older versions don't have this.
* Original Xbox discs are recognized as game discs before the dump. On a PC drive they only show a small video partition (a `VIDEO_TS` folder), so they were taken for DVD movies. The app now spots the factory timestamp volume label they use (month + 11 digits, e.g. `SEP13011042072`) and shows "Original Xbox" right away; a disc with only a tiny `VIDEO_TS` (under 50 MB) is treated as a game disc and identified from Redumper's log after the dump.

### 🐛 Fixed
* An Xbox disc dumped through the DVD menu's Raw Sector ISO option is now recognized from Redumper's log ("XGD detected") and moved from `DVD_Movies` into `Games\Original Xbox\` or `Games\Xbox 360\`.
* Saved artwork was looked up by the disc's volume label, so the last game saved under a generic label such as `DVD_ROM` showed its cover for every disc with that label. Discs with a generic label (`DVD_ROM`, `PS4VOLUME`, `XBOX360` and similar) now look up saved art by their serial, or skip it when there's no serial.

---

## v2.2 - 2026-10-05

### ✨ Added
* Original Xbox and Xbox 360 discs are now identified after the dump. Windows can't see their game files, so they can't be identified beforehand, but Redumper can read them on a supported drive (e.g. OmniDrive firmware) and writes `XGD detected (version: N)` to its log. After each game dump the app reads that line (XGD1 = Original Xbox, XGD2/XGD3 = Xbox 360), shows the console on the drive bay, records it in History, and moves a dump that landed in `Games\General_Games\` into `Games\Original Xbox\` or `Games\Xbox 360\`. Dumps in a folder chosen by a bay label, or with Auto-categorize off, are left where they are.
* When Redumper reports an Xbox security sector as incomplete, the log notes that the game data was dumped but the dump may not match Redump's database.

---

## v2.1 - 2026-10-01

### ✨ Added
* Game discs are now identified by console from their own files, the same way v1.1 did. PlayStation 1, 2 and 3 discs are recognized automatically. The console and the game's serial number (e.g. `SLUS-20062`, `BLUS-30001`) show on the drive bay, and game dumps go into `Games\<console>\` when the bay has no custom label. A bay label still wins, and discs that can't be identified still go to `Games\General_Games\`. Original Xbox and Xbox 360 detection is included too, but it only works when the drive can see the disc's game files, which a standard PC drive usually can't, so those discs normally show up as unrecognized.
* Game discs: a "File name" box on the drive bay. Type a name before the dump starts to use it instead of the disc title, or change it while a dump is running (including one that Batch Queue Mode or Unattended Mode started on its own) and it's applied when the dump finishes. After a dump, type a new name and click **Rename** to rename the folder and all its files; the History record is updated to match.
* Archival History: a new Serial ID column, filled in for PlayStation games (Xbox discs have no readable serial). Game rips record their console as the System, so the "By System" stats count PS1, PS2 and PS3 separately. The AACS Version column is now filled in for Blu-ray rips, and the filter also searches serial numbers.

### ⚙️ Changed
* The history file now uses the same columns, in the same order, as v1.1 ("Serial ID" added, "Output Path" renamed to "ISO File Path"). A history file from v2.0 is converted automatically the next time a rip is recorded, and a copy of the original is kept as `history.csv.v2.0-backup`.

### 🐛 Fixed
* Tool paths pasted into Settings with quotation marks around them (which Windows adds when you use "Copy as path", e.g. `"C:\redumper\redumper.exe"`) were quietly ignored, so the app fell back to searching the usual install locations instead. The quotes are now removed when you click Save Preferences, for the output folder and all four tool paths.

---

## v2.0 - 2026-09-29

### 🔖 Version
* Bumped from v1.1 to v2.0 for a complete rewrite: the app is now built in C# (.NET 8) as a WPF desktop app with a Blazor Hybrid interface, replacing the Python/Tkinter version. Everything in v1.1 was carried over; the differences are listed below. The Tkinter scroll-tearing workarounds from v1.1 no longer apply, since the interface no longer uses Tkinter.

### ✨ Added
* Movie-Only MKV: the output file name can now be edited in the track picker before extraction starts. MakeMKV still writes its own file name, which is renamed to the chosen one when it finishes.
* Movie-Only MKV output now always gets SHA1/MD5 checksums (the Redump match is still reported as "N/A (re-muxed)").
* Automatic LibreDrive confirmation: when MakeMKV reports "Using LibreDrive mode" during a rip, the drive bay shows "✓ LibreDrive mode (MakeMKV)" and notes it in the log.
* Metadata search: common disc-label abbreviations are expanded before searching (XMAS → Christmas, VOL → Volume, PT → Part, COLL → Collection), and a TMDB search that finds nothing is retried with up to three shorter versions of the title.
* Metadata search window: attribution links for TMDB and RAWG, as their terms of use require.
* Settings: a "Play a sound when a rip completes" checkbox (the sound was always on in v1.1, with no way to turn it off in Settings).
* Archival History: the filter also matches Drive Model.
* Startup: the config folder is created when the app starts, with a visible warning if that fails, instead of failing silently on the first save.
* The main window opens maximized.
* Settings: a "Check for Updates" button that checks GitHub for a newer release and offers to open its download page. It only reports; it never downloads or replaces files itself.

### ⚙️ Changed
* Blu-ray and 4K UHD rips now go into a `BluRay_Movies` folder instead of `Movies`, so it's clear next to `DVD_Movies`. Existing rips in `Movies` aren't moved; rename that folder yourself to keep everything together.
* Full backups (Blu-ray backup, DVD MakeMKV backup, DVD raw ISO) are now sorted into size folders (`BD-25`/`BD-50`/`DVD-5`/`DVD-9`) whenever Auto-categorize is on, not only in Unattended Mode, so attended and unattended backups end up in the same place.
* Movie-Only MKVs now go into their own `MKV` folder (e.g. `BluRay_Movies\MKV\Movie Name\`), so they don't mix with full backups, and a backup and an MKV of the same movie no longer trigger the duplicate warning.
* Batch Queue Mode's queued drives are shown with a banner on each waiting drive bay, plus a queued count in the footer, instead of a separate strip above the footer.
* After an attended rip, the result shows in the drive bay and the 📂 Output Folder button opens it, instead of an "Open this folder?" pop-up.
* Game dumps go into `Games\<bay label>\` when the bay has a custom label, otherwise `Games\General_Games\`. v1.1 could also fall back to the disc's detected system name, which this version doesn't extract.

### 🐛 Fixed
* Tray eject could report success without the tray actually opening, when Windows couldn't get an exclusive lock on the drive (often because the app's own drive polling had it open). The lock is now retried, and the log says exactly which step failed.
* The Eject / Close Tray button now follows the tray's real state: Eject is always available (including on an empty drive), and Close appears only once the tray is open.
* Retry Last Rip now goes through Multi-Drive Batch Sync's concurrent-rip limit, like every other way of starting a rip.

### 🗑️ Removed
* iTunes movie search, which v1.1 used when no TMDB key was set. In testing it returned no movie results at all, even for titles Apple sells. Movie artwork now needs a free TMDB API key; without one, auto-fetch is skipped with a note in the log.
* The read speed profile setting (Max / 8x / 4x for Redumper), which only existed in v1.1's config file with no Settings control. Dumps always run at the drive's full speed.
* The file naming pattern setting (`{Title}`, `{System}`, `{Serial}`), which also only existed in the config file. Output is always named after the disc title, falling back to a drive-letter and timestamp name when the disc has no usable title.

### ⚠️ Known Differences
* Disc serial numbers aren't extracted yet, so they aren't recorded in the history, and cached artwork is keyed by disc title instead of serial number.

---

## v1.1 - 2026-09-08

### 🔖 Version
* Bumped from v1.0 to v1.1 — first versioned release since Multi-Drive Batch Sync, the Unattended Mode overhaul (auto rip-mode, destination, naming, duplicate handling, and preflight confirmations), and the fixes below. Everything in this entry shipped under the old v1.0 label with no version bump at the time; this is the catch-up marker so future changes have an actual baseline to diff against instead of everything sitting under "1.0" indefinitely.

### 🐛 Fixed
* Replaced the failed event-loop fixes for scroll corruption with one that targets the actual mechanism. Confirmed via a second screen recording that `update()` didn't resolve it either — the corruption (white-striped or solid-color buttons with missing text) only ever appeared on whichever card was currently straddling the canvas's top clip boundary *while it was still moving*, and cleared the instant the scroll settled. That's Windows mis-painting a partially-clipped native child window during rapid repositioning — a rendering-layer issue neither `update_idletasks()` nor `update()` could touch, since both only affect Tk's own event queue. Fixed by locking `yscrollincrement` to one full card row's height (recomputed on every `bays_container` resize, so it tracks a card's log console expanding too), so every mouse-wheel scroll jumps straight to the next row boundary instead of passing through many small partially-clipped positions on the way. Not yet re-confirmed against a third recording. Known residual gap: this only covers mouse-wheel scrolling (`yview_scroll`) — dragging the scrollbar thumb directly (`yview_moveto`) isn't affected by `yscrollincrement` and could still land mid-row.
* Escalated the scroll-tearing fix from `update_idletasks()` to `update()` on all three scrollable canvases (main bays grid, Settings, MKV track picker) — reported as still occurring after the `update_idletasks()` attempt. Reasoning: `update_idletasks()` only processes Tk's own queued idle/geometry callbacks; it never touches the Windows-level paint/move messages for each drive bay card's embedded child window, which is the actual bottleneck when several mouse-wheel events queue up faster than Windows can finish repositioning/repainting each card. `update()` forces the full pending event queue to process, including that OS-level step. Guarded against reentrancy on the main bays canvas, since `update()` can itself process a second queued-up wheel event and re-enter the same handler before the first call returns. Not yet confirmed fixed — still needs a real run to verify.
* Confirmed the reported "screen tear" via frame-by-frame analysis of an actual screen recording (not assumed from the description) — it's a real Tkinter rendering lag, not just a subjective visual complaint. Drive bay cards are deeply nested Frames embedded in the bays canvas via `create_window`, not lightweight canvas-drawn shapes; scrolling fast enough queues mouse-wheel events ahead of Tkinter finishing each card's redraw, so for several frames at a time some cards showed blank space where their Optical Media Readout panel or Process Telemetry log should be, until the redraw caught up. Fixed by forcing `update_idletasks()` after every scroll step on all three scrollable canvases (main bays grid, Settings, and the MKV track picker), so redraws can't fall behind a burst of scroll events. Distinct from, and in addition to, the DPI-awareness fix below — that one addresses OS-level compositor stretching, this one addresses genuine Tk widget redraw lag; both could plausibly contribute to the same reported symptom.
* No Windows DPI awareness declared anywhere in the app — reported as a ~1 second screen-tearing effect while scrolling, on both the .py and the compiled .exe. Without declaring it, Windows renders the app at a virtualized resolution and has the OS compositor stretch that bitmap to match the display's actual scale factor (125%/150% is the norm on most modern displays); scrolling fast enough for Tkinter to redraw ahead of that stretched copy catching up is what showed up as tearing. Fixed with `SetProcessDpiAwareness(2)` (falling back to the older `SetProcessDPIAware()` on pre-8.1 Windows) called before any Tk window is created. Trade-off worth knowing: since the app's many fixed-pixel `.place()` layouts (Settings especially) now render at true pixel size instead of Windows' compensated-up size, the UI may look smaller on a scaled display than it did before — not verified against a real scaled display, since this was fixed from code review of a reported symptom, not a Windows environment.
* Settings' "Max concurrent rips" spinner was hardcoded to a max of 8, silently capping anyone with more than 8 drive bays below their actual drive count via the spinner arrows. Now scales to whichever is bigger: 8 as a sensible floor for a small setup, or the real number of detected bays for a larger one.
* Two more Unattended Mode blockers found in `_run_preflight_check` (shared by the Blu-ray/DVD pipeline): a "Low Disk Space — continue anyway?" confirmation and a "MakeMKV License Warning — continue anyway?" confirmation, neither gated on Unattended Mode. An auto-started rip could still silently stall on either one. Now: low disk space logs and skips that rip (safer than attempting one likely to fail partway through); the license warning — a heuristic, not a certainty — logs and proceeds. Scoped to the movie/DVD pipeline only; Retry Last Rip and the game-disc pipeline are unchanged and still ask interactively, matching how they've always worked.
* ImgBurn's BUILD (Blu-ray ISO staging) and READ (DVD ISO dump) calls were missing `/NOIMAGEDETAILS`. `/START` and `/CLOSESUCCESS` start the operation and close ImgBurn on success, but don't suppress the separate "Image Details" confirmation dialog ImgBurn shows once the build/read finishes — so every rip using ImgBurn still stopped and waited for someone to click OK, defeating Batch Queue Mode and Unattended Mode alike. Added to both call sites.
* `_advance_batch_queue` could re-enter itself: resuming a queued bay can flip its `is_ripping` to `True` mid-call, which fires another state-change signal and re-enters the same method while the outer pass is still iterating the bay list — a genuine race that could plausibly cause a queued bay to be skipped in the pass that mattered (reported: with 2 slots freed, drive F resumed but drive E stayed stuck with a loaded disc until manually ejected and reinserted). Fixed with a re-entrancy guard, and each pass now claims at most one freed slot, letting the newly-started bay's own state-change trigger the next pass instead of the same pass looping further.
* Added diagnostic logging for auto-triggered rips that clear the slot gate but never actually start (e.g. `tray_open` still `True`, or another of `_start_optical_pipeline`'s own early guards silently returning) — previously this failed with zero explanation, leaving a bay that looked hung with no way to tell why short of a full repro. Scoped to auto-triggered starts only, so a normal manual dialog cancel doesn't get flagged.
* The completion prompt ("Extraction Complete — open this folder?") was only skipped when Batch Queue Mode was on, even though it's just as blocking under Unattended Mode alone. Enabling Unattended Mode without also enabling Batch Queue Mode meant every auto-started rip finished by popping up a modal and sitting there — undoing the rest of Unattended Mode's walk-away behavior. Now skipped under either mode.
* Multi-Drive Batch Sync's concurrent-rip limit only applied to auto-started rips — clicking Movie/DVD/Game (or Retry Last Rip) yourself bypassed it entirely, since manual starts didn't go through the gate at all. All four rip-start paths (auto-detect, manual button, Retry) now share one gate, so the configured limit means "no more than N rips running at once," full stop, regardless of how each one was started. A manual click that can't start immediately is queued the same way an auto-started disc is — shown in the Batch Queue box, starts itself once a slot frees — instead of just being refused.
* Multi-Drive Batch Sync's concurrent-rip limit only counted bays where `is_ripping` was already `True`. Since that flag isn't set until *after* the rip-mode choice, destination picker, and naming dialog are all answered, several bays clearing the check in the same window (e.g. confirming multiple drives' auto-start prompts back to back) would all see a free slot and start simultaneously, ignoring the configured limit. Fixed by having a bay reserve its slot the moment it clears the check, not only once it's actually ripping.
* `_resolve_makemkv_disc_index()` matched drive letters with a trailing colon (`"D:"`) against MakeMKV's own `DRV:` robot-mode output, which reports the bare letter (`"D"`) — so the match never succeeded, on any system. Invisible on a single drive-bay setup (the fallback UI-order guess is always correct when there's only one drive), but with two or more drives of the same type, MakeMKV's own internal drive numbering doesn't always match this app's UI order, so Movie-Only MKV mode (and the BD/UHD full decrypt path, which shares the same resolver) could silently target the wrong physical drive and come back with "no titles found." Fixed by stripping `:`/`\` from both sides before comparing.

### ✨ Added
* Batch Queue box: a bar that appears just above the footer whenever one or more drive bays are waiting on Multi-Drive Batch Sync, listing each queued drive letter and its loaded disc title. Hidden automatically the moment nothing's queued.
* Multi-Drive Batch Sync: optional cap (1–8, configurable in Settings → Extra Features) on how many drive bays are allowed to rip at once under Batch Queue Mode. Bays that fill a disc past the limit show "WAITING FOR FREE DRIVE SLOT" and auto-start the moment another bay finishes, is canceled, or ejects — no manual restart needed.

### ⚙️ Changed
* Corrected the disc-capacity → media-type classification thresholds (`read_full_disc_info_worker`): dropped the invented "BD-66 Dual Layer" tier, corrected BD-100 to Triple Layer, and added a BD-128 Quad Layer tier at 119 GB. This is the same classification the disc-info panel already showed and Unattended Mode's auto-foldering now uses, so it affects both.
* Unattended Mode's auto-started rips now also skip the destination-folder picker and the naming prompt — sorted automatically into a size-based subfolder (`BD-25`/`BD-50`/`BD-100`/`BD-128`/`DVD-5`/`DVD-9`) under the existing Movies/DVD_Movies category folders, using the same disc-capacity classification already shown in the disc-info panel (`current_disc["layers"]`), and named from the existing naming-pattern setting. A collision with an existing archive is resolved by silently appending "(2)", "(3)", etc., instead of blocking on the overwrite-confirmation prompt.
* Unattended Mode's auto-started rips now always go Full Backup for movies and DVDs, skipping the Full Backup / Movie-Only rip-mode prompt entirely — there's often nobody there to answer it. Batch Queue Mode auto-starts (with Unattended Mode off) still ask, same as before; manual button clicks are unaffected either way.
* Refactored the disc-insertion auto-start logic (Unattended Mode / Batch Queue Mode) out of `_on_disc_inserted_async` and into a shared `DriveBayCard._try_batch_autostart()`, so the same method now handles both the original immediate auto-start and the new queued/deferred case.
* The app-level `on_state_change` callback each drive bay fires (rip start/finish/cancel, disc insert/eject) now also re-checks the Multi-Drive Batch Sync queue, in addition to its existing footer-stats refresh.

### 🗑️ Removed
* The "Auto Track-Profile Filtering [Planned]" placeholder — retired, not a bug waiting on a fix. MakeMKV's own developer (forum.makemkv.com) confirmed that as of release ~1.14.2, per-invocation profile-file selection rules are no longer applied at all; the equivalent now lives as a single static default rule set once in MakeMKV's own GUI (Preferences → Advanced → Expert Mode), applied to every future rip regardless of what's on each disc. Scripting around that would mean silently rewriting the user's global MakeMKV preferences before every rip — worse than today's approach, not better, and incompatible with the per-disc, per-language choices this app's track picker already offers. mkvmerge's post-extraction trim is the correct approach going forward, not a stopgap. Full history documented in `build_makemkv_track_profile`'s docstring.
* The "Multi-Drive Batch Sync [Planned]" placeholder from the Extra Features panel, now that the feature is implemented.

---

## - 2026-09-06

### ✨ Added
* Batch Queue Mode: skips the completion prompt and auto-continues ripping each disc you swap in, with a running per-drive session counter and auto-eject between discs.
* Archival History stats bar: total rips, Redump match rate, total data archived, and a breakdown by system, computed live from the full history log.
* "Export Filtered to CSV" — export exactly the filtered subset of the history you're currently viewing to its own CSV file.
* "Clear History" — permanently wipes the archival history log, with an explicit confirmation and a reminder to export first.
* Settings: collapsible "Extra Features" panel for less-common toggles (Unattended Mode, Auto-Fetch Metadata & Artwork, Batch Queue Mode). The Settings window widens when it's opened, keeping the main view uncluttered.
* Standalone Windows `.exe` build path (`build_exe.bat`, PyInstaller) so the app can be distributed without requiring Python on the end user's machine.

### ⚙️ Changed
* Consolidated the Blu-ray and DVD "Movie" ripping pipelines (`_start_movie_pipeline` and `_start_dvd_iso_pipeline`) into a single shared `_start_optical_pipeline(kind)`, removing duplicated preamble logic that had to be fixed twice with nothing enforcing they stayed in sync.

### 🗑️ Removed
* A stale "History Stats & Export" placeholder from the Extra Features panel, now that the feature is implemented.

---

## - 2026-09-04

### ✨ Added
* DVD Movie-Only MKV mode — the DVD button now offers the same "Direct 1:1 ISO Dump vs Movie Only (MKV)" choice previously exclusive to Blu-ray.
* Per-track audio/subtitle picker for Movie-Only MKV extraction, using SINFO stream-level scan data (language, codec, bitrate, channels, sample rate) matching MakeMKV's own info panel.
* mkvmerge (MKVToolNix) integration for reliable post-extraction track trimming, replacing an unreliable `--profile`-based MakeMKV filtering approach that accepted the flag but silently extracted every track anyway.
* Per-language track reconciliation to handle discs that author a duplicate "forced-only" companion PGS sub-stream per language, without blocking the trim for the whole disc.

### ⚙️ Changed
* Rewrote `_resolve_makemkv_disc_index()` and `_scan_all_movie_titles()` from `subprocess.run()` to `subprocess.Popen()` with iterative line reading and a watchdog thread, fixing hangs on large info-command output.

### 🐛 Fixed
* Naming dialog no longer says "(without .iso)" when the output is actually an MKV.
* Post-extraction rename now uses `os.replace()` instead of `os.rename()` (which failed on Windows if the destination already existed), and picks the correct file by newest modified time instead of `os.listdir()[0]`'s arbitrary order.
* Redump verification now reports an honest "N/A (re-muxed)" for MKV output instead of a misleading "No match" — re-muxed files can never match Redump's raw-dump database.
* The verification result now surfaces immediately at completion (toast + dialog) instead of sitting silently in the history CSV.

---

## - 2026-08-23

### ✨ Added
* Initial release of **ArchivSector-OD**.
* Core automation engine for handling sequential disc archiving workflows.
* User interface configuration screen for managing external third-party utility paths.
* Automated SHA1 and MD5 checksum file generation for data integrity verification.
* Multi-speed profile configuration options ranging from 4x to 16x CAV.

### ⚙️ Changed
* Updated UI layout from a vertical stack to a wider, side-by-side split layout for improved readability on modern monitors.

### 🔒 Security & Legal
* Explicitly separated external tools (**MakeMKV**, **Redumper**, **ImgBurn**) into user-defined local paths to ensure compliance with open-source licensing.
