using System;
using System.IO;
using System.Runtime.InteropServices;

namespace PokeModelExporter.Formats
{
    // Oodle decompression through the player's own oo2core_*_win64.dll (not redistributable;
    // see README step 5b). Loaded from an explicit path, so any version's file name works;
    // newer Oodle versions decode data from older ones.
    public sealed class Oodle : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate long DecompressFn(byte[] comp, long compSize, byte[] raw, long rawSize,
            int fuzzSafe, int checkCrc, int verbosity, IntPtr decBufBase, long decBufSize,
            IntPtr callback, IntPtr callbackData, IntPtr decoderMemory, long decoderMemorySize, int threadPhase);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr LoadLibraryW(string path);

        [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        [DllImport("kernel32", SetLastError = true)]
        static extern bool FreeLibrary(IntPtr module);

        // Linux: the same Windows DLL through linoodle (McSimp/linoodle, MIT; our build loads it from a path given to
        // Linoodle_Load, docs/linux.md): liblinoodle.so beside this program or at $LINOODLE. Its OodleLZ_Decompress takes
        // the int arguments as 64-bit (System V: an int in a register leaves the upper half undefined).
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate long DecompressFn64(byte[] comp, long compSize, byte[] raw, long rawSize,
            long fuzzSafe, long checkCrc, long verbosity, IntPtr decBufBase, long decBufSize,
            IntPtr callback, IntPtr callbackData, IntPtr decoderMemory, long decoderMemorySize, long threadPhase);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int LoadFn(byte[] utf8Path);

        [DllImport("libdl.so.2", EntryPoint = "dlopen")]
        static extern IntPtr DlOpen(string file, int mode);

        [DllImport("libdl.so.2", EntryPoint = "dlsym")]
        static extern IntPtr DlSym(IntPtr handle, string name);

        [DllImport("libdl.so.2", EntryPoint = "dlerror")]
        static extern IntPtr DlError();

        static bool Windows => Path.DirectorySeparatorChar == '\\';
        readonly DecompressFn64 _decompress64;

        readonly IntPtr _module;
        readonly DecompressFn _decompress;

        public string DllPath { get; }

        public Oodle(string dllPath)
        {
            DllPath = dllPath;
            if (!Windows)
            {
                string lib = Environment.GetEnvironmentVariable("LINOODLE");
                if (string.IsNullOrEmpty(lib)) lib = Path.Combine(AppContext.BaseDirectory, "liblinoodle.so");
                var handle = DlOpen(lib, 2 /* RTLD_NOW */);
                if (handle == IntPtr.Zero)
                    throw new IOException($"Could not load linoodle from {lib} ({Marshal.PtrToStringAnsi(DlError())}): build it (docs/linux.md) or set LINOODLE");
                var load = DlSym(handle, "Linoodle_Load");
                var decompress = DlSym(handle, "OodleLZ_Decompress");
                if (load == IntPtr.Zero || decompress == IntPtr.Zero)
                    throw new IOException($"{lib} isn't our linoodle build (no Linoodle_Load / OodleLZ_Decompress)");
                var path = System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(dllPath) + "\0");
                if (Marshal.GetDelegateForFunctionPointer<LoadFn>(load)(path) == 0)
                    throw new IOException($"linoodle could not load {dllPath}");
                _decompress64 = Marshal.GetDelegateForFunctionPointer<DecompressFn64>(decompress);
                return;
            }
            _module = LoadLibraryW(dllPath);
            if (_module == IntPtr.Zero)
                throw new IOException($"Could not load Oodle from {dllPath} (error {Marshal.GetLastWin32Error()})");
            var fn = GetProcAddress(_module, "OodleLZ_Decompress");
            if (fn == IntPtr.Zero)
                throw new IOException($"{dllPath} has no OodleLZ_Decompress");
            _decompress = Marshal.GetDelegateForFunctionPointer<DecompressFn>(fn);
        }

        // The first oo2core_*_win64.dll in a folder, newest version first.
        public static string FindDll(string folder)
        {
            if (!Directory.Exists(folder)) return null;
            var dlls = Directory.GetFiles(folder, "oo2core_*_win64.dll");
            Array.Sort(dlls, StringComparer.OrdinalIgnoreCase);
            return dlls.Length > 0 ? dlls[dlls.Length - 1] : null;
        }

        public byte[] Decompress(byte[] compressed, int offset, int length, long rawSize)
        {
            var comp = offset == 0 && length == compressed.Length ? compressed : Slice(compressed, offset, length);
            var raw = new byte[rawSize];
            long got = _decompress64 != null
                ? _decompress64(comp, comp.Length, raw, rawSize, 1, 0, 0, IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3)
                : _decompress(comp, comp.Length, raw, rawSize, 1, 0, 0, IntPtr.Zero, 0,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, 3);
            if (got != rawSize)
                throw new InvalidDataException($"Oodle returned {got} bytes, expected {rawSize}");
            return raw;
        }

        static byte[] Slice(byte[] b, int offset, int length)
        {
            var s = new byte[length];
            Buffer.BlockCopy(b, offset, s, 0, length);
            return s;
        }

        public void Dispose()
        {
            if (_module != IntPtr.Zero) FreeLibrary(_module); // (linoodle stays loaded: one per process)
        }
    }
}
