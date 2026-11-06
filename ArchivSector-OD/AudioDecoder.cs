using System;
using System.IO;
using System.Threading;
using NAudio.Wave;

namespace ArchivSector_OD
{
    // Music files to CD audio (v4.0 beta 4), for Music CD mode: FLAC, MP3,
    // M4A/AAC, WMA, and WAV files that aren't 44.1 kHz 16-bit stereo. The
    // decoding is Windows' own (Media Foundation, through NAudio): Windows 10
    // and 11 read all of these without anything extra installed. The result
    // is resampled to 44.1 kHz, 16-bit stereo (mono is copied to both
    // speakers) and saved as a plain .wav the CD writer burns.
    // Media Foundation objects belong to the thread that makes them, so
    // each call makes and uses its own on the calling (background) thread.
    public static class AudioDecoder
    {
        /// <summary>The kinds of music file Music CD mode takes.</summary>
        public static readonly string[] Extensions = { ".wav", ".flac", ".mp3", ".m4a", ".aac", ".wma" };

        public static bool IsMusicFile(string path) =>
            Array.Exists(Extensions, e => path.EndsWith(e, StringComparison.OrdinalIgnoreCase));

        /// <summary>How long the music is, or null (with why) if Windows can't read the file.</summary>
        public static TimeSpan? Length(string path, out string? problem)
        {
            problem = null;
            try
            {
                using var reader = new MediaFoundationReader(path);
                var t = reader.TotalTime;
                if (t <= TimeSpan.Zero) { problem = "It has no music in it."; return null; }
                return t;
            }
            catch (Exception ex)
            {
                problem = "Windows can't read it (" + ex.Message.Trim().TrimEnd('.') + "). FLAC, MP3, M4A/AAC, WMA and WAV files work.";
                return null;
            }
        }

        /// <summary>
        /// Converts a music file to a CD-audio .wav (44.1 kHz, 16-bit,
        /// stereo). progress gets 0 to 1. A half-written file is removed if
        /// it fails or is cancelled.
        /// </summary>
        public static void ToCdWav(string source, string target, IProgress<double>? progress, CancellationToken ct)
        {
            try
            {
                using var reader = new MediaFoundationReader(source);
                using var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 2)) { ResamplerQuality = 60 };
                using var writer = new WaveFileWriter(target, resampler.WaveFormat);
                var buffer = new byte[176_400];   // one second
                long length = Math.Max(1, reader.Length);
                int n;
                while ((n = resampler.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(buffer, 0, n);
                    progress?.Report(Math.Min(1.0, reader.Position / (double)length));
                }
            }
            catch
            {
                try { if (File.Exists(target)) File.Delete(target); } catch { }
                throw;
            }
        }
    }
}
