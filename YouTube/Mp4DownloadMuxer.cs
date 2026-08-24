using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace YouTube
{
    // Direct C# 6 port of youtube-ios/src/player/YTMp4Writer.m.
    // It turns YouTube's separate fragmented H.264/AAC MP4 tracks into one ordinary MP4.
    // Samples are copied byte-for-byte; no decode/encode or Media Foundation transcode occurs.
    internal static class Mp4DownloadMuxer
    {
        private const uint MovieScale = 1000;
        private const int CopyChunk = 1024 * 1024;
        private const long MaxCoalescedCopy = 8L * 1024L * 1024L;

        private sealed class TrackPlan
        {
            public DashTrackInit Init;
            public readonly List<long> Offsets = new List<long>();
            public readonly List<uint> Sizes = new List<uint>();
            public readonly List<uint> Durations = new List<uint>();
            public readonly List<int> Shifts = new List<int>();
            public readonly List<bool> Syncs = new List<bool>();
            public ulong TotalBytes;
            public ulong TotalTicks;
            public ulong ChunkStart;

            public int Count { get { return Sizes.Count; } }
            public int SyncCount
            {
                get
                {
                    var result = 0;
                    for (var i = 0; i < Syncs.Count; i++) if (Syncs[i]) result++;
                    return result;
                }
            }
        }

        private sealed class BoxHeader
        {
            public ulong Size;
            public int HeaderSize;
            public string Type;
        }

        public static async Task<bool> WriteAsync(
            StorageFile outputFile,
            StorageFile videoFile,
            StorageFile audioFile,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            var video = await PlanForAsync(videoFile, true, cancellationToken).ConfigureAwait(false);
            if (video == null || video.Init == null || video.Init.Sps.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine("[Downloads/Mux] Video track could not be parsed");
                return false;
            }

            var audio = audioFile == null
                ? null
                : await PlanForAsync(audioFile, false, cancellationToken).ConfigureAwait(false);
            if (audioFile != null && (audio == null || audio.Init == null
                || audio.Init.CodecPrivate == null || audio.Init.CodecPrivate.Length < 2))
            {
                System.Diagnostics.Debug.WriteLine("[Downloads/Mux] Audio track could not be parsed");
                return false;
            }

            System.Diagnostics.Debug.WriteLine(
                "[Downloads/Mux] video samples=" + video.Count + " bytes=" + video.TotalBytes
                + "; audio samples=" + (audio == null ? 0 : audio.Count));

            var ftypBody = new MemoryStream();
            PutTag(ftypBody, "isom");
            Put32(ftypBody, 512);
            PutTag(ftypBody, "isom");
            PutTag(ftypBody, "iso2");
            PutTag(ftypBody, "avc1");
            PutTag(ftypBody, "mp41");
            var header = Box("ftyp", ftypBody.ToArray());

            video.ChunkStart = 0;
            if (audio != null) audio.ChunkStart = video.TotalBytes;
            var draft = MoovFor(video, audio);
            var dataStart = (ulong)header.Length + (ulong)draft.Length + 16UL;
            video.ChunkStart = dataStart;
            if (audio != null) audio.ChunkStart = dataStart + video.TotalBytes;
            var moov = MoovFor(video, audio);
            if (moov.Length != draft.Length)
                throw new InvalidOperationException("MP4 metadata changed size while resolving chunk offsets");

            var payload = video.TotalBytes + (audio == null ? 0UL : audio.TotalBytes);
            var mdatHead = new MemoryStream();
            Put32(mdatHead, 1);
            PutTag(mdatHead, "mdat");
            Put64(mdatHead, payload + 16UL);

            var randomOutput = await outputFile.OpenAsync(FileAccessMode.ReadWrite);
            randomOutput.Size = 0;
            try
            {
                using (var output = randomOutput.AsStreamForWrite())
                {
                    await output.WriteAsync(header, 0, header.Length, cancellationToken).ConfigureAwait(false);
                    await output.WriteAsync(moov, 0, moov.Length, cancellationToken).ConfigureAwait(false);
                    var mdatBytes = mdatHead.ToArray();
                    await output.WriteAsync(mdatBytes, 0, mdatBytes.Length, cancellationToken).ConfigureAwait(false);

                    ulong done = 0;
                    done = await CopyPlanAsync(videoFile, video, output, done, payload,
                        cancellationToken, progress).ConfigureAwait(false);
                    if (audio != null)
                    {
                        done = await CopyPlanAsync(audioFile, audio, output, done, payload,
                            cancellationToken, progress).ConfigureAwait(false);
                    }
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                randomOutput.Dispose();
            }

            if (progress != null) progress.Report(100.0);
            System.Diagnostics.Debug.WriteLine(
                "[Downloads/Mux] completed without re-encoding; bytes="
                + ((ulong)header.Length + (ulong)moov.Length + 16UL + payload));
            return true;
        }

        private static async Task<TrackPlan> PlanForAsync(
            StorageFile file,
            bool isVideo,
            CancellationToken cancellationToken)
        {
            var random = await file.OpenAsync(FileAccessMode.Read);
            try
            {
                using (var stream = random.AsStreamForRead())
                {
                    var plan = new TrackPlan();
                    var length = (ulong)stream.Length;
                    ulong position = 0;
                    while (position + 8UL <= length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var head = await ReadHeaderAsync(stream, position, cancellationToken).ConfigureAwait(false);
                        if (head == null || head.Size < (ulong)head.HeaderSize || position + head.Size > length)
                            break;

                        if (head.Type == "moov")
                        {
                            if (position + head.Size > int.MaxValue) return null;
                            var initBytes = new byte[(int)(position + head.Size)];
                            stream.Position = 0;
                            if (!await ReadExactlyAsync(stream, initBytes, 0, initBytes.Length,
                                cancellationToken).ConfigureAwait(false)) return null;
                            plan.Init = Mp4.ParseInit(initBytes, 0, initBytes.Length, isVideo);
                        }
                        else if (head.Type == "moof")
                        {
                            var mdatPosition = position + head.Size;
                            var mdat = await ReadHeaderAsync(stream, mdatPosition, cancellationToken).ConfigureAwait(false);
                            if (mdat == null || mdat.Type != "mdat" || mdatPosition + mdat.Size > length)
                                break;
                            var pairSize = head.Size + mdat.Size;
                            if (pairSize > int.MaxValue || head.Size > int.MaxValue
                                || plan.Init == null) return null;
                            var moof = new byte[(int)head.Size];
                            stream.Position = (long)position;
                            if (!await ReadExactlyAsync(stream, moof, 0, moof.Length,
                                cancellationToken).ConfigureAwait(false)) return null;

                            var samples = Mp4.ParseFragmentMetadata(
                                moof, (int)pairSize, plan.Init, isVideo);
                            for (var i = 0; i < samples.Count; i++)
                            {
                                var sample = samples[i];
                                plan.Offsets.Add((long)position + sample.Offset);
                                plan.Sizes.Add((uint)sample.Size);
                                plan.Durations.Add(sample.DurationUnits);
                                plan.Shifts.Add(sample.CompositionOffsetUnits);
                                plan.Syncs.Add(sample.KeyFrame);
                                plan.TotalBytes += (uint)sample.Size;
                                plan.TotalTicks += sample.DurationUnits;
                            }
                            position += pairSize;
                            continue;
                        }
                        position += head.Size;
                    }

                    if (plan.Init == null || plan.Count == 0) return null;
                    System.Diagnostics.Debug.WriteLine(
                        "[Downloads/Mux] planned " + (isVideo ? "video" : "audio")
                        + ": timescale=" + plan.Init.Timescale + " samples=" + plan.Count);
                    return plan;
                }
            }
            finally
            {
                random.Dispose();
            }
        }

        private static async Task<ulong> CopyPlanAsync(
            StorageFile sourceFile,
            TrackPlan plan,
            Stream output,
            ulong done,
            ulong total,
            CancellationToken cancellationToken,
            IProgress<double> progress)
        {
            var random = await sourceFile.OpenAsync(FileAccessMode.Read);
            try
            {
                using (var source = random.AsStreamForRead())
                {
                    var buffer = new byte[CopyChunk];
                    var i = 0;
                    while (i < plan.Count)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var rangeStart = plan.Offsets[i];
                        long rangeLength = plan.Sizes[i];
                        var next = i + 1;
                        while (next < plan.Count
                            && plan.Offsets[next] == rangeStart + rangeLength
                            && rangeLength + plan.Sizes[next] <= MaxCoalescedCopy)
                        {
                            rangeLength += plan.Sizes[next];
                            next++;
                        }

                        source.Position = rangeStart;
                        var left = rangeLength;
                        while (left > 0)
                        {
                            var take = (int)Math.Min((long)buffer.Length, left);
                            var got = await source.ReadAsync(buffer, 0, take, cancellationToken).ConfigureAwait(false);
                            if (got <= 0) throw new EndOfStreamException("MP4 sample ended early");
                            await output.WriteAsync(buffer, 0, got, cancellationToken).ConfigureAwait(false);
                            left -= got;
                            done += (uint)got;
                        }
                        i = next;
                        if (progress != null)
                            progress.Report(total == 0 ? 0.0 : (double)done * 100.0 / total);
                    }
                }
            }
            finally
            {
                random.Dispose();
            }
            return done;
        }

        private static byte[] MoovFor(TrackPlan video, TrackPlan audio)
        {
            var duration = video.Init.Timescale == 0 ? 0UL
                : video.TotalTicks * MovieScale / video.Init.Timescale;
            var mvhd = new MemoryStream();
            Put32(mvhd, 0); Put32(mvhd, 0);
            Put32(mvhd, MovieScale); Put32(mvhd, (uint)duration);
            Put32(mvhd, 0x00010000); Put16(mvhd, 0x0100); Put16(mvhd, 0);
            Put32(mvhd, 0); Put32(mvhd, 0);
            PutMatrix(mvhd);
            for (var i = 0; i < 6; i++) Put32(mvhd, 0);
            Put32(mvhd, audio == null ? 2U : 3U);

            var body = new MemoryStream();
            Append(body, FullBox("mvhd", 0, 0, mvhd.ToArray()));
            Append(body, TrakFor(video, 1));
            if (audio != null) Append(body, TrakFor(audio, 2));
            return Box("moov", body.ToArray());
        }

        private static byte[] TrakFor(TrackPlan plan, uint number)
        {
            var video = plan.Init.Sps.Count > 0;
            var duration = plan.Init.Timescale == 0 ? 0UL
                : plan.TotalTicks * MovieScale / plan.Init.Timescale;
            var tkhd = new MemoryStream();
            Put32(tkhd, 0); Put32(tkhd, 0); Put32(tkhd, number); Put32(tkhd, 0);
            Put32(tkhd, (uint)duration); Put32(tkhd, 0); Put32(tkhd, 0);
            Put16(tkhd, 0); Put16(tkhd, 0); Put16(tkhd, (ushort)(video ? 0 : 0x0100)); Put16(tkhd, 0);
            PutMatrix(tkhd);
            Put32(tkhd, video ? (uint)(plan.Init.Width << 16) : 0);
            Put32(tkhd, video ? (uint)(plan.Init.Height << 16) : 0);

            var trak = new MemoryStream();
            Append(trak, FullBox("tkhd", 0, 3, tkhd.ToArray()));

            var mdhd = new MemoryStream();
            Put32(mdhd, 0); Put32(mdhd, 0); Put32(mdhd, plan.Init.Timescale);
            Put32(mdhd, (uint)plan.TotalTicks); Put16(mdhd, 0x55C4); Put16(mdhd, 0);
            var mdia = new MemoryStream();
            Append(mdia, FullBox("mdhd", 0, 0, mdhd.ToArray()));

            var hdlr = new MemoryStream();
            Put32(hdlr, 0); PutTag(hdlr, video ? "vide" : "soun");
            Put32(hdlr, 0); Put32(hdlr, 0); Put32(hdlr, 0); Put8(hdlr, 0);
            Append(mdia, FullBox("hdlr", 0, 0, hdlr.ToArray()));

            var minf = new MemoryStream();
            if (video)
            {
                var vmhd = new MemoryStream();
                Put16(vmhd, 0); Put16(vmhd, 0); Put16(vmhd, 0); Put16(vmhd, 0);
                Append(minf, FullBox("vmhd", 0, 1, vmhd.ToArray()));
            }
            else
            {
                var smhd = new MemoryStream();
                Put16(smhd, 0); Put16(smhd, 0);
                Append(minf, FullBox("smhd", 0, 0, smhd.ToArray()));
            }

            var dref = new MemoryStream();
            Put32(dref, 1); Append(dref, FullBox("url ", 0, 1, null));
            var dinf = new MemoryStream();
            Append(dinf, FullBox("dref", 0, 0, dref.ToArray()));
            Append(minf, Box("dinf", dinf.ToArray()));
            Append(minf, StblFor(plan));
            Append(mdia, Box("minf", minf.ToArray()));
            Append(trak, Box("mdia", mdia.ToArray()));
            return Box("trak", trak.ToArray());
        }

        private static byte[] StblFor(TrackPlan plan)
        {
            var video = plan.Init.Sps.Count > 0;
            var entry = new MemoryStream();
            for (var i = 0; i < 6; i++) Put8(entry, 0);
            Put16(entry, 1);
            if (video)
            {
                Put16(entry, 0); Put16(entry, 0);
                Put32(entry, 0); Put32(entry, 0); Put32(entry, 0);
                Put16(entry, (ushort)plan.Init.Width); Put16(entry, (ushort)plan.Init.Height);
                Put32(entry, 0x00480000); Put32(entry, 0x00480000); Put32(entry, 0);
                Put16(entry, 1);
                for (var i = 0; i < 32; i++) Put8(entry, 0);
                Put16(entry, 0x0018); Put16(entry, 0xFFFF);
                Append(entry, Box("avcC", AvccFor(plan.Init)));
            }
            else
            {
                Put32(entry, 0); Put32(entry, 0);
                Put16(entry, (ushort)Math.Max(1, plan.Init.Channels)); Put16(entry, 16);
                Put16(entry, 0); Put16(entry, 0);
                Put16(entry, (ushort)AudioRate(plan.Init)); Put16(entry, 0);
                Append(entry, Box("esds", EsdsFor(plan.Init)));
            }

            var stsd = new MemoryStream();
            Put32(stsd, 1); Append(stsd, Box(video ? "avc1" : "mp4a", entry.ToArray()));
            var stbl = new MemoryStream();
            Append(stbl, FullBox("stsd", 0, 0, stsd.ToArray()));

            var sttsEntries = new MemoryStream();
            uint runs = 0;
            for (var i = 0; i < plan.Count;)
            {
                var value = plan.Durations[i];
                uint same = 1;
                while (i + same < plan.Count && plan.Durations[i + (int)same] == value) same++;
                Put32(sttsEntries, same); Put32(sttsEntries, value);
                runs++; i += (int)same;
            }
            var stts = new MemoryStream();
            Put32(stts, runs); Append(stts, sttsEntries.ToArray());
            Append(stbl, FullBox("stts", 0, 0, stts.ToArray()));

            var minimumShift = 0;
            var shifted = false;
            for (var i = 0; i < plan.Count; i++)
            {
                if (plan.Shifts[i] < minimumShift) minimumShift = plan.Shifts[i];
                if (plan.Shifts[i] != 0) shifted = true;
            }
            var lift = -minimumShift;
            if (shifted)
            {
                var cttsEntries = new MemoryStream();
                uint entries = 0;
                for (var i = 0; i < plan.Count;)
                {
                    var value = plan.Shifts[i];
                    uint same = 1;
                    while (i + same < plan.Count && plan.Shifts[i + (int)same] == value) same++;
                    Put32(cttsEntries, same); Put32(cttsEntries, unchecked((uint)(value + lift)));
                    entries++; i += (int)same;
                }
                var ctts = new MemoryStream();
                Put32(ctts, entries); Append(ctts, cttsEntries.ToArray());
                Append(stbl, FullBox("ctts", 0, 0, ctts.ToArray()));
            }

            var syncCount = plan.SyncCount;
            if (video && syncCount > 0 && syncCount < plan.Count)
            {
                var stss = new MemoryStream();
                Put32(stss, (uint)syncCount);
                for (var i = 0; i < plan.Count; i++) if (plan.Syncs[i]) Put32(stss, (uint)(i + 1));
                Append(stbl, FullBox("stss", 0, 0, stss.ToArray()));
            }

            var stsc = new MemoryStream();
            Put32(stsc, 1); Put32(stsc, 1); Put32(stsc, (uint)plan.Count); Put32(stsc, 1);
            Append(stbl, FullBox("stsc", 0, 0, stsc.ToArray()));

            var stsz = new MemoryStream();
            Put32(stsz, 0); Put32(stsz, (uint)plan.Count);
            for (var i = 0; i < plan.Count; i++) Put32(stsz, plan.Sizes[i]);
            Append(stbl, FullBox("stsz", 0, 0, stsz.ToArray()));

            var co64 = new MemoryStream();
            Put32(co64, 1); Put64(co64, plan.ChunkStart);
            Append(stbl, FullBox("co64", 0, 0, co64.ToArray()));
            return Box("stbl", stbl.ToArray());
        }

        private static byte[] AvccFor(DashTrackInit init)
        {
            if (init.Sps.Count == 0 || init.Sps[0].Length < 4)
                throw new InvalidOperationException("H.264 SPS is missing");
            var body = new MemoryStream();
            var first = init.Sps[0];
            Put8(body, 1); Put8(body, first[1]); Put8(body, first[2]); Put8(body, first[3]);
            Put8(body, (byte)(0xFC | (Math.Max(1, init.NalLengthSize) - 1)));
            Put8(body, (byte)(0xE0 | init.Sps.Count));
            for (var i = 0; i < init.Sps.Count; i++)
            {
                Put16(body, (ushort)init.Sps[i].Length); Append(body, init.Sps[i]);
            }
            Put8(body, (byte)init.Pps.Count);
            for (var i = 0; i < init.Pps.Count; i++)
            {
                Put16(body, (ushort)init.Pps[i].Length); Append(body, init.Pps[i]);
            }
            return body.ToArray();
        }

        private static byte[] EsdsFor(DashTrackInit init)
        {
            var asc = init.CodecPrivate;
            if (asc == null || asc.Length < 2) throw new InvalidOperationException("AAC config is missing");
            var body = new MemoryStream();
            Put32(body, 0);
            Put8(body, 0x03); Put8(body, (byte)(23 + asc.Length)); Put16(body, 0); Put8(body, 0);
            Put8(body, 0x04); Put8(body, (byte)(15 + asc.Length)); Put8(body, 0x40); Put8(body, 0x15);
            Put8(body, 0); Put16(body, 0); Put32(body, 0); Put32(body, 0);
            Put8(body, 0x05); Put8(body, (byte)asc.Length); Append(body, asc);
            Put8(body, 0x06); Put8(body, 1); Put8(body, 0x02);
            return body.ToArray();
        }

        private static uint AudioRate(DashTrackInit init)
        {
            if (init.SampleRate > 0) return (uint)init.SampleRate;
            var asc = init.CodecPrivate;
            if (asc == null || asc.Length < 2) return 44100;
            var index = ((asc[0] & 0x07) << 1) | ((asc[1] >> 7) & 1);
            var rates = new uint[] { 96000, 88200, 64000, 48000, 44100, 32000, 24000,
                22050, 16000, 12000, 11025, 8000, 7350, 0, 0, 0 };
            return rates[index] == 0 ? 44100U : rates[index];
        }

        private static async Task<BoxHeader> ReadHeaderAsync(
            Stream stream, ulong position, CancellationToken cancellationToken)
        {
            if (position > (ulong)long.MaxValue) return null;
            var bytes = new byte[16];
            stream.Position = (long)position;
            if (!await ReadExactlyAsync(stream, bytes, 0, 8, cancellationToken).ConfigureAwait(false))
                return null;
            var size = Read32(bytes, 0);
            var result = new BoxHeader { Size = size, HeaderSize = 8, Type = ReadTag(bytes, 4) };
            if (size == 1)
            {
                if (!await ReadExactlyAsync(stream, bytes, 8, 8, cancellationToken).ConfigureAwait(false))
                    return null;
                result.Size = Read64(bytes, 8);
                result.HeaderSize = 16;
            }
            else if (size == 0)
            {
                result.Size = (ulong)stream.Length - position;
            }
            return result;
        }

        private static async Task<bool> ReadExactlyAsync(
            Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            while (count > 0)
            {
                var got = await stream.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
                if (got <= 0) return false;
                offset += got; count -= got;
            }
            return true;
        }

        private static byte[] FullBox(string tag, byte version, uint flags, byte[] body)
        {
            var inner = new MemoryStream();
            Put32(inner, ((uint)version << 24) | (flags & 0x00FFFFFF));
            Append(inner, body);
            return Box(tag, inner.ToArray());
        }

        private static byte[] Box(string tag, byte[] body)
        {
            body = body ?? new byte[0];
            var result = new MemoryStream();
            Put32(result, (uint)(8 + body.Length)); PutTag(result, tag); Append(result, body);
            return result.ToArray();
        }

        private static void PutMatrix(Stream output)
        {
            var matrix = new uint[] { 0x00010000, 0, 0, 0, 0x00010000, 0, 0, 0, 0x40000000 };
            for (var i = 0; i < matrix.Length; i++) Put32(output, matrix[i]);
        }

        private static void Append(Stream output, byte[] bytes)
        {
            if (bytes != null && bytes.Length > 0) output.Write(bytes, 0, bytes.Length);
        }

        private static void Put8(Stream output, byte value) { output.WriteByte(value); }
        private static void Put16(Stream output, ushort value)
        {
            output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value);
        }
        private static void Put32(Stream output, uint value)
        {
            output.WriteByte((byte)(value >> 24)); output.WriteByte((byte)(value >> 16));
            output.WriteByte((byte)(value >> 8)); output.WriteByte((byte)value);
        }
        private static void Put64(Stream output, ulong value)
        {
            Put32(output, (uint)(value >> 32)); Put32(output, (uint)value);
        }
        private static void PutTag(Stream output, string tag)
        {
            for (var i = 0; i < 4; i++) output.WriteByte((byte)tag[i]);
        }
        private static uint Read32(byte[] bytes, int offset)
        {
            return ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16)
                | ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];
        }
        private static ulong Read64(byte[] bytes, int offset)
        {
            return ((ulong)Read32(bytes, offset) << 32) | Read32(bytes, offset + 4);
        }
        private static string ReadTag(byte[] bytes, int offset)
        {
            return "" + (char)bytes[offset] + (char)bytes[offset + 1]
                + (char)bytes[offset + 2] + (char)bytes[offset + 3];
        }
    }
}
