using System;
using System.Net;
using System.Net.Http;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

// The pure parts of the registry token dance: what a challenge means, what scope a request needs, and
// whether a service account may be sent to the realm the challenge names. The last one is a security
// decision - the realm is a URL chosen by the response we are about to authenticate to - so it is worth
// pinning down here rather than only end to end.
public class RegistryTokenProviderTest
{
  private static RegistryChallenge? Parse(string wwwAuthenticate, string upstream = "https://registry.example.com/v2/library/ubuntu/manifests/24.04")
  {
    using var response = new HttpResponseMessage(HttpStatusCode.Unauthorized);
    response.Headers.TryAddWithoutValidation("WWW-Authenticate", wwwAuthenticate);
    return RegistryTokenProvider.TryParseChallenge(response, new Uri(upstream));
  }

  [Fact]
  public void Challenge_Params_Survive_The_Commas_Between_Them()
  {
    // The reason the parser is hand-written: WWW-Authenticate is a comma-separated list of challenges
    // whose auth-params are also comma-separated, so the framework parser hands back everything after
    // the first param as a separate challenge and the realm arrives alone.
    var challenge = Parse(
      "Bearer realm=\"https://auth.docker.io/token\",service=\"registry.docker.io\",scope=\"repository:library/ubuntu:pull\"");

    Assert.NotNull(challenge);
    Assert.Equal("https://auth.docker.io/token", challenge.Realm.ToString());
    Assert.Equal("registry.docker.io", challenge.Service);
    Assert.Equal("repository:library/ubuntu:pull", challenge.Scope);
  }

  [Fact]
  public void A_Challenge_Without_A_Scope_Falls_Back_To_The_Request_Path()
  {
    // Some registries challenge without naming a scope; the path says what is being pulled anyway.
    var challenge = Parse("Bearer realm=\"https://registry.example.com/token\",service=\"registry.example.com\"");

    Assert.NotNull(challenge);
    Assert.Equal("repository:library/ubuntu:pull", challenge.Scope);
    Assert.Equal("registry.example.com", challenge.Service);
  }

  [Fact]
  public void A_Stated_Scope_Beats_The_One_The_Path_Suggests()
  {
    // Where a mirror is served under a prefix, the registry may scope to the prefix and not to the image
    // inside it - Space does exactly this - and the URL gives no way to tell. So the challenge decides,
    // even when the path would have derived something perfectly plausible.
    var challenge = Parse(
      "Bearer realm=\"https://registry.jetbrains.team/token\",scope=\"repository:p/ij/docker-hub:pull\"",
      "https://registry.jetbrains.team/v2/p/ij/docker-hub/library/ubuntu/manifests/latest");

    Assert.NotNull(challenge);
    Assert.Equal("repository:p/ij/docker-hub:pull", challenge.Scope);
  }

  [Theory]
  // Not a Bearer challenge: nothing to mint, so the 401 is the upstream's answer to relay.
  [InlineData("Basic realm=\"registry\"")]
  // A token request carries a credential, so the realm must not be plaintext...
  [InlineData("Bearer realm=\"http://auth.example.com/token\",service=\"s\"")]
  // ...nor relative or opaque, where "where does this credential go" has no answer.
  [InlineData("Bearer realm=\"/token\",service=\"s\"")]
  [InlineData("Bearer service=\"s\",scope=\"repository:library/ubuntu:pull\"")]
  public void An_Unusable_Challenge_Is_No_Challenge(string wwwAuthenticate) =>
    Assert.Null(Parse(wwwAuthenticate));

  [Fact]
  public void A_Loopback_Realm_May_Be_Plaintext()
  {
    // Only so the tests can run a registry and its token endpoint over http on localhost.
    var challenge = Parse("Bearer realm=\"http://127.0.0.1:5000/token\",service=\"s\"");
    Assert.NotNull(challenge);
    Assert.Equal("http://127.0.0.1:5000/token", challenge.Realm.ToString());
  }

  [Theory]
  // The ordinary case, and the one that matters: the repository name keeps its slashes.
  [InlineData("https://registry-1.docker.io/v2/library/ubuntu/manifests/24.04", "repository:library/ubuntu:pull")]
  [InlineData("https://registry-1.docker.io/v2/library/ubuntu/blobs/sha256:abcdef0123456789abcdef0123456789", "repository:library/ubuntu:pull")]
  [InlineData("https://registry-1.docker.io/v2/library/ubuntu/tags/list", "repository:library/ubuntu:pull")]
  // A registry serving mirrors under a project path. Taking the whole path as the name is a guess, and one
  // that registry happens to disagree with - but it only ever applies where the challenge named no scope,
  // and there is nothing better to guess from.
  [InlineData("https://registry.jetbrains.team/v2/p/ij/containers/team/img/manifests/1.0", "repository:p/ij/containers/team/img:pull")]
  // A repository named after an API verb: the verb that delimits the name is the last one.
  [InlineData("https://registry.example.com/v2/manifests/manifests/latest", "repository:manifests:pull")]
  public void Fallback_Scope_Is_The_Repository_Between_v2_And_The_Api_Verb(string upstream, string expected) =>
    Assert.Equal(expected, RegistryTokenProvider.TryDeriveScope(new Uri(upstream)));

  [Theory]
  [InlineData("https://registry.example.com/v2/")]              // the ping: nothing is being pulled
  [InlineData("https://registry.example.com/v2/_catalog")]      // a registry-wide scope, which the profile redirects
  [InlineData("https://registry.example.com/v2/library/ubuntu")] // no API verb, so no repository boundary
  // /v2 anywhere but the root is not the distribution API - a misconfigured origin, whose scope we must
  // not guess at. The registry's own challenge still names one if there is anything to name.
  [InlineData("https://registry.jetbrains.team/p/ij/containers/v2/team/img/manifests/1.0")]
  public void No_Repository_Means_No_Scope(string upstream) =>
    Assert.Null(RegistryTokenProvider.TryDeriveScope(new Uri(upstream)));

  private static bool MayForward(string realm, UpstreamAuth? auth) =>
    RegistryTokenProvider.MayForwardCredentials(new Uri(realm),
      new Uri("https://registry.example.com/v2/library/ubuntu/manifests/24.04"), auth);

  private static UpstreamAuth Account(params string[] tokenRealms) => new()
  {
    UrlPrefixes = ["registry.example.com/v2/"],
    Username = "service-account",
    Password = "pat",
    TokenRealms = tokenRealms,
  };

  [Fact]
  public void A_Realm_On_The_Upstreams_Own_Host_Needs_No_Allowlist() =>
    Assert.True(MayForward("https://registry.example.com/token", Account()));

  [Fact]
  public void Another_Port_On_That_Host_Is_Another_Service()
  {
    // On a shared host the next port along may belong to someone else entirely, so "its own host" means
    // its own origin. A registry that genuinely splits the two declares the realm.
    Assert.False(MayForward("https://registry.example.com:8443/token", Account()));
    Assert.True(MayForward("https://registry.example.com:8443/token", Account("https://registry.example.com:8443/")));
  }

  [Fact]
  public void A_Foreign_Realm_Is_Refused_Unless_Declared()
  {
    // Docker Hub's realm is on auth.docker.io, a different host from registry-1.docker.io, so the account
    // has to say so. Without that, the token is still minted - just anonymously.
    Assert.False(MayForward("https://auth.docker.io/token", Account()));
    Assert.True(MayForward("https://auth.docker.io/token", Account("https://auth.docker.io/")));
  }

  [Fact]
  public void A_Host_That_Merely_Starts_With_An_Allowed_One_Is_Refused()
  {
    // Why the allowlist entries carry a trailing slash: without it "https://auth.docker.io" would also
    // admit auth.docker.io.evil.example.com, which is a credential handed to whoever registered it.
    Assert.False(MayForward("https://auth.docker.io.evil.example.com/token", Account("https://auth.docker.io/")));
    Assert.True(MayForward("https://AUTH.docker.io/token", Account("https://auth.docker.IO/")));
  }

  [Fact]
  public void An_Unauthenticated_Upstream_Forwards_Nothing_Anyway()
  {
    // No entry means no credential to forward, so the question is moot - but a same-host realm still
    // answers true, and the caller has nothing to send either way.
    Assert.True(MayForward("https://registry.example.com/token", null));
    Assert.False(MayForward("https://auth.docker.io/token", null));
  }
}
