🌟 [RELEASE] ArchivSector-OD v2.2 | Optical Disc Ripping & Archival Control Center 🌟

Hey everyone! 👋

Welcome to **ArchivSector-OD v2.2**! Version 2.0 was a complete rewrite: the
app is now a native Windows program built in C# (.NET 8), replacing the old
Python/Tkinter version. Everything from v1.1 came along — the Blu-ray/4K UHD
and DVD backup pipelines, per-track Movie-Only MKV extraction, unattended
batch ripping, and the searchable archival history — with a new look and a
handful of improvements on top.

========================================================================
⚡ QUICK START
========================================================================

1. Unzip the whole folder, then double-click ArchivSector-OD.exe.
2. ⚠️ If Windows shows a blue "Windows protected your PC" screen, click
   "More info", then "Run anyway". This is normal for programs that
   aren't signed with a paid certificate, and you'll only see it once.
   (Full explanation under "How to Run" below.)
3. Open ⚙️ Settings and point it at your tools (MakeMKV, mkvmerge,
   Redumper, ImgBurn), then insert a disc.

Updating from v2.0 or v2.1? Just replace your old folder with this one. Your
settings, history, and cached artwork are kept — they're stored
separately in your AppData folder.

========================================================================
🎮 NEW IN V2.2 (since v2.1)
========================================================================

• Original Xbox and Xbox 360 discs are now identified after the dump,
  from Redumper's log. Windows can't see their game files, so the bay
  can't show the console beforehand -- but once the dump finishes, the
  console is shown on the bay and recorded in History, and a dump that
  went into \Games\General_Games is moved into \Games\Original Xbox or
  \Games\Xbox 360. This needs a drive Redumper can read Xbox discs on
  (e.g. one with OmniDrive firmware); on other drives they still go to
  General_Games.
• If Redumper reports the Xbox security sector as incomplete, the log
  says so: the game data is dumped, but the dump may not match Redump.

========================================================================
🔧 NEW IN V2.1 (since v2.0)
========================================================================

• PlayStation 1/2/3 game discs are identified automatically. The
  console and serial number show on the drive bay, and dumps go into
  \Games\<console> unless the bay has its own label.
• Name your game dumps: a "File name" box on the drive bay. Type a
  name before the dump, change it while it's running (even if Batch
  Queue or Unattended Mode started it), or rename it after it's done.
• History gets a Serial ID column, game rips are counted per console in
  the stats, and the filter searches serial numbers. Your existing
  history is converted automatically (a backup copy is kept).
• Fixed: tool paths pasted into Settings with quotation marks around
  them (which Windows adds when you use "Copy as path") were quietly
  ignored. The quotes are now removed automatically when you save.

========================================================================
🆕 NEW IN V2.0 (since v1.1)
========================================================================

🖥️ Rebuilt from the ground up
• New C# / .NET 8 app with a modern interface and three color themes
  (Midnight, Nova, Daylight). Opens maximized, with each drive bay shown
  as its own card.

✏️ Choose the MKV file name before ripping
• Movie-Only MKV now lets you edit the output name in the track picker
  before extraction starts. MakeMKV writes its own file name as usual,
  and it's renamed to your choice when it finishes.

🔒 Checksums for Movie-Only MKV
• Movie-Only output now always gets SHA1/MD5 checksums too. (The Redump
  match is still reported as "N/A (re-muxed)", since a re-muxed file can
  never match Redump's database.)

💡 Automatic LibreDrive confirmation
• When MakeMKV reports "Using LibreDrive mode" during a rip, the drive
  bay shows "✓ LibreDrive mode (MakeMKV)" and notes it in the log.

🔍 Smarter metadata search
• Common disc-label abbreviations are expanded before searching (XMAS →
  Christmas, VOL → Volume, PT → Part, COLL → Collection), and a search
  that finds nothing is retried with shorter versions of the title.

📁 Clearer folder names
• Blu-ray and 4K UHD rips now go into \BluRay_Movies instead of \Movies.
  Existing rips in \Movies aren't moved — rename that folder yourself if
  you want everything together.
• Full backups are always sorted into size folders (BD-25 / BD-50 /
  DVD-5 / DVD-9), not just in Unattended Mode, and Movie-Only MKVs get
  their own MKV folder — e.g. \BluRay_Movies\BD-50\Movie Name\ for a
  backup and \BluRay_Movies\MKV\Movie Name\ for the MKV, so the two
  never clash.

🔊 Sound on/off in Settings
• New "Play a sound when a rip completes" checkbox.

🔄 Check for Updates
• New button in Settings that checks GitHub for a newer release and
  offers to open its download page.

⚠️ Removed in v2.0
• iTunes movie search (the fallback when no TMDB key was set) — it
  stopped returning movie results. Movie artwork now needs a free TMDB
  API key.
• The read speed and file naming pattern options, which only existed in
  v1.1's config file. Dumps run at full speed, and files are named after
  the disc title.

========================================================================
⚡ ALL FEATURES
========================================================================

💿 Blu-ray / 4K UHD Backup Pipeline (MakeMKV)
• Direct 1:1 Backup: Full MakeMKV decrypt (AACS/BD+), with an optional
  ImgBurn stage 2 that builds a single-file ISO.
• Movie-Only MKV: Pick the main movie title from a list (sorted longest
  first) and extract it into a single, directly-playable .mkv — skips
  menus/extras.
• LibreDrive detection: shows whether the drive model is on the
  OmniDrive-compatible list, and confirms LibreDrive mode automatically
  when MakeMKV reports it.

📀 DVD Backup Pipeline
• Raw Sector ISO Dump: Byte-for-byte disc image via Redumper, falling
  back to ImgBurn if Redumper isn't set up.
• MakeMKV Decrypted Backup: a decrypted copy of the disc via MakeMKV.
• Movie-Only MKV: Same MakeMKV-driven extraction, title picker, and
  track picker as the Blu-ray pipeline.

🎚️ Per-Track Audio/Subtitle Picker (Movie-Only MKV)
• Shows every individual audio/subtitle track — language, codec,
  bitrate, channels — matching MakeMKV's own info panel, not just
  grouped by language.
• mkvmerge does the real track trimming as a reliable post-processing
  pass, with per-language reconciliation to handle discs that author a
  full + "forced-only" companion subtitle pair per language.

🔁 Unattended Mode & Batch Queue Mode
• Unattended Mode (toggle in the top bar): auto-starts ripping the
  moment a disc is detected. Blu-rays always go Full Backup and DVDs
  always go Raw Sector ISO — no prompts. Each disc goes into its size
  folder and is named after the disc title. If that
  name already exists, the new rip is saved as "Title (2)", "(3)", etc.
  instead of overwriting. Low-disk-space and MakeMKV-license warnings
  are logged instead of stopping to ask.
• Batch Queue Mode: keeps auto-starting every disc you swap in, with a
  running per-drive "BATCH QUEUE: N DONE" counter and auto-eject between
  discs. (With Unattended Mode off, it still asks Full Backup vs
  Movie-Only for each disc.)
• Multi-Drive Batch Sync: optional cap on how many drive bays are
  allowed to rip at the same time — applies to auto-started rips,
  button clicks, and Retry Last Rip alike. Extra bays show "Queued for
  Multi-Drive Batch Sync" and start themselves the moment another bay's
  rip finishes. The footer shows how many are running and queued.

✅ Checksums & Redump Verification
• Generates SHA1 and MD5 checksums for every rip.
• Verifies straight dumps against imported Redump DAT files and reports
  an honest "N/A (re-muxed)" for Movie-Only MKV output instead of a
  misleading "No match."
• The result (✓ match / ⚠ no match / ℹ n/a) shows right in the drive
  bay when the rip finishes.

📊 Archival History — Stats & Export
• Searchable/filterable log of every rip: title, system, drive model,
  capacity, checksums, and Redump match.
• Stats bar: total rips, Redump match rate, total data archived (GB),
  and a breakdown by system — computed live from your full history.
• Export Filtered to CSV, Open CSV File, Search Redump.org, and Clear
  History (with an explicit confirmation and a reminder to export first).

🎮 Game Disc Archival
• Dedicated Redumper-driven pipeline for game discs, saved into
  \Games\<console> — PlayStation 1/2/3 discs are recognized
  automatically, and their serial number is recorded. Rename a drive
  bay (e.g. "Xbox 360" or "Retro") and its games go into a folder with
  that name instead. Unrecognized discs go to \Games\General_Games.
  Original Xbox and Xbox 360 discs are identified once the dump
  finishes (from Redumper's log, on a drive that can read them, e.g.
  OmniDrive) and moved into their own console folder.
• File name box on the drive bay: name a game dump before, during, or
  after it rips.

🖼️ Metadata & Artwork
• Automatically looks up movie discs on insertion via TMDB and shows the
  poster on the drive bay. Manual search for movies (TMDB) and games
  (RAWG). Both need a free API key.

🔔 Notifications & Convenience
• Windows notification and a completion sound when a rip finishes.
• Auto-eject the physical tray when a rip finishes.
• Eject / Close Tray button on every drive bay, even with no disc in.
• Auto-categorize output into \BluRay_Movies, \DVD_Movies, and \Games:
  full backups sorted by disc size (BD-25 / BD-50 / BD-100 / DVD-5 /
  DVD-9), Movie-Only MKVs in their own MKV folder.
• Duplicate-output warning before a rip starts.
• Retry Last Rip, for re-running the most recent job with one click.
• Click a drive bay's title to rename it — remembered across restarts.
• Process Telemetry log on every drive bay.
• ISO Verifier for checking existing ISO files.

⚙️ Settings
• Tool paths, output folder, API keys, and core toggles up front;
  Auto-Fetch Metadata & Artwork, Batch Queue Mode, and Multi-Drive
  Batch Sync live in a collapsible "Extra Features" section.
• Redump DAT import, config folder shortcut, and a write diagnostic for
  tracking down antivirus/sandbox problems saving files.
• Check for Updates: asks GitHub whether a newer version is out and
  offers to open its download page.

========================================================================
📁 PACKAGE CONTENTS & FILE EXPLANATIONS
========================================================================

• ArchivSector-OD.exe
  The application — double-click this to run it.

• wwwroot\ folder
  The app's interface files (layout, colors, and styling). Required.

• All the other .dll / .json files and language folders (cs, de, fr...)
  The built-in .NET 8 runtime and its libraries, bundled so you don't
  have to install .NET yourself. Required — leave them alone.

Keep everything in the folder together. Moving ArchivSector-OD.exe out
on its own will stop it from starting. To put it somewhere else, move
or copy the whole folder.

• README.txt
  Complete documentation and feature overview for this release.

========================================================================
🚀 HOW TO RUN ARCHIVSECTOR-OD
========================================================================

1. Unzip the whole folder anywhere you like, then double-click
   "ArchivSector-OD.exe" — there's no installer, and you don't need to
   install .NET. Needs Windows 10 or 11 (64-bit) with the Microsoft Edge
   WebView2 Runtime, which Windows 11 and most up-to-date Windows 10
   PCs already have.

   ⚠️ The first time you run it, Windows may show a blue "Windows
   protected your PC" screen. That's Windows SmartScreen, which shows
   this for any program that isn't digitally signed with a paid
   certificate — it doesn't mean anything was found wrong with it.
   Click "More info", then "Run anyway". You'll only see this once.

2. Open ⚙️ Settings and point it at your external tools: MakeMKV
   (makemkvcon64.exe), mkvmerge.exe (MKVToolNix), Redumper, and ImgBurn.
   MakeMKV is found automatically if it's installed in the usual place.
   Add a free TMDB and/or RAWG API key if you want artwork.
3. Insert a disc — ArchivSector-OD detects your optical drive(s)
   automatically and shows a Drive Bay card for each one.

------------------------------------------------------------
BUILDING FROM SOURCE (for developers)
------------------------------------------------------------
You'll need Windows 10/11, the .NET 8 SDK, and Visual Studio 2022 (or
later) with the ".NET desktop development" workload. Open the solution
and build, or publish a release build from a command prompt in the
project folder:
    dotnet publish -c Release -r win-x64 --self-contained true
If a build shows errors that don't make sense, delete the bin and obj
folders and Rebuild Solution. ArchivSector-OD relies on Windows-only
APIs (tray eject, registry, system sounds), so it only runs on Windows.

========================================================================
📀 SUPPORTED DISC TYPES
========================================================================
• DVD Video ------------------------------- (1:1 ISO or Movie-Only MKV)
• Blu-ray (BD-25 / BD-50) ------------------ (1:1 Decrypt or Movie-Only MKV)
• 4K UHD Blu-ray (LibreDrive-detected) ----- (1:1 Decrypt or Movie-Only MKV)
• Game Discs (PC / console, via Redumper) — (1:1 archival dump)

========================================================================
🔮 PLANNED — NOT YET BUILT
========================================================================

☁️ Cloud/NAS Mirror
• Auto-sync completed archives to a secondary backup location.
