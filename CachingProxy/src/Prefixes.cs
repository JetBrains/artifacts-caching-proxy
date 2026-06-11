using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace JetBrains.CachingProxy;

public class RemoteServers
{
  private readonly List<RemoteServer> myServers = [];

  public RemoteServers(CachingProxyConfig config)
  {
    // Order by length here to handle longer prefixes first
    // This will help to handle overlapping prefixes like:
    // /aprefix
    // /aprefix/too
    foreach (var prefix in config.Prefixes.OrderByDescending(x => x.Prefix.Length))
    {
      var trimmed = prefix.Prefix.Trim('/');
      if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

      var index = trimmed.IndexOf('=');
      var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";

      var target = index < 0 ? trimmed : trimmed[(index + 1)..];
      target = target.TrimEnd('/') + '/';
      myServers.Add(new RemoteServer(new PathString(trimmedPrefix),
        Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ? targetUri :
        new Uri(Uri.UriSchemeHttps + Uri.SchemeDelimiter + target, UriKind.Absolute), prefix.CacheDuration));
    }
  }

  public RemoteServer? LookupRemoteServer(PathString url, out PathString remainingPart)
  {
    foreach (var server in myServers)
    {
      if (url.StartsWithSegments(server.Prefix, StringComparison.Ordinal, out remainingPart))
        return server;
    }

    remainingPart = PathString.Empty;
    return null;
  }

  public class RemoteServer
  {
    internal RemoteServer(PathString prefix, Uri remoteUri, CacheDuration? cacheDuration)
    {
      Prefix = prefix;
      RemoteUri = remoteUri;
      CacheDuration = cacheDuration;
    }

    public PathString Prefix { get; }
    public Uri RemoteUri { get; }
    public CacheDuration? CacheDuration { get; }

    public Uri GetUpstreamUri(PathString remainingPath) => remainingPath == PathString.Empty ? RemoteUri :
      new Uri(RemoteUri, remainingPath.Value.AsSpan(remainingPath.Value?[0] == '/' ? 1 : 0).ToString());

    public string GetUpstreamUriKey(PathString remainingPath) =>
      GetUpstreamUri(remainingPath).GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped);

    public override string ToString() => $"{Prefix}={RemoteUri}";
  }
}
