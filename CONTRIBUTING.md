# 🤝 Contributing to ArchivSector-OD

Thank you for your interest in improving **ArchivSector-OD**! We welcome community contributions, bug fixes, and feature suggestions to help make optical disc archiving easier for everyone.

---

## 📜 Code of Conduct

* **Be Respectful:** Treat all contributors, testers, and users with kindness and respect.
* **Keep It Legal:** Do not submit code snippets, keys, or tools that violate copyrights, licenses, or bypass DRM. All pull requests must contain original code only.

---

## 🛠️ How to Contribute

### 1. Reporting Bugs
If you find a bug or layout issue, please open an **Issue** on GitHub and include:
* A clear description of the problem.
* Steps to reproduce the issue.
* Your operating system and the optical drive model used (if relevant).
* The **Process Telemetry** log from the affected drive bay, if the problem happened during a rip.

### 2. Suggesting Features
Have an idea for a new feature or layout style? Open an **Issue** with the tag `enhancement` and describe how it would improve the archiving workflow.

### 3. Submitting Code Changes (Pull Requests)
If you want to write code for the project, please follow these steps:
1. **Fork** the repository to your own GitHub account.
2. Create a new **feature branch** for your changes (`git checkout -b feature/your-feature-name`).
3. Commit your changes with clear, descriptive commit messages.
4. Push your branch to GitHub and open a **Pull Request (PR)** against our main branch.

---

## 🧰 Building From Source

* **Requirements:** Windows 10/11, the **.NET 8 SDK**, and **Visual Studio 2022** (or later) with the **.NET desktop development** workload.
* **Project type:** WPF app (`net8.0-windows`) hosting a Blazor Hybrid `BlazorWebView`. The main UI lives in `AppShell.razor` and `DriveBayCard.razor`; pop-up windows (Settings, History, ISO Verifier, title/track pickers, metadata search) are native WPF windows.
* **If a build shows errors that don't make sense:** delete the `bin` and `obj` folders, then **Rebuild Solution**. Stale generated files are a common cause.

---

## 💻 Code Style Guidelines

* **Keep Layouts Flexible:** When updating UI or grid structures, prefer responsive layouts (CSS grid/flex in the `.razor` files, `Grid` rows/columns in XAML) over fixed pixel sizes, to accommodate different monitor dimensions.
* **Avoid Duplicating Pipeline Logic:** Similar disc-type workflows (e.g. Blu-ray vs. DVD ripping) should share a single implementation wherever their steps genuinely overlap, rather than being copy-pasted per disc type. `RunFullBackup()` and `PickMovieOnly()` in `DriveBayCard.razor` each handle both Blu-ray and DVD, and `RunRedumperDump()` handles both DVD raw dumps and game discs. Only split logic out where the underlying operation is genuinely different.
* **Keep External Tools in Services:** Calls to MakeMKV, mkvmerge, Redumper, and ImgBurn belong in their own service classes (`MakeMkvService`, `MkvmergeService`, `RedumperService`, `ImgBurnService`), not inline in UI code.
* **Error Handling:** Ensure any external command-line calls fail gracefully with clear error messages if a user's local path is configured incorrectly. A failure should show in the bay's status and log, never crash the app.
* **Open WPF Windows with `Show()`, never `ShowDialog()`:** `ShowDialog()` runs a nested message loop on the same thread WebView2 uses, which can close the whole app without any error. To wait for a window's result, give it a `TaskCompletionSource` and `await` its `ResultTask` (see `TitlePickerWindow` for the pattern).
* **WPF Data Binding Needs Properties:** Classes shown through `{Binding}` must use `{ get; set; }` properties, not plain public fields. Fields bind silently to nothing, with no error.
* **Read Config Fresh:** Call `ConfigService.Load()` at the start of each operation rather than caching it, so Settings changes take effect on the very next rip.
* **Clean Code:** Use clear variable names and comment on complex automation or multi-threading logic.
