using System;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public class CachingProfileTest
{
  [Fact]
  public void No_Rule_Matches_Returns_Null()
  {
    var profile = new CachingProfile
    {
      Rules = [new CachingRule { Pattern = @"maven-metadata\.xml(\..+)?$", RefreshAfter = TimeSpan.FromHours(1) }]
    };

    Assert.Null(profile.Match("/repo/org/foo/1.0/foo-1.0.jar"));
  }

  [Fact]
  public void First_Matching_Rule_Wins()
  {
    // A SNAPSHOT maven-metadata.xml matches both rules; the first one declared must win.
    var profile = new CachingProfile
    {
      Rules =
      [
        new CachingRule { Pattern = @"maven-metadata\.xml(\..+)?$", RefreshAfter = TimeSpan.FromHours(1) },
        new CachingRule { Pattern = "-SNAPSHOT", RefreshAfter = TimeSpan.FromDays(1) },
      ]
    };

    var match = profile.Match("/repo/org/foo/1.0-SNAPSHOT/maven-metadata.xml");
    Assert.NotNull(match);
    Assert.Equal(TimeSpan.FromHours(1), match!.RefreshAfter);
  }

  [Fact]
  public void Resolves_Freshness_And_Redirect_Rules()
  {
    var profile = new CachingProfile
    {
      Rules =
      [
        new CachingRule { Pattern = "-/npm/v1/security/", Redirect = true },
        new CachingRule { Pattern = "-SNAPSHOT", RefreshAfter = TimeSpan.FromDays(1) },
      ]
    };

    var redirect = profile.Match("/registry/-/npm/v1/security/audits/quick");
    Assert.NotNull(redirect);
    Assert.True(redirect!.Redirect);
    Assert.Null(redirect.RefreshAfter);

    var snapshot = profile.Match("/repo/org/foo/1.0-SNAPSHOT/foo-1.0-SNAPSHOT.jar");
    Assert.NotNull(snapshot);
    Assert.False(snapshot!.Redirect);
    Assert.Equal(TimeSpan.FromDays(1), snapshot.RefreshAfter);
  }

  [Fact]
  public void Empty_Profile_Matches_Nothing()
  {
    Assert.Null(new CachingProfile().Match("/anything"));
  }

  [Fact]
  public void Npm_Profile_Rules_Resolve_By_Endpoint_Kind()
  {
    // Mirrors the shipped npm profile ordering: security redirect, tarball eternal, catch-all freshness.
    var profile = new CachingProfile
    {
      Rules =
      [
        new CachingRule { Pattern = "-/npm/v1/security/", Redirect = true },
        new CachingRule { Pattern = @"\.tgz$" },
        new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromHours(1) },
      ]
    };

    // Tarball matches the eternal .tgz rule before the catch-all (immutable => no freshness window).
    var tarball = profile.Match("/registry.npmjs.org/express/-/express-1.0.0.tgz");
    Assert.NotNull(tarball);
    Assert.False(tarball!.Redirect);
    Assert.Null(tarball.RefreshAfter);

    // A bare packument path falls through to the catch-all => 1-hour freshness.
    var packument = profile.Match("/registry.npmjs.org/express");
    Assert.NotNull(packument);
    Assert.Equal(TimeSpan.FromHours(1), packument!.RefreshAfter);

    // The security-audit endpoint redirects (dynamic).
    var security = profile.Match("/registry.npmjs.org/-/npm/v1/security/audits/quick");
    Assert.NotNull(security);
    Assert.True(security!.Redirect);
  }

  [Fact]
  public void Docker_Profile_Rules_Resolve_By_Endpoint_Kind()
  {
    // Mirrors the shipped docker profile ordering (appsettings.json). Digest-addressed content is
    // immutable, tag-addressed content is not, and _catalog is a dynamic listing.
    var profile = DockerProfile();

    // A digest-addressed blob is content-addressed: eternal, and no negotiation (a layer is bytes).
    var blob = profile.Match("/v2/docker-hub/library/ubuntu/blobs/sha256:" + new string('a', 64));
    Assert.NotNull(blob);
    Assert.Null(blob!.RefreshAfter);
    Assert.False(blob.Redirect);
    Assert.False(blob.VaryByAccept);

    // A digest-addressed manifest is equally immutable, but still negotiated: one digest can be served
    // as more than one media type, and a client rejects a schema it did not ask for.
    var byDigest = profile.Match("/v2/docker-hub/library/ubuntu/manifests/sha256:" + new string('b', 64));
    Assert.NotNull(byDigest);
    Assert.Null(byDigest!.RefreshAfter);
    Assert.True(byDigest.VaryByAccept);

    // A tag moves, so it revalidates on a short window - and negotiates.
    var byTag = profile.Match("/v2/docker-hub/library/ubuntu/manifests/24.04");
    Assert.NotNull(byTag);
    Assert.Equal(TimeSpan.FromMinutes(5), byTag!.RefreshAfter);
    Assert.True(byTag.VaryByAccept);

    // Tag lists and referrers are mutable but not negotiated.
    var tags = profile.Match("/v2/docker-hub/library/ubuntu/tags/list");
    Assert.NotNull(tags);
    Assert.Equal(TimeSpan.FromMinutes(5), tags!.RefreshAfter);
    Assert.False(tags.VaryByAccept);

    var referrers = profile.Match("/v2/docker-hub/library/ubuntu/referrers/sha256:" + new string('c', 64));
    Assert.NotNull(referrers);
    Assert.Equal(TimeSpan.FromMinutes(5), referrers!.RefreshAfter);

    // The registry-wide listing is dynamic.
    var catalog = profile.Match("/v2/docker-hub/_catalog");
    Assert.NotNull(catalog);
    Assert.True(catalog!.Redirect);

    // An unrecognised endpoint hits the catch-all: a short window, never a redirect, so it is never
    // bounced to an origin the client has no credentials for.
    var unknown = profile.Match("/v2/docker-hub/library/ubuntu/something-new");
    Assert.NotNull(unknown);
    Assert.Equal(TimeSpan.FromMinutes(5), unknown!.RefreshAfter);
    Assert.False(unknown.Redirect);
  }

  [Fact]
  public void Docker_Digest_Rules_Reject_Malformed_Digests()
  {
    // The eternal rules must not swallow a tag that merely looks digest-ish: caching a mutable
    // reference forever is the one mistake there is no recovering from short of a cache wipe.
    var profile = DockerProfile();

    // Too short for a digest hex.
    var shortHex = profile.Match("/v2/docker-hub/library/ubuntu/manifests/sha256:abc123");
    Assert.NotNull(shortHex);
    Assert.Equal(TimeSpan.FromMinutes(5), shortHex!.RefreshAfter);

    // Trailing junk after the hex: not the whole reference, so not content-addressed.
    var trailing = profile.Match("/v2/docker-hub/library/ubuntu/manifests/sha256:" + new string('a', 64) + "-latest");
    Assert.NotNull(trailing);
    Assert.Equal(TimeSpan.FromMinutes(5), trailing!.RefreshAfter);
  }

  [Fact]
  public void Nuget_Profile_Rules_Resolve_By_Endpoint_Kind()
  {
    // Mirrors the shipped nuget profile ordering (appsettings.json). Package content under
    // {id}/{version}/ is immutable, every index document over the same feed grows.
    var profile = NugetProfile();

    // Flat-container package content: a published version cannot be replaced, so eternal.
    var nupkg = profile.Match("/api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.nupkg");
    Assert.NotNull(nupkg);
    Assert.Null(nupkg!.RefreshAfter);
    Assert.False(nupkg.Redirect);

    var nuspec = profile.Match("/api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.nuspec");
    Assert.NotNull(nuspec);
    Assert.Null(nuspec!.RefreshAfter);

    var snupkg = profile.Match("/api.nuget.org/v3-flatcontainer/newtonsoft.json/13.0.3/newtonsoft.json.13.0.3.snupkg");
    Assert.NotNull(snupkg);
    Assert.Null(snupkg!.RefreshAfter);

    // The flat-container version list gains an entry on every publish.
    var versions = profile.Match("/api.nuget.org/v3-flatcontainer/newtonsoft.json/index.json");
    Assert.NotNull(versions);
    Assert.Equal(TimeSpan.FromHours(1), versions!.RefreshAfter);

    // Registration documents carry deprecation and vulnerability metadata, which changes in place.
    var registration = profile.Match("/api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/index.json");
    Assert.NotNull(registration);
    Assert.Equal(TimeSpan.FromHours(1), registration!.RefreshAfter);

    var page = profile.Match("/api.nuget.org/v3/registration5-gz-semver2/newtonsoft.json/page/3.0.0/13.0.3.json");
    Assert.NotNull(page);
    Assert.Equal(TimeSpan.FromHours(1), page!.RefreshAfter);

    // The vulnerability index is republished as advisories land.
    var vulnerabilities = profile.Match("/api.nuget.org/v3/vulnerabilities/index.json");
    Assert.NotNull(vulnerabilities);
    Assert.Equal(TimeSpan.FromHours(1), vulnerabilities!.RefreshAfter);

    // Nothing here redirects: search and autocomplete live on hosts of their own and are handled by
    // cache-redirector, so no query-carrying endpoint reaches this prefix.
    Assert.DoesNotContain(profile.Rules, rule => rule.Redirect);
  }

  [Fact]
  public void Nuget_Eternal_Rules_Are_Anchored_At_The_Path_End()
  {
    // The extension rules must not swallow a document that merely carries a package extension mid-path:
    // caching a growing index forever is the one mistake there is no recovering from short of a cache wipe.
    var profile = NugetProfile();

    var index = profile.Match("/api.nuget.org/v3-flatcontainer/some.nupkg.tool/index.json");
    Assert.NotNull(index);
    Assert.Equal(TimeSpan.FromHours(1), index!.RefreshAfter);
  }

  // The shipped docker profile, kept in the same order as CachingProxy/appsettings.json.
  private static CachingProfile DockerProfile() => new()
  {
    Oci = true,
    Rules =
    [
      new CachingRule { Pattern = "/blobs/[a-z0-9]+(?:[+._-][a-z0-9]+)*:[0-9a-fA-F]{32,}$" },
      new CachingRule { Pattern = "/manifests/[a-z0-9]+(?:[+._-][a-z0-9]+)*:[0-9a-fA-F]{32,}$", VaryByAccept = true },
      new CachingRule { Pattern = "/manifests/", RefreshAfter = TimeSpan.FromMinutes(5), VaryByAccept = true },
      new CachingRule { Pattern = "/tags/list", RefreshAfter = TimeSpan.FromMinutes(5) },
      new CachingRule { Pattern = "/referrers/", RefreshAfter = TimeSpan.FromMinutes(5) },
      new CachingRule { Pattern = "/_catalog", Redirect = true },
      new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromMinutes(5) },
    ]
  };

  // The shipped nuget profile, kept in the same order as CachingProxy/appsettings.json.
  private static CachingProfile NugetProfile() => new()
  {
    Rules =
    [
      new CachingRule { Pattern = @"\.nupkg$" },
      new CachingRule { Pattern = @"\.snupkg$" },
      new CachingRule { Pattern = @"\.nuspec$" },
      new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromHours(1) },
    ]
  };
}
