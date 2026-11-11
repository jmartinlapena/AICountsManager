using System.Security.Cryptography;
using System.Text.Json;

namespace AICountsManager;

// DPAPI wire format intentionally matches the existing session and organization archives.
internal static class ProtectedJsonStore
{
    internal static void Save<T>(string path, T data)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(data);
        try { AtomicFile.WriteBytes(path, ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    internal static T Load<T>(string path)
    {
        var encrypted = BoundedFile.Read(path, 96 * 1024 * 1024) ?? throw new FileNotFoundException();
        var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidDataException(); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}

internal static class BoundedFile
{
    internal static byte[]? Read(string path, int maximum = 16 * 1024 * 1024)
    {
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("No se pueden sustituir enlaces de sesión.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (file.Length > maximum) throw new InvalidDataException("El archivo de sesión supera el tamaño admitido.");
        var data = new byte[(int)file.Length]; file.ReadExactly(data);
        if (file.ReadByte() != -1) throw new IOException("La sesión cambió mientras se leía. Vuelve a intentarlo.");
        return data;
    }
}
