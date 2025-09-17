using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Http;

namespace JetBrains.CachingProxy
{
  public class RemoteServers
  {
    private readonly List<RemoteServer> myServers = new();

    public RemoteServers(IEnumerable<string> prefixes, ICollection<string> contentTypeValidationPrefixes)
    {
      var trimmedPrefixes = new HashSet<string>();

      // Order by length here to handle longer prefixes first
      // This will help to handle overlapping prefixes like:
      // /aprefix
      // /aprefix/too
      foreach (var prefix in prefixes.OrderByDescending(x => x.Length))
      {
        var trimmed = prefix.Trim('/');
        if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

        var index = trimmed.IndexOf('=');
        var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";
        trimmedPrefixes.Add(trimmedPrefix);

        var validateContentType = contentTypeValidationPrefixes.Contains(trimmedPrefix);
        myServers.Add(index < 0
          ? new RemoteServer(new PathString(trimmedPrefix), new Uri("https://" + trimmed + "/"), validateContentType)
          : new RemoteServer(new PathString(trimmedPrefix), new Uri(trimmed[(index + 1)..].TrimEnd('/')), validateContentType));
      }

      foreach (var contentTypeValidationPrefix in contentTypeValidationPrefixes)
      {
        if (!trimmedPrefixes.Contains(contentTypeValidationPrefix))
          throw new ArgumentException(
            $"ContentTypeValidation prefix '{contentTypeValidationPrefix}' must be present in Prefixes list");
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
      internal RemoteServer(PathString prefix, Uri remoteUri, bool validateContentTypes)
      {
        Prefix = prefix;
        RemoteUri = remoteUri;
        ValidateContentTypes = validateContentTypes;
      }

      public PathString Prefix { get; }
      public Uri RemoteUri { get; }
      public bool ValidateContentTypes { get; }

      public override string ToString() => $"{Prefix}={RemoteUri}";
    }
  }
}
