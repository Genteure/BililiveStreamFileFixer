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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace BililiveStreamFileFixer
{
    public class FlvMetadata : IDictionary<string, object>
    {
        private IDictionary<string, object> Meta { get; set; }

        public ICollection<string> Keys => Meta.Keys;

        public ICollection<object> Values => Meta.Values;

        public int Count => Meta.Count;

        public bool IsReadOnly => false;

        public object this[string key] { get => Meta[key]; set => Meta[key] = value; }

        public FlvMetadata()
        {
            Meta = new Dictionary<string, object>
            {
                ["duration"] = 0d
            };
        }

        public FlvMetadata(byte[] data)
        {
            int readHead = 0;

            if (DecodeScriptDataValue(data, ref readHead, AMFTypes.String) as string != "onMetaData")
            {
                throw new Exception("Isn't onMetadata");
            }

            var dic = new Dictionary<string, object>();
            var raw = DecodeScriptDataValue(data, ref readHead) as Dictionary<string, object>;
            raw.Remove("");

            double dur = 0;
            if (raw.ContainsKey("duration"))
            {
                if (raw["duration"] is double raw_duration)
                {
                    dur = raw_duration;
                }
                else
                {
                    dur = 0;
                }
                raw.Remove("duration");
            }

            dic.Add("duration", dur);

            foreach (var item in raw)
            {
                if (item.Value is string text)
                {
                    dic.Add(item.Key, text.Replace("\0", ""));
                }
                else
                {
                    dic.Add(item.Key, item.Value);
                }
            }

            Meta = dic;
        }

        public byte[] ToBytes()
        {
            using (var ms = new MemoryStream())
            {
                EncodeScriptDataValue(ms, "onMetaData");
                EncodeScriptDataValue(ms, Meta);
                return ms.ToArray();
            }
        }

        #region Static

        private static void EncodeScriptDataValue(MemoryStream ms, object value)
        {
            switch (value)
            {
                case int number:
                    {
                        double asDouble = number;
                        ms.WriteByte((byte)AMFTypes.Number);
                        ms.Write(BitConverter.GetBytes(asDouble).ToBE(), 0, sizeof(double));
                        break;
                    }
                case double number:
                    {
                        ms.WriteByte((byte)AMFTypes.Number);
                        ms.Write(BitConverter.GetBytes(number).ToBE(), 0, sizeof(double));
                        break;
                    }
                case bool boolean:
                    {
                        ms.WriteByte((byte)AMFTypes.Boolean);
                        if (boolean)
                        {
                            ms.WriteByte(1);
                        }
                        else
                        {
                            ms.WriteByte(0);
                        }
                        break;
                    }
                case string text:
                    {
                        var b = Encoding.UTF8.GetBytes(text);
                        if (b.Length >= ushort.MaxValue)
                        {
                            ms.WriteByte((byte)AMFTypes.LongString);
                            ms.Write(BitConverter.GetBytes((uint)b.Length).ToBE(), 0, sizeof(uint));
                        }
                        else
                        {
                            ms.WriteByte((byte)AMFTypes.String);
                            ms.Write(BitConverter.GetBytes((ushort)b.Length).ToBE(), 0, sizeof(ushort));
                        }
                        ms.Write(b, 0, b.Length);
                        break;
                    }
                case Dictionary<string, object> d:
                    {
                        ms.WriteByte((byte)AMFTypes.Object);

                        foreach (var item in d)
                        {

                            var b = Encoding.UTF8.GetBytes(item.Key);
                            ms.Write(BitConverter.GetBytes((ushort)b.Length).ToBE(), 0, sizeof(ushort));
                            ms.Write(b, 0, b.Length);
                            EncodeScriptDataValue(ms, item.Value);
                        }

                        ms.WriteByte(0);
                        ms.WriteByte(0);
                        ms.WriteByte(9);
                        break;
                    }
                case List<object> l:
                    {
                        ms.WriteByte((byte)AMFTypes.StrictArray);
                        ms.Write(BitConverter.GetBytes((uint)l.Count), 0, sizeof(uint));
                        foreach (var item in l)
                        {
                            EncodeScriptDataValue(ms, item);
                        }
                        break;
                    }
                case DateTime dateTime:
                    {
                        ms.WriteByte((byte)AMFTypes.Date);
                        ms.Write(BitConverter.GetBytes(dateTime.ToUniversalTime().Subtract(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds).ToBE(), 0, sizeof(double));
                        ms.Write(BitConverter.GetBytes((short)0).ToBE(), 0, sizeof(short)); // UTC
                        break;
                    }
                default:
                    throw new NotSupportedException("Type " + value.GetType().FullName + " is not supported");
            }
        }

        private static object DecodeScriptDataValue(byte[] buff, ref int _readHead, AMFTypes expectType = AMFTypes.Any)
        {
            AMFTypes type = (AMFTypes)buff[_readHead++];
            if (expectType != AMFTypes.Any && expectType != type)
            {
                throw new Exception("AMF Decode type error");
            }

            switch (type)
            {
                case AMFTypes.Number:
                    {
                        const int SIZEOF_DOUBLE = sizeof(double);
                        byte[] bytes = new byte[SIZEOF_DOUBLE];
                        Buffer.BlockCopy(buff, _readHead, bytes, 0, bytes.Length);
                        _readHead += SIZEOF_DOUBLE;
                        return BitConverter.ToDouble(bytes.ToBE(), 0);
                    }
                case AMFTypes.Boolean:
                    return buff[_readHead++] != 0;
                case AMFTypes.String:
                    {
                        byte[] bytes = new byte[sizeof(ushort)];
                        bytes[0] = buff[_readHead++];
                        bytes[1] = buff[_readHead++];
                        ushort text_size = BitConverter.ToUInt16(bytes.ToBE(), 0);
                        string text = Encoding.UTF8.GetString(buff, _readHead, text_size);
                        _readHead += text_size;
                        return text;
                    }
                case AMFTypes.Object:
                    {
                        var d = new Dictionary<string, object>();
                        while (!(buff[_readHead] == 0 && buff[_readHead + 1] == 0 && buff[_readHead + 2] == 9))
                        {
                            string key;
                            {
                                byte[] bytes = new byte[sizeof(ushort)];
                                bytes[0] = buff[_readHead++];
                                bytes[1] = buff[_readHead++];
                                ushort text_size = BitConverter.ToUInt16(bytes.ToBE(), 0);
                                key = Encoding.UTF8.GetString(buff, _readHead, text_size);
                                _readHead += text_size;
                            }
                            object value = DecodeScriptDataValue(buff, ref _readHead);
                            d.Add(key, value);
                        }
                        _readHead += 3;
                        return d;
                    }
                case AMFTypes.Null:
                case AMFTypes.Undefined:
                    return null;
                case AMFTypes.ECMAArray:
                    _readHead += 4;
                    goto case AMFTypes.Object;
                case AMFTypes.StrictArray:
                    {
                        byte[] bytes = new byte[sizeof(uint)];
                        bytes[0] = buff[_readHead++];
                        bytes[1] = buff[_readHead++];
                        bytes[2] = buff[_readHead++];
                        bytes[3] = buff[_readHead++];
                        uint array_size = BitConverter.ToUInt32(bytes.ToBE(), 0);

                        var d = new List<object>();
                        while (d.Count < array_size)
                        {
                            d.Add(DecodeScriptDataValue(buff, ref _readHead));
                        }
                        return d;
                    }
                case AMFTypes.Date:
                    {
                        const int SIZEOF_DOUBLE = sizeof(double);
                        const int SIZEOF_SI16 = sizeof(short);

                        byte[] datetime_bytes = new byte[SIZEOF_DOUBLE];
                        Buffer.BlockCopy(buff, _readHead, datetime_bytes, 0, datetime_bytes.Length);
                        _readHead += SIZEOF_DOUBLE;
                        var datetime = BitConverter.ToDouble(datetime_bytes.ToBE(), 0);

                        byte[] offset_bytes = new byte[SIZEOF_SI16];
                        Buffer.BlockCopy(buff, _readHead, offset_bytes, 0, offset_bytes.Length);
                        _readHead += SIZEOF_SI16;
                        var offset = BitConverter.ToInt16(offset_bytes.ToBE(), 0);

                        return new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(datetime).AddMinutes(-offset);
                    }
                case AMFTypes.LongString:
                    unchecked
                    {
                        byte[] bytes = new byte[sizeof(uint)];
                        bytes[0] = buff[_readHead++];
                        bytes[1] = buff[_readHead++];
                        bytes[2] = buff[_readHead++];
                        bytes[3] = buff[_readHead++];
                        int text_size = (int)BitConverter.ToUInt32(bytes.ToBE(), 0);
                        if (text_size <= 0)
                        {
                            throw new Exception("LongString longer than " + int.MaxValue + " is not supported");
                        }
                        string text = Encoding.UTF8.GetString(buff, _readHead, text_size);
                        _readHead += text_size;
                        return text;
                    }
                default:
                case AMFTypes.MovieClip:
                case AMFTypes.Reference:
                case AMFTypes.ObjectEndMarker:
                    throw new NotSupportedException("AMF type not supported");
            }
        }

        #endregion

        public void Add(string key, object value)
        {
            Meta.Add(key, value);
        }

        public bool ContainsKey(string key)
        {
            return Meta.ContainsKey(key);
        }

        public bool Remove(string key)
        {
            return Meta.Remove(key);
        }

        public bool TryGetValue(string key, out object value)
        {
            return Meta.TryGetValue(key, out value);
        }

        public void Add(KeyValuePair<string, object> item)
        {
            Meta.Add(item);
        }

        public void Clear()
        {
            Meta.Clear();
        }

        public bool Contains(KeyValuePair<string, object> item)
        {
            return Meta.Contains(item);
        }

        public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex)
        {
            Meta.CopyTo(array, arrayIndex);
        }

        public bool Remove(KeyValuePair<string, object> item)
        {
            return Meta.Remove(item);
        }

        public IEnumerator<KeyValuePair<string, object>> GetEnumerator()
        {
            return Meta.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return Meta.GetEnumerator();
        }

        public enum AMFTypes : byte
        {
            /// <summary>
            /// 非标准类型。在 Decode 过程中作为函数参数使用
            /// </summary>
            Any = 0xFF,
            /// <summary>
            /// Double
            /// </summary>
            Number = 0,
            /// <summary>
            /// 8 bit unsigned integer
            /// </summary>
            Boolean = 1,
            /// <summary>
            /// ScriptDataString
            /// </summary>
            String = 2,
            /// <summary>
            /// ScriptDataObject
            /// </summary>
            Object = 3,
            /// <summary>
            /// Not Supported
            /// </summary>
            MovieClip = 4,
            /// <summary>
            /// Nothing
            /// </summary>
            Null = 5,
            /// <summary>
            /// Nothing
            /// </summary>
            Undefined = 6,
            /// <summary>
            /// Not Supported
            /// </summary>
            Reference = 7,
            /// <summary>
            /// ScriptDataEcmaArray
            /// </summary>
            ECMAArray = 8,
            /// <summary>
            /// Nothing
            /// </summary>
            ObjectEndMarker = 9,
            /// <summary>
            /// ScriptDataStrictArray
            /// </summary>
            StrictArray = 10,
            /// <summary>
            /// ScriptDataDate
            /// </summary>
            Date = 11,
            /// <summary>
            /// ScriptDataLongString
            /// </summary>
            LongString = 12
        }
    }
}
