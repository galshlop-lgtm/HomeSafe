namespace HomeSafe.Shared;

public static class ServerAddress
{
    public static string? Normalize(string address)
    {
        if (!Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host.Length == 0 || uri.Query.Length != 0
            || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0)
            return null;
        return uri.AbsoluteUri.TrimEnd('/');
    }

    public static bool IsTrusted(string endpoint, string approved) =>
        Normalize(endpoint) is {} canonical && approved
            .Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => string.Equals(Normalize(value), canonical, StringComparison.Ordinal));
}
