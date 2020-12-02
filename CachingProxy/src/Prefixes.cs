using System;
using System.Collections.Generic;
using System.Linq;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Http;

namespace JetBrains.CachingProxy
{
  public class RemoteServers
  {
    private readonly List<RemoteServer> myServers = new List<RemoteServer>();

    public RemoteServers(IEnumerable<string> prefixes)
    {
      // Order by length here to handle longer prefixes first
      // This will help to handle overlapping prefixes like:
      // /aprefix
      // /aprefix/too
      foreach (var prefix in prefixes.OrderByDescending(x => x.Length))
      {
        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

        var index = trimmed.IndexOf("=", StringComparison.Ordinal);
        myServers.Add(index < 0
          ? new RemoteServer(new PathString("/" + trimmed), new Uri("https://" + trimmed + "/"))
          : new RemoteServer(new PathString("/" + trimmed.Substring(0, index)), new Uri(trimmed.Substring(index + 1).TrimEnd('/'))));
      }
    }

    [CanBeNull]
    public RemoteServer LookupRemoteServer(PathString url, out PathString remainingPart)
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
      internal RemoteServer(PathString prefix, Uri remoteUri)
      {
        Prefix = prefix;
        RemoteUri = remoteUri;
      }

      public PathString Prefix { get; }
      public Uri RemoteUri { get; }

      public override string ToString() => $"{Prefix}={RemoteUri}";
    }
  }
}
