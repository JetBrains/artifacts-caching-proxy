using System;
using System.Collections.Generic;
using System.Threading.Tasks;
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
      var remoteServer = new RemoteServer(trimmedPrefix,
        Uri.TryCreate(target, UriKind.Absolute, out var targetUri) ? targetUri
          : new Uri(Uri.UriSchemeHttps + Uri.SchemeDelimiter + target, UriKind.Absolute), prefix.CacheDuration);

      // Overlapping prefixes (e.g. "/aprefix" and "/aprefix/too") both match via their {**path}
      // catch-all, and routing breaks such ties by Endpoint.Order, NOT by specificity. So order by
      // descending prefix length here, making the longer (more specific) prefix win regardless of the
      // order it was declared in. Equal-length prefixes can never match the same path, so ties between
      // them are never observed.
      endpoints[i] = new RouteEndpoint(
        requestDelegate: static _ => Task.CompletedTask,
        routePattern: RoutePatternFactory.Parse(trimmedPrefix + "/{**path}"),
        order: 0, // Yes, the same order for everything. Real order will be determined by the ASP.NET in runtime according to prefixes topology.
        metadata: new EndpointMetadataCollection(remoteServer),
        displayName: $"Metadata-only {prefix}");
    }

    Endpoints = endpoints;
  }

  public static RemoteServer? GetRemoteServer(HttpContext context, out string? path)
  {
    path = context.GetRouteValue("path")?.ToString();
    return context.GetEndpoint()?.Metadata.GetMetadata<RemoteServer>();
  }

  public record RemoteServer(PathString Prefix, Uri RemoteUri, CacheDuration? CacheDuration = null)
  {
    public Uri GetUpstreamUri(string? remainingPath) => string.IsNullOrEmpty(remainingPath) ? RemoteUri :
      new Uri(RemoteUri, remainingPath.AsSpan(remainingPath[0] == '/' ? 1 : 0).ToString());

    public string GetUpstreamUriKey(string? remainingPath) =>
      GetUpstreamUri(remainingPath).GetComponents(UriComponents.Host | UriComponents.Port | UriComponents.Path, UriFormat.UriEscaped);

    public override string ToString() => $"{Prefix}={RemoteUri}";
  }

  public override IChangeToken GetChangeToken() => NullChangeToken.Singleton;

  public override IReadOnlyList<Endpoint> Endpoints { get; }
}
