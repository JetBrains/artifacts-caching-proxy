using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace JetBrains.CachingProxy
{
  public class RemoteServers
  {
    private readonly List<RemoteServer> myServers = new();

    public RemoteServers(IReadOnlyCollection<CachingProxyPrefix> prefixes)
    {
      // Order by length here to handle longer prefixes first
      // This will help to handle overlapping prefixes like:
      // /aprefix
      // /aprefix/too
      foreach (var prefix in prefixes.OrderByDescending(x => x.Prefix.Length))
      {
        var trimmed = prefix.Prefix.Trim('/');
        if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

        var index = trimmed.IndexOf('=');
        var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";

        myServers.Add(index < 0
          ? new RemoteServer(new PathString(trimmedPrefix), new Uri("https://" + trimmed + "/"), prefix.CacheDuration)
          : new RemoteServer(new PathString(trimmedPrefix), new Uri(trimmed[(index + 1)..].TrimEnd('/')), prefix.CacheDuration));
      }
    }

    public RemoteServer? LookupRemoteServer(PathString url, out PathString remainingPart)
    {
      foreach (var server in myServers)
      {
        if (url.StartsWithSegments(server.Prefix, StringComparison.Ordinal, out remainingPart))
          return server;
      }

      remainingPart = null;
      return null;
    }

    public IReadOnlyList<RemoteServer> Servers => myServers;

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

      public override string ToString() => $"{Prefix}={RemoteUri}";
    }
  }
}
