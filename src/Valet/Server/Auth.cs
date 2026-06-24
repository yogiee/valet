using System.Net;

namespace Valet.Server;

internal sealed class Auth
{
    private readonly string _token;
    private readonly IPNetwork _allowed;

    public Auth(string token, string allowedCidr)
    {
        _token = token ?? throw new ArgumentNullException(nameof(token));
        _allowed = IPNetwork.Parse(allowedCidr);
    }

    public bool IsAllowedFrom(IPAddress remote)
    {
        if (remote.IsIPv4MappedToIPv6) remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote)) return true;
        return _allowed.Contains(remote);
    }

    // Returns: (token-valid?, normalised path with /<token> prefix stripped).
    public (bool Valid, string Path) CheckToken(HttpListenerRequest req)
    {
        var path = req.Url?.AbsolutePath ?? "/";

        var header = req.Headers["X-Auth-Token"];
        if (!string.IsNullOrEmpty(header))
        {
            return (string.Equals(header, _token, StringComparison.Ordinal), path);
        }

        var prefix = "/" + _token;
        if (path == prefix || path == prefix + "/")
        {
            return (true, "/");
        }
        if (path.StartsWith(prefix + "/", StringComparison.Ordinal))
        {
            return (true, path.Substring(prefix.Length));
        }

        return (false, path);
    }
}
