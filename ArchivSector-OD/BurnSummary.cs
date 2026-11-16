using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ArchivSector_OD.Burning;

namespace ArchivSector_OD
{
    // The summary card of a burn (beta 5, on the burn screen; beta 6, shared
    // with the burn queue): what was burned, on what, how, and what the
    // verify found, as rows for the card and as text for 📋 Copy summary.
    public static class BurnSummary
    {
        public sealed record Row(string Label, string Value, string Colour);

        /// <summary>One burn, remembered when it starts and filled in when it ends.</summary>
        public sealed class Info
        {
            public string ImagePath = "";
            public long ImageBytes;
            // The disc type, remembered at the start: once burned, the disc
            // isn't blank any more and the drive no longer says what it was.
            public string Disc = "";
            public string DriveLetter = "";
            public string DriveModel = "";
            public string Engine = "";
            public bool Switched;            // the own writer couldn't start and another program took over
            public double SpeedChosen;       // 0 = fastest
            public double OneX = 4_500_000;  // bytes per second of 1x for this disc
            public BurnEngine.Result? Result;
            public DiscHealthService.Result? Health;
            public DateTime Time;
        }

        /// <summary>Bytes per second of 1x for a disc type ("BD-R", "DVD+RW", "CD-R"...).</summary>
        public static double OneXFor(string media) =>
            (media ?? "").StartsWith("BD") ? 4_500_000 : (media ?? "").StartsWith("DVD") ? 1_385_000 : 153_600;

        public static bool HealthBad(Info i) => i.Health?.Verdict == DiscHealthService.Level.Damaged;

        /// <summary>The headline's colour: green verified, red failed, amber burned but not checked.</summary>
        public static string Colour(Info i) =>
            HealthBad(i) ? "var(--danger)" : i.Result?.Verified == true ? "var(--success)"
            : i.Result?.Verified == false || i.Result?.Burned != true ? "var(--danger)" : "var(--amber)";

        public static string Icon(Info i) =>
            HealthBad(i) ? "⚠" : i.Result?.Verified == true ? "✅" : i.Result?.Verified == false || i.Result?.Burned != true ? "⚠" : "✓";

        public static List<Row> Rows(Info i)
        {
            var rows = new List<Row>();
            var result = i.Result;
            if (result is null) return rows;
            const string plain = "var(--text-primary)", good = "var(--success)", bad = "var(--danger)", muted = "var(--text-secondary)";

            rows.Add(new("Image", $"{Path.GetFileName(i.ImagePath)}{(i.ImageBytes > 0 ? $" ({SizeText(i.ImageBytes)})" : "")}", plain));
            rows.Add(new("Disc", $"{(i.Disc.Length > 0 ? i.Disc : "Disc")} in {i.DriveLetter}: {i.DriveModel}".TrimEnd(), plain));

            string writer = i.Switched ? $"{i.Engine} couldn't start; another program took over" : i.Engine;
            rows.Add(new("Writer", writer, plain));
            string speed = (i.SpeedChosen > 0 ? BurnEngine.SpeedText(i.SpeedChosen) : "fastest") + " chosen";
            if (result.DriveSpeed.Length > 0 && !i.Switched)
                speed += $" · drive set {result.DriveSpeed}";
            if (result.Burned && result.BurnTime.TotalSeconds > 1 && i.ImageBytes > 0 && !i.Switched)
                speed += $" · averaged {DiscHealthService.FormatSpeed(i.ImageBytes / result.BurnTime.TotalSeconds, i.OneX)}";
            rows.Add(new("Speed", speed, plain));
            rows.Add(new("Burn", result.Burned ? $"✓ written in {Dur(result.BurnTime)}" : result.Cancelled ? "stopped" : "⚠ not burned", result.Burned ? plain : bad));

            if (result.Verified == true)
                rows.Add(new("Verify", $"✓ every sector matches the image{(result.VerifiedByImgBurn ? " (checked by ImgBurn)" : "")} · {Dur(result.VerifyTime)}", good));
            else if (result.Verified == false)
            {
                string what = result.DifferentSectors + result.UnreadableSectors > 0
                    ? $"⚠ {result.DifferentSectors:N0} sector(s) differ, {result.UnreadableSectors:N0} can't be read"
                    : $"⚠ {result.MismatchedSectors:N0} sector(s) don't match";
                rows.Add(new("Verify", what + (result.VerifyTime > TimeSpan.Zero ? $" · {Dur(result.VerifyTime)}" : ""), bad));
                string where = result.BadRanges.Count > 0
                    ? string.Join(", ", result.BadRanges.Take(8).Select(b => b.Count == 1 ? b.Lba.ToString("N0") : $"{b.Lba:N0}–{b.Lba + b.Count - 1:N0}"))
                      + (result.BadRanges.Count > 8 ? $" and {result.BadRanges.Count - 8} more ranges (see the log)" : "")
                    : result.BadSectorLbas.Count > 0
                    ? string.Join(", ", result.BadSectorLbas.Take(12).Select(l => l.ToString("N0"))) + (result.BadSectorLbas.Count > 12 ? $" and {result.BadSectorLbas.Count - 12} more (see the log)" : "")
                    : "";
                if (where.Length > 0) rows.Add(new("Where", "sectors " + where, bad));
            }
            else if (result.Burned)
                rows.Add(new("Verify", result.Cancelled ? "stopped before the end: the disc wasn't fully checked" : "not verified", muted));

            if (i.Health is not null) rows.Add(new("Health", BurnService.HealthLine(i.Health), HealthBad(i) ? bad : plain));
            return rows;
        }

        /// <summary>The summary as text: the result and its rows, lined up.</summary>
        public static string Body(Info i)
        {
            var sb = new System.Text.StringBuilder();
            if (i.Result is not null) sb.AppendLine(i.Result.Message);
            foreach (var row in Rows(i)) sb.AppendLine($"{row.Label,-8} {row.Value}");
            return sb.ToString();
        }

        /// <summary>The text 📋 Copy summary puts on the clipboard for one burn.</summary>
        public static string Text(Info i) =>
            $"ArchivSector-OD v{UpdateCheckService.CurrentVersionDisplay}: burn of {i.Time:yyyy-MM-dd HH:mm}{Environment.NewLine}" + Body(i);

        public static string Dur(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min" : $"{t.Seconds}s";

        public static string SizeText(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0} MB";
    }
}
