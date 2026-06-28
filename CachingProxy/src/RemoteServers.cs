using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;

namespace JetBrains.CachingProxy;

public class RemoteServers : EndpointDataSource
{
  public RemoteServers(CachingProxyConfig config)
  {
    var endpoints = new Endpoint[config.Prefixes.Length];
    for (var i = 0; i < config.Prefixes.Length; i++)
    {
      var prefix = config.Prefixes[i];
      var trimmed = prefix.Prefix.Trim('/');
      if (trimmed.Length == 0) throw new ArgumentException("Prefix is empty: " + prefix);

      var index = trimmed.IndexOf('=');
      var trimmedPrefix = index < 0 ? $"/{trimmed}" : $"/{trimmed[..index]}";

      var target = index < 0 ? trimmed : trimmed[(index + 1)..];
      target = target.TrimEnd('/') + '/';
      var remoteUri = Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ? targetUri :
        new Uri(Uri.UriSchemeHttps + Uri.SchemeDelimiter + target, UriKind.Absolute);
      var remoteServer = new RemoteServer(trimmedPrefix, remoteUri,
        config.CacheDuration.Union(prefix.CacheDuration), MatchAuth(remoteUri, config.UpstreamAuth.Values));

      // A prefix with a matched UpstreamAuth serves proxy-fetched private artifacts, so its inbound route
      // must require a validated client JWT too: attach an AuthorizeAttribute (enforced by
      // UseAuthentication/UseAuthorization). A prefix with no matched auth has no upstream credentials and
      // is left public/un-gated.
      var metadata = remoteServer.Auth is not null ?
        new EndpointMetadataCollection(remoteServer, new AuthorizeAttribute()) :
        new EndpointMetadataCollection(remoteServer);

      // Overlapping prefixes (e.g. "/aprefix" and "/aprefix/too") both match via their {**path}
      // catch-all, and routing breaks such ties by Endpoint.Order, NOT by specificity. So order by
      // descending prefix length here, making the longer (more specific) prefix win regardless of the
      // order it was declared in. Equal-length prefixes can never match the same path, so ties between
      // them are never observed.
      endpoints[i] = new RouteEndpoint(
        requestDelegate: static _ => Task.CompletedTask,
        routePattern: RoutePatternFactory.Parse(trimmedPrefix + $"/{{**{PathParameterName}}}"),
        order: 0, // Yes, the same order for everything. Real order will be determined by the ASP.NET in runtime according to prefixes topology.
        metadata: metadata,
        displayName: $"Metadata-only {prefix}");
    }

    Endpoints = endpoints;
  }

  private const string PathParameterName = "path";

  // Among the auth entries whose UrlPrefixes contain a prefix of the upstream URL, the longest (most
  // specific) one wins, so a host-wide block and a path-scoped block can coexist. Returns null when
  // nothing matches, leaving the upstream unauthenticated.
  private static UpstreamAuth? MatchAuth(Uri remoteUri, IReadOnlyCollection<UpstreamAuth> auths)
  {
    var remoteUriAbsoluteUri = remoteUri.AbsoluteUri;
    return auths.
      SelectMany(auth => auth.UrlPrefixes.Select(prefix => KeyValuePair.Create(prefix, auth)))
      .Where(kv => remoteUriAbsoluteUri.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
      .OrderByDescending(kv => kv.Key.Length)
      .Select(kv => kv.Value)
      .FirstOrDefault();
  }

  public static RemoteServer? GetRemoteServer(HttpContext context, out string? path)
  {
    path = context.GetRouteValue(PathParameterName)?.ToString();
    return context.GetEndpoint()?.Metadata.GetMetadata<RemoteServer>();
  }

  public record RemoteServer(PathString Prefix, Uri RemoteUri, CacheDuration CacheDuration, UpstreamAuth? Auth = null)
  {
    public Uri GetUpstreamUri(string? remainingPath) =>
      string.IsNullOrEmpty(remainingPath) ? RemoteUri : new Uri(RemoteUri, remainingPath);

    public override string ToString() => $"{Prefix}={RemoteUri}";
  }

  public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

  public override IReadOnlyList<Endpoint> Endpoints { get; }
}
