using AravalsStream.Core.Interfaces;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AravalsStream.Platform;

public sealed class MacKeychainStorage : ISecretStorage
{
    public Task StoreAsync(string key, string secret, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        RequireMac(); PlatformSecretStorage.Validate(key, secret); cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(key); using var data = new CfDictionary();
        data.Data("kSecValueData", Encoding.UTF8.GetBytes(secret));
        var status = Native.SecItemUpdate(query.Handle, data.Handle);
        if (status == -25300)
        {
            query.Data("kSecValueData", Encoding.UTF8.GetBytes(secret));
            status = Native.SecItemAdd(query.Handle, out var item);
            if (item != 0) Native.CFRelease(item);
        }
        Check(status);
    }, cancellationToken);

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        RequireMac(); PlatformSecretStorage.Validate(key); cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(key); query.Constant("kSecReturnData", Native.BooleanTrue);
        var status = Native.SecItemCopyMatching(query.Handle, out var result);
        if (status == -25300) return null;
        Check(status);
        try
        {
            var length = Native.CFDataGetLength(result);
            if (length < 0 || length > 512 * 1024 * 4) throw new IOException("Stored credential is too large.");
            var bytes = new byte[(int)length]; Marshal.Copy(Native.CFDataGetBytePtr(result), bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); } finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (result != 0) Native.CFRelease(result); }
    }, cancellationToken);

    public Task RemoveAsync(string key, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        RequireMac(); PlatformSecretStorage.Validate(key); cancellationToken.ThrowIfCancellationRequested();
        using var query = Query(key); var status = Native.SecItemDelete(query.Handle); if (status != -25300) Check(status);
    }, cancellationToken);
    private static void RequireMac() { if (!OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("macOS Keychain is required."); }
    private static void Check(int status) { if (status != 0) throw new IOException("macOS Keychain could not complete the credential operation. Unlock your keychain and authorize the application."); }
    private static CfDictionary Query(string reference)
    {
        var query = new CfDictionary(); query.Constant("kSecClass", Native.Constant("kSecClassGenericPassword"));
        query.String("kSecAttrService", "com.aravals.stream"); query.String("kSecAttrAccount", reference); return query;
    }
    private sealed class CfDictionary : IDisposable
    {
        private readonly List<nint> _owned = [];
        public nint Handle { get; } = Native.CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(Native.CoreFoundation, "kCFTypeDictionaryKeyCallBacks"), NativeLibrary.GetExport(Native.CoreFoundation, "kCFTypeDictionaryValueCallBacks"));
        public void Constant(string key, nint value) => Native.CFDictionarySetValue(Handle, Native.Constant(key), value);
        public void String(string key, string value)
        { var item = Native.CFStringCreateWithCString(0, value, 0x08000100); if (item == 0) throw new IOException("Unable to prepare Keychain request."); _owned.Add(item); Constant(key, item); }
        public void Data(string key, byte[] value)
        { try { var item = Native.CFDataCreate(0, value, value.Length); if (item == 0) throw new IOException("Unable to prepare Keychain request."); _owned.Add(item); Constant(key, item); } finally { CryptographicOperations.ZeroMemory(value); } }
        public void Dispose() { if (Handle != 0) Native.CFRelease(Handle); foreach (var item in _owned) Native.CFRelease(item); }
    }
    private static class Native
    {
        private const string Security = "/System/Library/Frameworks/Security.framework/Security";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal static readonly nint CoreFoundation = NativeLibrary.Load(Foundation);
        private static readonly nint SecurityHandle = NativeLibrary.Load(Security);
        internal static nint BooleanTrue => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreFoundation, "kCFBooleanTrue"));
        internal static nint Constant(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle, name));
        [DllImport(Security)] internal static extern int SecItemAdd(nint attributes, out nint result);
        [DllImport(Security)] internal static extern int SecItemUpdate(nint query, nint attributes);
        [DllImport(Security)] internal static extern int SecItemCopyMatching(nint query, out nint result);
        [DllImport(Security)] internal static extern int SecItemDelete(nint query);
        [DllImport(Foundation)] internal static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
        [DllImport(Foundation)] internal static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
        [DllImport(Foundation)] internal static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, uint encoding);
        [DllImport(Foundation)] internal static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
        [DllImport(Foundation)] internal static extern nint CFDataGetLength(nint data);
        [DllImport(Foundation)] internal static extern nint CFDataGetBytePtr(nint data);
        [DllImport(Foundation)] internal static extern void CFRelease(nint value);
    }
}
