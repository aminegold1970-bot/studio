using System;
using System.IO;
using System.Runtime.InteropServices;
using AssetStudio.PInvoke;

namespace Texture2DDecoder
{
    public static unsafe partial class TextureDecoder
    {

        static TextureDecoder()
        {
#if NETFRAMEWORK
            DllLoader.PreloadDll(T2DDll.DllName);
#elif NET6_0_OR_GREATER
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                NativeLibrary.SetDllImportResolver(
                    typeof(TextureDecoder).Assembly, ResolveWindowsNativeLibrary);
            else if (OperatingSystem.IsIOS())
                NativeLibrary.SetDllImportResolver(
                    typeof(TextureDecoder).Assembly, ResolveIOSNativeLibrary);
#endif
        }

#if NET6_0_OR_GREATER
        private static IntPtr ResolveWindowsNativeLibrary(
            string libraryName,
            System.Reflection.Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, T2DDll.DllName, StringComparison.Ordinal)
                || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return IntPtr.Zero;

            var runtimeIdentifier = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.X86 => "win-x86",
                Architecture.X64 => "win-x64",
                Architecture.Arm64 => "win-arm64",
                _ => null
            };
            if (runtimeIdentifier == null)
                return IntPtr.Zero;

            var assemblyDirectory = Path.GetDirectoryName(assembly.Location);
            var baseDirectory = string.IsNullOrEmpty(assemblyDirectory)
                ? AppContext.BaseDirectory
                : assemblyDirectory;
            var nativeLibraryPath = Path.Combine(
                baseDirectory, "runtimes", runtimeIdentifier, "native", $"{T2DDll.DllName}.dll");
            return File.Exists(nativeLibraryPath)
                ? NativeLibrary.Load(nativeLibraryPath)
                : IntPtr.Zero;
        }

        // .NET for iOS embeds dynamic frameworks under Frameworks, but it does not
        // reliably resolve a bare DllImport name to that bundle at runtime. The
        // framework is linked through NativeReference in the app project; load the
        // embedded copy explicitly so ASTC/ETC/PVRTC/BC decoders are available.
        private static IntPtr ResolveIOSNativeLibrary(
            string libraryName,
            System.Reflection.Assembly assembly,
            DllImportSearchPath? searchPath)
        {
            if (!string.Equals(libraryName, T2DDll.DllName, StringComparison.Ordinal)
                || !OperatingSystem.IsIOS())
                return IntPtr.Zero;

            var baseDirectory = AppContext.BaseDirectory;
            if (string.IsNullOrEmpty(baseDirectory))
                return IntPtr.Zero;

            var binaryName = T2DDll.DllName;
            var frameworkRelativePath =
                Path.Combine($"{binaryName}.framework", binaryName);
            var candidates = new[]
            {
                Path.Combine(baseDirectory, "Frameworks", frameworkRelativePath),
                Path.Combine(baseDirectory, frameworkRelativePath),
                Path.Combine(baseDirectory, binaryName),
                Path.Combine(baseDirectory, $"{binaryName}.dylib"),
                Path.Combine(baseDirectory, $"lib{binaryName}.dylib"),
                $"@rpath/{binaryName}.framework/{binaryName}",
                $"@executable_path/Frameworks/{binaryName}.framework/{binaryName}"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    var handle = NativeLibrary.Load(candidate, assembly, searchPath);
                    if (handle != IntPtr.Zero)
                        return handle;
                }
                catch (DllNotFoundException)
                {
                }
                catch (BadImageFormatException)
                {
                }
            }

            return IntPtr.Zero;
        }
#endif

        public static bool DecodeDXT1(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeDXT5(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeDXT5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodePVRTC(byte[] data, int width, int height, byte[] image, bool is2bpp)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodePVRTC(pData, width, height, pImage, is2bpp);
                }
            }
        }

        public static bool DecodeETC1(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A1(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A1(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeETC2A8(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeETC2A8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACR(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACR(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRSigned(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRG(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRG(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeEACRGSigned(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeEACRGSigned(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC4(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC5(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC5(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC6(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC6(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeBC7(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeBC7(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGB4(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGB4(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeATCRGBA8(byte[] data, int width, int height, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeATCRGBA8(pData, width, height, pImage);
                }
            }
        }

        public static bool DecodeASTC(byte[] data, int width, int height, int blockWidth, int blockHeight, byte[] image)
        {
            fixed (byte* pData = data)
            {
                fixed (byte* pImage = image)
                {
                    return DecodeASTC(pData, width, height, blockWidth, blockHeight, pImage);
                }
            }
        }

        public static byte[] UnpackCrunch(byte[] data)
        {
            return UnpackCrunch(data, data == null ? 0 : data.Length, false);
        }

        public static byte[] UnpackCrunch(byte[] data, int dataSize)
        {
            return UnpackCrunch(data, dataSize, false);
        }

        public static byte[] UnpackUnityCrunch(byte[] data)
        {
            return UnpackCrunch(data, data == null ? 0 : data.Length, true);
        }

        public static byte[] UnpackUnityCrunch(byte[] data, int dataSize)
        {
            return UnpackCrunch(data, dataSize, true);
        }

        private static byte[] UnpackCrunch(byte[] data, int dataSize, bool isUnityCrunch)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (dataSize < 0 || dataSize > data.Length)
                throw new ArgumentOutOfRangeException(nameof(dataSize));

            void* pBuffer = null;
            uint bufferSize = 0;
            fixed (byte* pData = data)
            {
                if (isUnityCrunch)
                    UnpackUnityCrunch(pData, (uint)dataSize, out pBuffer, out bufferSize);
                else
                    UnpackCrunch(pData, (uint)dataSize, out pBuffer, out bufferSize);
            }

            if (pBuffer == null)
                return null;

            try
            {
                if (bufferSize == 0)
                    return null;
                if (bufferSize > int.MaxValue)
                    throw new InvalidDataException("The unpacked Crunch texture exceeds the supported size.");

                var result = new byte[(int)bufferSize];
                Marshal.Copy(new IntPtr(pBuffer), result, 0, (int)bufferSize);
                return result;
            }
            finally
            {
                DisposeBuffer(ref pBuffer);
            }
        }

    }
}
