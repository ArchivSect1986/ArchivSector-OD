# 📂 ArchivSector-OD

Developed by **ArchivSect1986**, **ArchivSector-OD** is a powerful automation frontend designed to streamline optical disc archiving workflows. It simplifies the disc preservation process by managing file structures, verifying data integrity, and orchestrating external tools through a clean, unified interface.

**Inspiration:** ArchivSector-OD's concept was inspired by [SabreTools' Media Preservation Frontend (MPF)](https://github.com/SabreTools/MPF), an open-source Redumper/Aaru/DiscImageCreator GUI for disc preservation. ArchivSector-OD is an independently-built project with its own codebase (no code, assets, or binaries shared between the two), distinguished by MakeMKV-driven AACS/BD+ decryption for protected Blu-ray/4K UHD content — a capability outside the scope of MPF's raw-dump-focused backends.

---

## 🚀 Key Features

* 💿 **Blu-ray, 4K UHD & DVD Backup:** Direct 1:1 backup (MakeMKV decrypt for Blu-ray/UHD, Redumper/ImgBurn for DVD) or Movie-Only MKV extraction that pulls just the main title.
* 🎚️ **Per-Track Audio/Subtitle Selection:** Choose exactly which audio and subtitle tracks to keep in Movie-Only MKV output, with language, codec, bitrate, and channel info shown per track — trimmed reliably via mkvmerge. The output file name can be edited before extraction starts.
* 🤖 **Unattended Operation:** Optional unattended mode that auto-starts ripping upon physical disc insertion — movies and DVDs always go Full Backup, with the rip-mode prompt, destination folder, and file name all auto-filled (saved as "Title (2)" instead of overwriting if that name already exists) — plus a Batch Queue Mode that auto-continues to the next disc after each one finishes, with a per-drive session counter. Multi-Drive Batch Sync can cap how many drive bays rip at once, queuing extra discs until a slot frees up.
* 📁 **Automated File Organization:** Auto-categorizes movie and game sub-folders (`\BluRay_Movies\`, `\DVD_Movies\`, and `\Games\<console>\`), with full backups sorted by disc size (`BD-25`/`BD-50`/`DVD-5`/`DVD-9`) and Movie-Only MKVs in their own `MKV` folder.
* 🔒 **Data Integrity Verification:** Automatically generates SHA1 and MD5 checksums for every archive, with optional verification against imported Redump DAT files and an honest match/no-match/not-applicable status reported at completion.
* 📊 **Archival History:** A searchable, filterable log of every rip, with a stats summary (total rips, Redump match rate, total data archived, breakdown by system) and the ability to export a filtered view to its own CSV.
* 🎮 **Game Disc Archival:** Dedicated Redumper-driven pipeline for game discs. PlayStation 1/2/3/4 and Xbox One discs are recognized automatically and sorted into a folder per console, with PlayStation serial numbers recorded and the real game name read from PS3, PS4 and Xbox One discs. PS4 and Xbox One dumps are encrypted exact copies for preservation and Redump verification; they can't be played or installed directly. Original Xbox discs are recognized as soon as they're inserted. Xbox 360 discs (and any Original Xbox disc the app can't tell beforehand) are identified from Redumper's log once the dump finishes, on a drive that can read them such as one with OmniDrive firmware, and moved into their own console folder. PC game discs and other unrecognized data discs can be dumped too, CDs come out as `.bin` + `.cue`, and music CDs (including burned CDs of MP3s) can be copied exactly into their own folder. Each dump can be named before, during, or after it rips.
* 🖼️ **Metadata & Artwork:** Automatic lookup of movie titles/artwork on disc insertion via TMDB, plus manual search for movies (TMDB) and games (RAWG). Both use free API keys.
* 💡 **LibreDrive Detection:** Drive bays show OmniDrive-compatible drive models, and automatically confirm LibreDrive mode when MakeMKV reports it during a rip.
* ⚡ **Smart Workflow Automation:** Duplicate-output warnings, a completion sound and Windows notification, and physical optical tray auto-ejection upon completion.
* 🔄 **One-Click Updates:** Checks GitHub for a new version at startup (can be turned off in Settings). With one click it downloads the update, verifies it, installs it and restarts, keeping your settings and history. It never updates in the middle of a rip.

---

## ⚖️ Legal Disclaimer & Compliance Statement

* **No Bundled Software:** This repository contains only original source code written for **ArchivSector-OD**. It does not host, mirror, package, or distribute any third-party binaries or proprietary executables.
* **No Circumvention Keys:** This software does not contain decryption keys or digital rights management (DRM) circumvention tools.
* **Compliance with Local Laws:** The author does not condone, promote, or facilitate copyright infringement. Digital Millennium Copyright Act (DMCA) regulations and copyright laws regarding private format-shifting vary by country.
* **User Responsibility:** The end-user assumes all legal responsibility for installing external dependencies, maintaining valid software licenses, and ensuring their physical media archiving workflows comply with local jurisdictions.
* **No Warranty:** This software is distributed for free under the MIT License "as-is", without any express or implied warranties regarding its functionality or interoperability.

---

## 🛠️ System Requirements & External Prerequisites

**Windows:** Windows 10 or 11 (64-bit). The app also needs the **Microsoft Edge WebView2 Runtime**, which is already installed on Windows 11 and most up-to-date Windows 10 PCs, and the **.NET 8 Desktop Runtime** unless you're using a self-contained release build.

To use **ArchivSector-OD**, you must independently download the following third-party utilities. The application relies entirely on your user-defined local file paths to communicate with these external command-line interfaces:

### 1. MakeMKV
* **Purpose:** Required for Blu-ray/4K UHD decrypt and backup, and for Movie-Only MKV extraction (DVD and Blu-ray/UHD alike) via `makemkvcon.exe`.
* **Source:** Download exclusively from the [Official MakeMKV Website](https://makemkv.com).

### 2. mkvmerge (MKVToolNix)
* **Purpose:** Required for Movie-Only MKV output — performs the actual per-track audio/subtitle trimming as a post-processing pass after MakeMKV's extraction.
* **Source:** Download from the [Official MKVToolNix Website](https://mkvtoolnix.download).

### 3. Redumper
* **Purpose:** Required for raw sector DVD dumps and for game disc archival.
* **Source:** Download from official community preservation channels.

### 4. ImgBurn *(Optional)*
* **Purpose:** Used as a fallback for DVD raw ISO dumps when Redumper isn't set up, and as an optional stage 2 that turns a decrypted Blu-ray/UHD backup into a single-file ISO.
* **Source:** Download from the official ImgBurn repository.

### 5. API Keys *(Optional, free)*
* **TMDB** ([themoviedb.org](https://www.themoviedb.org)) for movie titles and artwork.
* **RAWG** ([rawg.io/apidocs](https://rawg.io/apidocs)) for game titles and artwork.

---

## ⚙️ Initial Setup Guide

1. **Launch the App:** Unzip the release folder and run `ArchivSector-OD.exe`. If Windows shows a blue **"Windows protected your PC"** screen, click **More info → Run anyway**. Windows SmartScreen shows this for any program that isn't signed with a paid certificate; it doesn't mean anything was found wrong with it. Then click **⚙️ Settings** to open the **System Settings & Archival Preferences** window.
2. **Set Target Directory:** Configure your **Default Output Base Folder** to your preferred storage drive.
3. **Link External Utilities:** Paste or browse to the exact file paths for your local installations of `makemkvcon64.exe`, `mkvmerge.exe`, `redumper.exe`, and (optionally) `ImgBurn.exe`.
4. **Add API Keys (optional):** Paste your free TMDB and/or RAWG API keys to enable metadata and artwork lookup, then click **Save Preferences**.
