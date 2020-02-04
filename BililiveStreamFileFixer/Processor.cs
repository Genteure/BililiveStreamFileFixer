/*
B站直播录像修复工具
Copyright(C) 2020 Genteure

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.If not, see<https://www.gnu.org/licenses/>.
*/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace BililiveStreamFileFixer
{
    internal class Processor : IDisposable
    {
        private readonly FileStream Input;
        private readonly FileInfo FileInfo;

        private Memory<FlvTag> Tags;
        private readonly List<Memory<FlvTag>> Slices = new List<Memory<FlvTag>>();
        private readonly List<Dictionary<long, int>> JumpTables = new List<Dictionary<long, int>>();

        private const int MINIMAL_TAG_IN_SLICE = 2000;
        private const int JUMP_THRESHOLD = 1000;

        internal enum TagType : int
        {
            Default = 0,
            Audio = 8,
            Video = 9,
            Script = 18,
        }

        [Flags]
        internal enum TagFlag : int
        {
            None = 0,
            Header = 1 << 0,
            Keyframe = 1 << 1,
            End = 1 << 2,
            SameAsLastTimestamp = 1 << 3,
        }

        [DebuggerDisplay("{DebuggerDisplay,nq}")]
        internal struct FlvTag
        {
            public TagType TagType;
            public TagFlag Flag;
            public int TagSize;
            public int TimeStamp;
            public long Position;
            private string DebuggerDisplay
            {
                get
                {
                    string t;
                    switch (TagType)
                    {
                        case TagType.Audio:
                            t = "A";
                            break;
                        case TagType.Video:
                            t = "V";
                            break;
                        case TagType.Script:
                            t = "S";
                            break;
                        default:
                            t = "?";
                            break;
                    }
                    return string.Format("{0}, {1}{2}{3}{4}, TS = {5}, Size = {6}, Pos = {7}",
                        t,
                        Flag.HasFlag(TagFlag.Keyframe) ? "K" : "-",
                        Flag.HasFlag(TagFlag.Header) ? "H" : "-",
                        Flag.HasFlag(TagFlag.End) ? "E" : "-",
                        Flag.HasFlag(TagFlag.SameAsLastTimestamp) ? "L" : "-",
                        TimeStamp,
                        TagSize,
                        Position);
                }
            }
        }

        public Processor(string input_file_path)
        {
            FileInfo = new FileInfo(input_file_path);

            if (!FileInfo.Exists)
            {
                throw new Exception("source file does not exist");
            }

            try
            {
                Input = new FileStream(FileInfo.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (Input.ReadByte() != 'F' || Input.ReadByte() != 'L' || Input.ReadByte() != 'V' || Input.ReadByte() != 1)
                {
                    throw new Exception("source file is not a FLV file");
                }
                Input.ReadByte(); if (Input.ReadByte() != 0 || Input.ReadByte() != 0 || Input.ReadByte() != 0 || Input.ReadByte() != 9)
                {
                    throw new Exception("file format not supported");
                }
            }
            catch (Exception)
            {
                Input?.Dispose();
                throw;
            }
        }

        private FlvTag[] ReadAllTagsFromInput(Stream stream)
        {
            List<FlvTag> tags = new List<FlvTag>(1800);

            byte[] b = new byte[4];

            do
            {
                FlvTag tag = new FlvTag();
                // ----------------------------- //
                if (4 != stream.Read(b, 0, 4))
                {
                    break;
                }
                // ----------------------------- //
                tag.Position = stream.Position;
                tag.TagType = (TagType)stream.ReadByte();
                if (tag.TagType != TagType.Audio && tag.TagType != TagType.Video && tag.TagType != TagType.Script)
                {
                    break;
                }
                // ----------------------------- //
                b[0] = 0;
                if (3 != stream.Read(b, 1, 3))
                {
                    break;
                }
                tag.TagSize = BitConverter.ToInt32(b.ToBE(), 0);
                // ----------------------------- //
                if (3 != stream.Read(b, 1, 3))
                {
                    break;
                }
                var temp = stream.ReadByte();
                if (temp == -1)
                {
                    break;
                }
                b[0] = (byte)temp;
                tag.TimeStamp = BitConverter.ToInt32(b.ToBE(), 0);
                // ----------------------------- //
                switch (tag.TagType)
                {
                    case TagType.Audio:
                        {
                            if (4 != stream.Read(b, 0, 4)) { goto break_out; }
                            tag.Flag = stream.ReadByte() == 0 ? TagFlag.Header : TagFlag.None;
                            var totalSkip = tag.TagSize - 2;
                            if (totalSkip != stream.SkipBytes(totalSkip)) { goto break_out; }
                        }
                        break;
                    case TagType.Video:
                        {
                            if (3 != stream.Read(b, 0, 3)) { goto break_out; }
                            if (stream.ReadByte() == 0x17)
                            {
                                tag.Flag |= TagFlag.Keyframe;
                            }
                            switch (stream.ReadByte())
                            {
                                case 0:
                                    tag.Flag |= TagFlag.Header;
                                    break;
                                case 2:
                                    tag.Flag |= TagFlag.End;
                                    break;
                            }
                            var totalSkip = tag.TagSize - 2;
                            if (totalSkip != stream.SkipBytes(totalSkip)) { goto break_out; }
                        }
                        break;
                    default:
                        {
                            var totalSkip = 3 + tag.TagSize;
                            if (totalSkip != stream.SkipBytes(totalSkip))
                            {
                                goto break_out;
                            }
                        }
                        break;
                }
                // ----------------------------- //
                tags.Add(tag);
            } while (true);

        break_out:

            return tags.ToArray();
        }

        public bool DetectProblem()
        {
            bool haveProblem = false;

            // 读出所有 tag 的基本信息
            Tags = new Memory<FlvTag>(ReadAllTagsFromInput(Input));

            if (Tags.Span[0].TagType != TagType.Script)
            {
                throw new Exception("no onmetadata");
            }

            // 以 Script Tag 为准切割成不同文件
            {
                var span = Tags.Span;
                var lastIndex = 0;
                for (int i = 1; i < span.Length; i++)
                {
                    if (span[i].TagType == TagType.Script)
                    {
                        Slices.Add(Tags.Slice(lastIndex, i - lastIndex));
                        JumpTables.Add(new Dictionary<long, int>());
                        lastIndex = i;
                    }
                }
                Slices.Add(Tags.Slice(lastIndex, span.Length - lastIndex));
                JumpTables.Add(new Dictionary<long, int>());

                if (Slices.Count > 1)
                {
                    haveProblem = true;
                }
            }

            // 每段数据单独处理
            for (int z = 0; z < Slices.Count; z++)
            {
                var jumpTable = JumpTables[z];
                var slice = Slices[z];
                var span = slice.Span;

                if (slice.Length < MINIMAL_TAG_IN_SLICE)
                {
                    // 小于 2000 个 Tag 的段落不处理，正常在 30 FPS 的情况下是大约 30 秒
                    continue;
                }

                // 求出最常见tag间隔
                int videoDiff = 0, audioDiff = 0;
                {
                    int lastV = 0, lastA = 0;
                    var vd = new Dictionary<int, int>();
                    var ad = new Dictionary<int, int>();

                    foreach (var tag in span)
                    {
                        switch (tag.TagType)
                        {
                            case TagType.Audio:
                                {
                                    var diff = tag.TimeStamp - lastA;
                                    lastA = tag.TimeStamp;
                                    ad[diff] = ad.ContainsKey(diff) ? ad[diff] + 1 : 1;
                                }
                                break;
                            case TagType.Video:
                                {
                                    var diff = tag.TimeStamp - lastV;
                                    lastV = tag.TimeStamp;
                                    vd[diff] = vd.ContainsKey(diff) ? vd[diff] + 1 : 1;
                                }
                                break;
                        }
                    }
                    audioDiff = ad.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;
                    videoDiff = vd.Aggregate((x, y) => x.Value > y.Value ? x : y).Key;
                }

                // 查找跳变点并记录解决方案
                int compareTo = 0;
                bool haveAudio = false, haveVideo = false; // 是否有过非 header 数据
                for (int i = 1; i < span.Length; i++)
                {
                    var diff = Math.Abs(span[i].TimeStamp - span[compareTo].TimeStamp);
                    if (diff > JUMP_THRESHOLD)
                    {
                        haveProblem = true;

                        if (!(haveAudio || haveVideo))
                        {
                            // 如果是第一个视频或音频
                            jumpTable[span[i].Position] = span[i - 1].TimeStamp - span[i].TimeStamp;
                            goto skip_compute;
                        }

                        // 单独出现的header处理逻辑
                        if (span[i].Flag.HasFlag(TagFlag.Header))
                        {
                            // 取下一个非 header tag
                            int nextTagIndex = i + 1;
                            while (true)
                            {
                                // 如果是最后一个tag
                                if (nextTagIndex >= span.Length)
                                {
                                    span[i].Flag |= TagFlag.SameAsLastTimestamp;
                                    goto continue_for; // 忽略，不增长 compareTo
                                }

                                if (!span[nextTagIndex].Flag.HasFlag(TagFlag.Header))
                                {
                                    // 如果对比下一个tag又跳了
                                    if (Math.Abs(span[nextTagIndex].TimeStamp - span[i].TimeStamp) > JUMP_THRESHOLD)
                                    {
                                        span[i].Flag |= TagFlag.SameAsLastTimestamp;
                                        goto continue_for; // 忽略，不增长 compareTo
                                    }
                                    else
                                    {
                                        break; // while (true)
                                    }
                                }
                                else
                                {
                                    nextTagIndex++;
                                }
                            }
                        }


                        var jumpOffset = ComputeOffset(span, i, audioDiff, videoDiff);
                        jumpTable[span[i].Position] = jumpOffset;
                    }

                    if (!span[i].Flag.HasFlag(TagFlag.Header))
                    {
                        if (span[i].TagType == TagType.Audio)
                        {
                            haveAudio = true;
                        }
                        else if (span[i].TagType == TagType.Video)
                        {
                            haveVideo = true;
                        }
                    }
                skip_compute:
                    compareTo = i;
                continue_for:;
                }
            }

            return haveProblem;
        }

        private int ComputeOffset(Span<FlvTag> span, int index, int audioDiff, int videoDiff)
        {
            try
            {
                FlvTag beforeVideo = new FlvTag(), afterVideo, beforeAudio = new FlvTag(), afterAudio;
                bool findA = false, findV = false;

                if (span[index].TagType == TagType.Audio)
                {
                    afterAudio = span[index];
                    int i = 1;
                    do
                    {
                        if ((!span[index + i].Flag.HasFlag(TagFlag.SameAsLastTimestamp)) && span[index + i].TagType == TagType.Video)
                        {
                            afterVideo = span[index + i];
                            break;
                        }
                        i++;
                    } while (true);
                }
                else
                {
                    afterVideo = span[index];
                    int i = 1;
                    do
                    {
                        if ((!span[index + i].Flag.HasFlag(TagFlag.SameAsLastTimestamp)) && span[index + i].TagType == TagType.Audio)
                        {
                            afterAudio = span[index + i];
                            break;
                        }
                        i++;
                    } while (true);
                }

                {
                    int i = 1;
                    do
                    {
                        FlvTag curr = span[index - i];
                        if (!curr.Flag.HasFlag(TagFlag.SameAsLastTimestamp))
                        {
                            if (!findA && curr.TagType == TagType.Audio)
                            {
                                findA = true;
                                beforeAudio = curr;
                            }
                            else if (!findV && curr.TagType == TagType.Video)
                            {
                                findV = true;
                                beforeVideo = curr;
                            }
                        }
                        i++;
                    } while (!(findA && findV));
                }

                var vtimstamp = beforeVideo.TimeStamp + videoDiff;
                if (vtimstamp <= beforeAudio.TimeStamp)
                {
                    vtimstamp = beforeAudio.TimeStamp + 1;
                }

                var atimestamp = beforeAudio.TimeStamp + audioDiff;
                if (atimestamp <= beforeVideo.TimeStamp)
                {
                    atimestamp = beforeVideo.TimeStamp + 1;
                }

                if (atimestamp > vtimstamp)
                {
                    return atimestamp - afterAudio.TimeStamp;
                }
                else
                {
                    return vtimstamp - afterVideo.TimeStamp;
                }
            }
            catch (IndexOutOfRangeException)
            {
                Console.WriteLine("请反馈问题：计算偏移量失败，i" + index);
                return 0;
            }
        }

        public string GetProblemDescription()
        {
            var s = new StringBuilder();

            s.AppendFormat("将输出 {0} 个 flv 文件\n", Slices.Count);

            s.AppendFormat("{0,5} {1,5} {2,12} {3,12}\n", string.Empty, "序号", "预计文件大小", "开始位置");
            for (int i = 0; i < Slices.Count; i++)
            {
                s.AppendFormat("{0,-5} {1,5} {2,12:F2}MiB {3,12:D}\n", "=>", i, (Slices[i].Span[Slices[i].Length - 1].Position - Slices[i].Span[0].Position) / 1048576d, Slices[i].Span[0].Position);
                if (JumpTables[i].Count > 0)
                {
                    s.AppendFormat("{0,5} {1,-9}  {2,-12}\n", string.Empty, "时间戳偏移量", "开始位置");
                    foreach (var item in JumpTables[i])
                    {
                        s.AppendFormat("{0,-5} {1,9:D}  {2,12:D}\n", "====>", item.Value, item.Key);
                    }
                }
            }

            s.Append("\n输出文件名是 【原文件名】_fixed_【序号】.flv\n");
            return s.ToString();
        }

        private static readonly byte[] FLVSTART = new byte[] { 70, 76, 86, 1, 5, 0, 0, 0, 9, 0, 0, 0, 0 };
        public void WriteNewFile()
        {
            byte[] ZERO = new byte[10];
            for (int z = 0; z < Slices.Count; z++)
            {
                var jumpTable = JumpTables[z];
                var slice = Slices[z];
                var span = slice.Span;

                var path = Path.Combine(FileInfo.DirectoryName, Path.GetFileNameWithoutExtension(FileInfo.FullName) + "_fixed_" + z + ".flv");
                using (var write = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    write.Write(FLVSTART, 0, 13);

                    {
                        // 写 script tag
                        var script = span[0];

                        var metabytes = new byte[script.TagSize];
                        Input.Seek(script.Position + 11, SeekOrigin.Begin);
                        Input.Read(metabytes, 0, metabytes.Length);

                        var meta = new FlvMetadata(metabytes);
                        var writemetabytes = meta.ToBytes();

                        write.WriteByte((byte)script.TagType);

                        var size = BitConverter.GetBytes(writemetabytes.Length).ToBE();
                        write.Write(size, 1, 3);

                        write.Write(ZERO, 0, 3 + 1 + 3);

                        write.Write(writemetabytes, 0, writemetabytes.Length);
                        write.Write(BitConverter.GetBytes(11 + writemetabytes.Length).ToBE(), 0, 4);
                    }

                    int offset = span[1].TimeStamp;
                    int lastTimestamp = 0;

                    for (int i = 1; i < span.Length; i++)
                    {
                        Input.Seek(span[i].Position, SeekOrigin.Begin);
                        Input.CopyBytes(write, 4);
                        Input.Seek(4, SeekOrigin.Current);

                        if (!span[i].Flag.HasFlag(TagFlag.SameAsLastTimestamp))
                        {
                            if (jumpTable.ContainsKey(span[i].Position))
                            {
                                offset -= jumpTable[span[i].Position];
                            }

                            lastTimestamp = span[i].TimeStamp - offset;
                        }
                        byte[] timing = BitConverter.GetBytes(lastTimestamp).ToBE();

                        write.Write(timing, 1, 3);
                        write.WriteByte(timing[0]);

                        Input.CopyBytes(write, 3 + span[i].TagSize + 4);
                    }

                    write.Seek(49, SeekOrigin.Begin);
                    write.Write(BitConverter.GetBytes(lastTimestamp / 1000d).ToBE(), 0, sizeof(double));
                }
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // 要检测冗余调用

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    Input.Close();
                    Input.Dispose();
                    Tags = null;
                    // TODO: 释放托管状态(托管对象)。
                }

                // TODO: 释放未托管的资源(未托管的对象)并在以下内容中替代终结器。
                // TODO: 将大型字段设置为 null。

                disposedValue = true;
            }
        }

        // TODO: 仅当以上 Dispose(bool disposing) 拥有用于释放未托管资源的代码时才替代终结器。
        // ~Processor() {
        //   // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
        //   Dispose(false);
        // }

        // 添加此代码以正确实现可处置模式。
        public void Dispose()
        {
            // 请勿更改此代码。将清理代码放入以上 Dispose(bool disposing) 中。
            Dispose(true);
            // TODO: 如果在以上内容中替代了终结器，则取消注释以下行。
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
