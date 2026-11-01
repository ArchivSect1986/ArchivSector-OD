using System;
using System.Collections.Generic;

namespace ArchivSector_OD
{
    // Files dropped from Explorer onto the app (v4.0 beta 4). The page can't
    // see a dropped file's path, so index.html hands the files to WebView2
    // (postMessageWithAdditionalObjects) and MainWindow gets their paths
    // here. "target" is the drop zone's name (data-file-drop="..." on the
    // element), so each panel only takes the drops meant for it.
    public static class FileDrop
    {
        public static event Action<string, IReadOnlyList<string>>? Dropped;

        internal static void Raise(string target, IReadOnlyList<string> paths)
        {
            if (paths.Count == 0) return;
            try { Dropped?.Invoke(target, paths); } catch { }
        }
    }
}
