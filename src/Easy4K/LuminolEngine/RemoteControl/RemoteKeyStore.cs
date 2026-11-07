using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;

namespace Easy4K.LuminolEngine.RemoteControl;

/// <summary>远程链接安全密钥：用当前用户的 DPAPI 加密后存进 appsettings.json。
/// 只有本机同一 Windows 用户能解密显示；忘记密钥时无法恢复，只能重装/清除配置。</summary>
public static class RemoteKeyStore
{
    /// <summary>密钥最短长度</summary>
    public const int MinLength = 4;

    public static async Task<string> ProtectAsync(string key)
    {
        var provider = new DataProtectionProvider("LOCAL=user");
        var buffer = CryptographicBuffer.ConvertStringToBinary(key, BinaryStringEncoding.Utf8);
        var protectedBuffer = await provider.ProtectAsync(buffer);
        return CryptographicBuffer.EncodeToBase64String(protectedBuffer);
    }

    /// <summary>解密失败（换用户/换机器/配置被改）返回空串。</summary>
    public static async Task<string> UnprotectAsync(string protectedBase64)
    {
        try
        {
            var provider = new DataProtectionProvider("LOCAL=user");
            var buffer = CryptographicBuffer.DecodeFromBase64String(protectedBase64);
            var data = await provider.UnprotectAsync(buffer);
            return CryptographicBuffer.ConvertBinaryToString(BinaryStringEncoding.Utf8, data);
        }
        catch
        {
            return "";
        }
    }
}
