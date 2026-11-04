namespace ArchivSector_OD.Burning
{
    // A .dvd file (v4.0 beta 4): ImgBurn's small text file for a dual-layer
    // image, made by ImgBurn, abgx360 and others. Two lines:
    //     LayerBreak=1913760
    //     Game.iso
    // The image is burned with layer 0 ending after that many sectors. The
    // image name can be relative to the .dvd's folder or a full path.
    public sealed class DvdLayerFile
    {
        public string DvdPath = "";
        public string ImagePath = "";
        public long LayerBreak;          // sectors in layer 0
        public long ImageBytes;
        public string? Problem;          // why it can't be burned, if it can't

        public static bool IsDvdFile(string path) => path.EndsWith(".dvd", StringComparison.OrdinalIgnoreCase);

        public static DvdLayerFile Load(string dvdPath)
        {
            var d = new DvdLayerFile { DvdPath = dvdPath };
            try
            {
                var fi = new FileInfo(dvdPath);
                if (!fi.Exists) { d.Problem = "The .dvd file isn't there any more."; return d; }
                if (fi.Length > 64 * 1024) { d.Problem = "This isn't a layer break (.dvd) file: it's too big."; return d; }
                string? image = null;
                foreach (var raw in File.ReadAllLines(dvdPath))
                {
                    var line = raw.Trim().Trim('"');
                    if (line.Length == 0) continue;
                    int eq = line.IndexOf('=');
                    if (eq > 0 && line[..eq].Trim().Equals("LayerBreak", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(line[(eq + 1)..].Trim(), out var lb)) d.LayerBreak = lb;
                    }
                    else if (image is null) image = line;
                }
                if (d.LayerBreak <= 0) { d.Problem = "The .dvd file doesn't give a layer break (a line like LayerBreak=1913760)."; return d; }
                if (image is null) { d.Problem = "The .dvd file doesn't name its image file."; return d; }
                var dir = Path.GetDirectoryName(Path.GetFullPath(dvdPath)) ?? ".";
                var named = Path.IsPathRooted(image) ? image : Path.Combine(dir, image);
                // The name as written; next to the .dvd file; with ".iso" added
                // (some tools leave it off, and ImgBurn copes); or an ISO with
                // the .dvd file's own name.
                var candidates = new[]
                {
                    named,
                    Path.Combine(dir, Path.GetFileName(image)),
                    named + ".iso",
                    Path.Combine(dir, Path.GetFileName(image) + ".iso"),
                    Path.ChangeExtension(Path.GetFullPath(dvdPath), ".iso"),
                };
                var full = candidates.FirstOrDefault(File.Exists);
                if (full is null) { d.Problem = $"The .dvd file names \"{image}\", but that file isn't next to it."; return d; }
                d.ImagePath = full;
                d.ImageBytes = new FileInfo(full).Length;
                if (d.ImageBytes == 0 || d.ImageBytes % 2048 != 0) { d.Problem = $"{Path.GetFileName(full)} isn't a disc image made of whole 2 KB sectors."; return d; }
                long sectors = d.ImageBytes / 2048;
                if (d.LayerBreak >= sectors) { d.Problem = $"The layer break ({d.LayerBreak:N0}) is past the end of the image ({sectors:N0} sectors)."; return d; }
                if (d.LayerBreak * 2 < sectors) { d.Problem = $"The layer break ({d.LayerBreak:N0}) would leave more than half the image ({sectors:N0} sectors) for layer 1, which a DVD+R DL can't hold."; return d; }
            }
            catch (Exception ex) { d.Problem = "The .dvd file couldn't be read: " + ex.Message; }
            return d;
        }

        public string Summary => Problem ?? $"layer break at sector {LayerBreak:N0} · {Path.GetFileName(ImagePath)} · {ImageBytes / 1_000_000_000.0:0.00} GB";
    }
}
