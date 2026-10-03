namespace AravalsStream.Core.Facebook;

public static class FacebookIngest
{
    // Keep every credential-bearing byte out of settings.json. The documented Page Live
    // secure_stream_url normally has /rtmp/{key}; reject unfamiliar shapes.
    public static (string Server, string Key) Split(string ingest)
    {
        if (!Uri.TryCreate(ingest, UriKind.Absolute, out var uri) || uri.Scheme != "rtmps" ||
            uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            !(uri.Host.Equals("facebook.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".facebook.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Meta did not return a recognized secure RTMPS ingest URL.");
        var path = uri.AbsolutePath;
        if (!path.StartsWith("/rtmp/", StringComparison.OrdinalIgnoreCase) ||
            path.IndexOf('/', "/rtmp/".Length) >= 0 || path.Length <= "/rtmp/".Length)
            throw new InvalidOperationException("Meta's RTMPS ingest URL has an unfamiliar credential shape.");
        return (uri.GetLeftPart(UriPartial.Authority) + "/rtmp", path["/rtmp/".Length..] + uri.Query);
    }
}
