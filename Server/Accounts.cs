using System.Net.Mail;
using System.Security.Cryptography;
using HomeSafe.Shared;

namespace HomeSafe.Server;

public static class Accounts
{
    public static string PasswordHash(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(password, salt, 210_000, HashAlgorithmName.SHA512, 32);
        return Convert.ToHexString(salt) + "." + Convert.ToHexString(key);
    }

    public static bool Verify(string password, string stored)
    {
        var parts = stored.Split('.');
        byte[] expected = Convert.FromHexString(parts[1]);
        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, Convert.FromHexString(parts[0]), 210_000, HashAlgorithmName.SHA512, expected.Length);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    public static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)));

    public static string Email(string? input)
    {
        string value = input?.Trim().ToLowerInvariant() ?? "";
        if (value.Length > 254 || !MailAddress.TryCreate(value, out var mail) || mail.Address != value)
            throw new ApiError(400, "כתובת הדוא״ל אינה תקינה.");
        return value;
    }

    public static string? Phone(string? input)
    {
        string? value = input?.Trim();
        if (string.IsNullOrEmpty(value)) return null;
        if (value.Length > 25 || value.Count(char.IsAsciiDigit) < 7
            || value.Any(c => !char.IsAsciiDigit(c) && c is not ('+' or '-' or ' ' or '(' or ')')))
            throw new ApiError(400, "מספר הטלפון אינו תקין.");
        return value;
    }

    public static Session NewSession(State state, UserRow user, DateTimeOffset now)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        state.Sessions.RemoveAll(s => s.ExpiresAt <= now);
        state.Sessions.Add(new(user.Account.Id, Hash(token), now.AddDays(30)));
        return new(token, user.Account);
    }

    public static string Token(HttpContext ctx)
    {
        string header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) || header.Length != 71)
            throw new ApiError(401, "יש להתחבר מחדש.");
        return Hash(header[7..]);
    }

    public static UserRow Require(State state, string token, DateTimeOffset now, FamilyRole? role = null)
    {
        var session = state.Sessions.Find(s => s.TokenHash == token && s.ExpiresAt > now);
        var user = state.Users.Find(u => u.Account.Id == session?.UserId)
            ?? throw new ApiError(401, "יש להתחבר מחדש.");
        if (role is not null && user.Account.Role != role)
            throw new ApiError(403, "הפעולה אינה זמינה לחשבון זה.");
        return user;
    }
}
