using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AssetStudio
{
    public static class BinaryReaderExtensions
    {
        public static void AlignStream(this BinaryReader reader)
        {
            reader.AlignStream(4);
        }

        public static void AlignStream(this BinaryReader reader, int alignment)
        {
            var pos = reader.BaseStream.Position;
            var mod = pos % alignment;
            if (mod != 0)
            {
                reader.BaseStream.Position += alignment - mod;
            }
        }

        public static string ReadAlignedString(this BinaryReader reader)
        {
            var length = reader.ReadInt32();
            if (length > 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position)
            {
                var stringData = reader.ReadBytes(length);
                var result = Encoding.UTF8.GetString(stringData);
                reader.AlignStream(4);
                return result;
            }
            return "";
        }

        public static string ReadStringToNull(this BinaryReader reader, int maxLength = 32767)
        {
            var bytes = new List<byte>();
            int count = 0;
            while (reader.BaseStream.Position != reader.BaseStream.Length && count < maxLength)
            {
                var b = reader.ReadByte();
                if (b == 0)
                {
                    break;
                }
                bytes.Add(b);
                count++;
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }

        public static Quaternion ReadQuaternion(this BinaryReader reader)
        {
            return new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector2 ReadVector2(this BinaryReader reader)
        {
            return new Vector2(reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector3 ReadVector3(this BinaryReader reader)
        {
            return new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Vector4 ReadVector4(this BinaryReader reader)
        {
            return new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Color ReadColor4(this BinaryReader reader)
        {
            return new Color(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        }

        public static Matrix4x4 ReadMatrix(this BinaryReader reader)
        {
            return new Matrix4x4(reader.ReadSingleArray(16));
        }

        // A malformed/corrupt object can declare a huge array count. Validate
        // against the bytes actually left in the stream before allocating, so
        // a bad shader fails with a readable error instead of OOM-killing iOS.
        private static void ValidateArrayLength(BinaryReader reader, int length, int minBytesPerElement)
        {
            if (length < 0)
                throw new InvalidDataException($"Array length {length} is negative.");

            var remaining = reader.BaseStream.Length - reader.BaseStream.Position;
            if ((long)length * minBytesPerElement > remaining)
            {
                throw new InvalidDataException(
                    $"Array length {length} exceeds the {remaining} bytes remaining in the stream.");
            }
        }

        private static T[] ReadArray<T>(BinaryReader reader, Func<T> del, int length, int minBytesPerElement = 1)
        {
            ValidateArrayLength(reader, length, minBytesPerElement);
            var array = new T[length];
            for (int i = 0; i < length; i++)
            {
                array[i] = del();
            }
            return array;
        }

        public static bool[] ReadBooleanArray(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadBoolean, reader.ReadInt32());
        }

        public static byte[] ReadUInt8Array(this BinaryReader reader)
        {
            var length = reader.ReadInt32();
            ValidateArrayLength(reader, length, 1);
            return reader.ReadBytes(length);
        }

        public static ushort[] ReadUInt16Array(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadUInt16, reader.ReadInt32(), 2);
        }

        public static int[] ReadInt32Array(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadInt32, reader.ReadInt32(), 4);
        }

        public static int[] ReadInt32Array(this BinaryReader reader, int length)
        {
            return ReadArray(reader, reader.ReadInt32, length, 4);
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadUInt32, reader.ReadInt32(), 4);
        }

        public static uint[][] ReadUInt32ArrayArray(this BinaryReader reader)
        {
            // Each inner array starts with a 4-byte count.
            return ReadArray(reader, reader.ReadUInt32Array, reader.ReadInt32(), 4);
        }

        public static uint[] ReadUInt32Array(this BinaryReader reader, int length)
        {
            return ReadArray(reader, reader.ReadUInt32, length, 4);
        }

        public static float[] ReadSingleArray(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadSingle, reader.ReadInt32(), 4);
        }

        public static float[] ReadSingleArray(this BinaryReader reader, int length)
        {
            return ReadArray(reader, reader.ReadSingle, length, 4);
        }

        public static string[] ReadStringArray(this BinaryReader reader)
        {
            // Each string starts with a 4-byte aligned length prefix.
            return ReadArray(reader, reader.ReadAlignedString, reader.ReadInt32(), 4);
        }

        public static Vector2[] ReadVector2Array(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadVector2, reader.ReadInt32(), 8);
        }

        public static Vector4[] ReadVector4Array(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadVector4, reader.ReadInt32(), 16);
        }

        public static Matrix4x4[] ReadMatrixArray(this BinaryReader reader)
        {
            return ReadArray(reader, reader.ReadMatrix, reader.ReadInt32(), 16 * sizeof(float));
        }
    }
}
