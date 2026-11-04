using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Amazon;
using Amazon.CodeArtifact;
using Amazon.CodeArtifact.Model;
using Microsoft.Extensions.Logging;
using ZiggyCreatures.Caching.Fusion;

namespace JetBrains.CachingProxy;

/// <summary>
/// The CodeArtifact domain a repository endpoint belongs to, parsed from its fixed host scheme
/// <c>&lt;domain&gt;-&lt;owner&gt;.d.codeartifact.&lt;region&gt;.amazonaws.com</c>. That host is all
/// GetAuthorizationToken needs, so the deployment configures nothing beyond the UrlPrefixes config-gen
/// already emits.
/// </summary>
public sealed partial record CodeArtifactDomain(string Name, string Owner, string Region)
{
  /// <summary>The domain of a UrlPrefix (<c>host[:port]/path</c>), or null when it is no CodeArtifact endpoint.</summary>
  public static CodeArtifactDomain? FromUrlPrefix(string urlPrefix)
  {
    var host = urlPrefix.Split('/', 2)[0].Split(':', 2)[0];
    var match = OurEndpointHost.Match(host);
    return match.Success
      ? new CodeArtifactDomain(match.Groups["domain"].Value, match.Groups["owner"].Value, match.Groups["region"].Value)
      : null;
  }

  public override string ToString() => $"{Name}-{Owner} ({Region})";

  // Domain names may hold hyphens and digits, so the greedy domain backs off to the last -<12 digits>.
  [GeneratedRegex(@"^(?<domain>[a-z0-9-]+)-(?<owner>\d{12})\.d\.codeartifact\.(?<region>[a-z0-9-]+)\.amazonaws\.com$",
    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
  private static partial Regex OurEndpointHost { get; }
}

/// <summary>The one CodeArtifact call the proxy makes, behind an interface so tests need no AWS.</summary>
public interface ICodeArtifactTokenClient
{
  Task<(string Token, DateTimeOffset ExpiresAt)> GetAuthorizationTokenAsync(CodeArtifactDomain domain, CancellationToken ct);
}

public sealed class AwsCodeArtifactTokenClient : ICodeArtifactTokenClient
{
  // The maximum. The token outlives the role session it was minted with, so this holds on ECS too.
  private static readonly TimeSpan ourTokenDuration = TimeSpan.FromHours(12);

  public async Task<(string Token, DateTimeOffset ExpiresAt)> GetAuthorizationTokenAsync(CodeArtifactDomain domain, CancellationToken ct)
  {
    // The domain's own region, whatever AWS_REGION the deployment runs with. Built per call: a token lasts
    // hours, so there is nothing to gain from keeping a client around. Credentials come from the default
    // chain; off AWS that is web identity (AWSSDK.SecurityToken).
    using var client = new AmazonCodeArtifactClient(RegionEndpoint.GetBySystemName(domain.Region));
    var response = await client.GetAuthorizationTokenAsync(new GetAuthorizationTokenRequest
    {
      Domain = domain.Name,
      DomainOwner = domain.Owner,
      DurationSeconds = (long)ourTokenDuration.TotalSeconds,
    }, ct);

    if (response.AuthorizationToken is not { Length: > 0 } token)
      throw new InvalidOperationException($"CodeArtifact domain {domain} returned an empty authorization token.");
    var expiresAt = response.Expiration is { } expiration
      ? new DateTimeOffset(DateTime.SpecifyKind(expiration, DateTimeKind.Utc))
      : DateTimeOffset.UtcNow + ourTokenDuration;
    return (token, expiresAt);
  }
}

/// <summary>
/// Mints and caches CodeArtifact authorization tokens. Memory only, like
/// <see cref="GitHubAppInstallationTokenProvider"/>: a bearer secret has no business in the shared L2.
/// FusionCache also makes the mint single-flight.
/// </summary>
public sealed class CodeArtifactTokenProvider(
  ICodeArtifactTokenClient client,
  IFusionCache cache,
  TimeProvider timeProvider,
  ILogger<CodeArtifactTokenProvider> logger)
{
  // Re-mint this long before expiry, so a fetch never goes out with a token about to die.
  private static readonly TimeSpan ourRefreshSkew = TimeSpan.FromMinutes(30);

  // Lifetime of a token that comes back already inside the refresh window.
  private static readonly TimeSpan ourFallbackDuration = TimeSpan.FromMinutes(1);

  public ValueTask<string> GetTokenAsync(CodeArtifactDomain domain, CancellationToken ct) =>
    cache.GetOrSetAsync<string>(
      $"codeartifact-token::{domain.Name}::{domain.Owner}::{domain.Region}",
      async (ctx, innerCt) =>
      {
        var (token, expiresAt) = await client.GetAuthorizationTokenAsync(domain, innerCt);
        var ttl = expiresAt - timeProvider.GetUtcNow() - ourRefreshSkew;
        ctx.Options.SetDuration(ttl > TimeSpan.Zero ? ttl : ourFallbackDuration);
        logger.LogDebug("Obtained CodeArtifact {Domain} authorization token, expires at {ExpiresAt}", domain, expiresAt);
        return token;
      },
      options: new FusionCacheEntryOptions().SetSkipDistributedCache(true, true),
      token: ct);
}
