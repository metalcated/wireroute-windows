using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace WireRoute.Storage;

[SupportedOSPlatform("windows")]
internal static class WindowsDpapi
{
    private const uint CryptProtectUiForbidden = 0x1;
    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("WireRoute.RouterOS.Storage.v1");

    public static byte[] Protect(ReadOnlySpan<byte> plaintext) => Transform(plaintext, protect: true);

    public static byte[] Unprotect(ReadOnlySpan<byte> ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        var inputBlob = DataBlob.Create(input);
        var entropyBlob = DataBlob.Create(OptionalEntropy);
        var outputBlob = default(DataBlob);
        var description = IntPtr.Zero;
        try
        {
            var succeeded = protect
                ? CryptProtectData(
                    ref inputBlob,
                    null,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob)
                : CryptUnprotectData(
                    ref inputBlob,
                    out description,
                    ref entropyBlob,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    CryptProtectUiForbidden,
                    out outputBlob);
            if (!succeeded)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            if (outputBlob.Size <= 0 || outputBlob.Data == IntPtr.Zero)
            {
                throw new CryptographicException("Windows DPAPI returned an empty value.");
            }

            var output = new byte[outputBlob.Size];
            Marshal.Copy(outputBlob.Data, output, 0, output.Length);
            return output;
        }
        finally
        {
            if (description != IntPtr.Zero)
            {
                LocalFree(description);
            }

            if (outputBlob.Data != IntPtr.Zero)
            {
                ZeroAndLocalFree(outputBlob);
            }

            entropyBlob.Dispose();
            inputBlob.Dispose();
        }
    }

    private static void ZeroAndLocalFree(DataBlob blob)
    {
        if (blob.Size > 0)
        {
            Marshal.Copy(new byte[blob.Size], 0, blob.Data, blob.Size);
        }

        LocalFree(blob.Data);
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn,
        string? dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn,
        out IntPtr dataDescription,
        ref DataBlob optionalEntropy,
        IntPtr reserved,
        IntPtr promptStruct,
        uint flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob : IDisposable
    {
        public int Size;
        public IntPtr Data;

        public static DataBlob Create(ReadOnlySpan<byte> value)
        {
            if (value.IsEmpty)
            {
                return default;
            }

            var bytes = value.ToArray();
            var data = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, data, bytes.Length);
                return new DataBlob { Size = bytes.Length, Data = data };
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }

        public void Dispose()
        {
            if (Data == IntPtr.Zero)
            {
                return;
            }

            if (Size > 0)
            {
                Marshal.Copy(new byte[Size], 0, Data, Size);
            }

            Marshal.FreeHGlobal(Data);
            Data = IntPtr.Zero;
            Size = 0;
        }
    }
}
