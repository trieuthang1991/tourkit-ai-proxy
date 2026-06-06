using System.Security.Cryptography;
using System.Text;

namespace TourkitAiProxy.Services.Security;

/// <summary>
/// AES-256/CBC encryption — PORT NGUYÊN từ TourKit.Shared/Crypton.cs (toutkit-app).
/// PHẢI giữ y hệt passPhrase/salt/IV/keySize/iterations để token mã hóa bên TourKit
/// giải mã được ở proxy này (và ngược lại). KHÔNG đổi các hằng số dưới đây.
///
/// Dùng cho: /api/v1/login-token — client gửi token = Crypton.Encrypt(JSON{username,password,domain}),
/// proxy Decrypt ra credentials rồi login TourKit.Api.
/// </summary>
public static class Crypton
{
    private const string PassPhrase = "Pas5pr@se";
    private const string SaltValue = "s@1tValue";
    private const string InitVector = "@1B2c3D4e5F6g7H8"; // 16 bytes
    private const int KeySize = 256;
    private const int Iterations = 2;

    public static string Encrypt(string plainText)
        => EncryptCore(plainText, PassPhrase, SaltValue);

    public static string Decrypt(string cipherText)
        => DecryptCore(cipherText, PassPhrase, SaltValue);

    public static string EncryptByKey(string plainText, string key)
        => EncryptCore(plainText, key + "passPhrase", key + "saltValue");

    public static string DecryptByKey(string cipherText, string key)
        => DecryptCore(cipherText, key + "passPhrase", key + "saltValue");

    private static string EncryptCore(string plainText, string passPhrase, string salt)
    {
        var ivBytes = Encoding.ASCII.GetBytes(InitVector);
        var saltBytes = Encoding.ASCII.GetBytes(salt);
        var plainBytes = Encoding.UTF8.GetBytes(plainText);

#pragma warning disable SYSLIB0041 // PasswordDeriveBytes obsolete — giữ để tương thích TourKit
        using var password = new PasswordDeriveBytes(passPhrase, saltBytes, "SHA1", Iterations);
#pragma warning restore SYSLIB0041
        var keyBytes = password.GetBytes(KeySize / 8);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor(keyBytes, ivBytes);
        using var ms = new MemoryStream();
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            cs.Write(plainBytes, 0, plainBytes.Length);

        return Convert.ToBase64String(ms.ToArray());
    }

    private static string DecryptCore(string cipherText, string passPhrase, string salt)
    {
        if (string.IsNullOrEmpty(cipherText)) return string.Empty;

        byte[] cipherBytes;
        try { cipherBytes = Convert.FromBase64String(cipherText); }
        catch { return string.Empty; }

        var ivBytes = Encoding.ASCII.GetBytes(InitVector);
        var saltBytes = Encoding.ASCII.GetBytes(salt);

#pragma warning disable SYSLIB0041
        using var password = new PasswordDeriveBytes(passPhrase, saltBytes, "SHA1", Iterations);
#pragma warning restore SYSLIB0041
        var keyBytes = password.GetBytes(KeySize / 8);

        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor(keyBytes, ivBytes);
        using var ms = new MemoryStream(cipherBytes);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        // AES-CBC + PKCS7 đọc theo block 16 bytes; CopyTo đảm bảo đọc hết block cuối (xử lý padding).
        using var output = new MemoryStream();
        cs.CopyTo(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
