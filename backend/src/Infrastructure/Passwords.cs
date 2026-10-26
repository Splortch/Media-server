using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace MediaServer.Infrastructure;

public static class Passwords
{
    private static async Task<byte[]> Derive(string password, byte[] salt)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = 1,
            MemorySize = 65536,
            Iterations = 3,
        };
        return await argon.GetBytesAsync(32);
    }

    public static async Task<string> Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return $"argon2id$v=19$m=65536,t=3,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(await Derive(password, salt))}";
    }

    public static async Task<bool> Verify(string password, string hash)
    {
        var p = hash.Split('$');
        return p.Length == 5
            && p[0] == "argon2id"
            && CryptographicOperations.FixedTimeEquals(
                await Derive(password, Convert.FromBase64String(p[3])),
                Convert.FromBase64String(p[4])
            );
    }
}
