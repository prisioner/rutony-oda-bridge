using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace RutonyOda;
public sealed record Credentials(string ClientId, string ClientSecret, string? RefreshToken, string? AccessToken, DateTimeOffset Expires);

public sealed class SecretVault(string directory)
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private string PathName => Path.Combine(directory, "credentials.bin");
    private static byte[] Transform(byte[] bytes, bool encrypt)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Хранилище требует Windows DPAPI.");
        var input = new Blob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var output = new Blob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = encrypt ? CryptProtectData(ref input, "RutonyOda", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException("DPAPI: не удалось обработать реквизиты.", new Win32Exception(Marshal.GetLastWin32Error()));
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally { for (int i = 0; i < bytes.Length; i++) Marshal.WriteByte(input.Data, i, 0); Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
    public Credentials? Load()
    {
        if (!File.Exists(PathName)) return null;
        var plain = Transform(File.ReadAllBytes(PathName), false);
        try { return JsonSerializer.Deserialize<Credentials>(plain) ?? throw new InvalidDataException("Нет реквизитов."); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void Save(Credentials value)
    {
        Directory.CreateDirectory(directory); var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try { var bytes = Transform(plain, true); var temp = PathName + ".tmp"; using (var f = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(bytes); f.Flush(true); } File.Move(temp, PathName, true); } finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
