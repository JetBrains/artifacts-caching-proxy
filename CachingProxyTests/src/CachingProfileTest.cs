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
}
