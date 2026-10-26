using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Scimilir.Storage;

/// <summary>
/// Turns the password into a one-way "hash" for saving, so settings.json never
/// holds the password itself, and checks a typed password against it.
/// </summary>
public static class Passwords
{
    const int Iterations = 200_000;
    const string Scheme = "pbkdf2-sha256";
    public const int MinLength = 6;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{Scheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        var parts = stored?.Split('$');
        if (parts is not [Scheme, var iterText, var saltText, var hashText] || !int.TryParse(iterText, out var iterations))
            return false;
        try
        {
            var salt = Convert.FromBase64String(saltText);
            var expected = Convert.FromBase64String(hashText);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// A short fingerprint of the saved password. Logins remember it, so changing
    /// the password signs out every other phone and browser.
    /// </summary>
    public static string Stamp(string? stored) =>
        stored is null ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stored)))[..16];
}

/// <summary>
/// Slows down password guessing: after 5 wrong passwords from one address,
/// that address must wait, and the wait doubles with each further miss.
/// </summary>
public sealed class LoginThrottle(TimeProvider clock)
{
    const int FreeTries = 5;
    readonly ConcurrentDictionary<string, (int Failures, DateTimeOffset LockedUntil)> _state = new();

    public TimeSpan? WaitFor(string address)
    {
        if (!_state.TryGetValue(address, out var s)) return null;
        var left = s.LockedUntil - clock.GetUtcNow();
        return left > TimeSpan.Zero ? left : null;
    }

    public void Failed(string address) => _state.AddOrUpdate(address,
        _ => (1, DateTimeOffset.MinValue),
        (_, s) =>
        {
            var failures = s.Failures + 1;
            if (failures < FreeTries) return (failures, s.LockedUntil);
            var minutes = Math.Min(30, Math.Pow(2, failures - FreeTries));
            return (failures, clock.GetUtcNow().AddMinutes(minutes));
        });

    public void Succeeded(string address) => _state.TryRemove(address, out _);
}
