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

  [Fact]
  public void Cargo_Profile_Rules_Resolve_By_Endpoint_Kind()
  {
    // Mirrors the shipped cargo profile ordering (appsettings.json). A published .crate cannot be
    // replaced, so every download shape is eternal; the sparse index is append-only apart from
    // `yanked` and revalidates.
    var profile = CargoProfile();

    // crates.io's own download endpoint, built by cargo from config.json's `dl` with no markers.
    var download = profile.Match("/static.crates.io/crates/serde/1.0.219/download");
    Assert.NotNull(download);
    Assert.Null(download!.RefreshAfter);
    Assert.False(download.Redirect);

    // The same tarball under its legacy extension-bearing name.
    var crate = profile.Match("/static.crates.io/crates/cfg-if/cfg-if-1.0.0.crate");
    Assert.NotNull(crate);
    Assert.Null(crate!.RefreshAfter);

    // A Space registry's download shape: `dl` there carries {crate}/{version} markers, so the URL
    // ends in neither an extension nor /download.
    var spaceFile = profile.Match("/packages.jetbrains.team/crates/p/ij/crates-central-mirror/files/cfg-if/1.0.0");
    Assert.NotNull(spaceFile);
    Assert.Null(spaceFile!.RefreshAfter);

    // static.rust-lang.org: a dated snapshot and a version-pinned channel are both immutable.
    var dated = profile.Match("/static.rust-lang.org/dist/2026-08-20/rust-std-1.93.0-x86_64-unknown-linux-gnu.tar.xz");
    Assert.NotNull(dated);
    Assert.Null(dated!.RefreshAfter);

    var pinnedChannel = profile.Match("/static.rust-lang.org/dist/channel-rust-1.90.0.toml");
    Assert.NotNull(pinnedChannel);
    Assert.Null(pinnedChannel!.RefreshAfter);

    var archivedRustup = profile.Match("/static.rust-lang.org/rustup/archive/1.28.2/x86_64-unknown-linux-gnu/rustup-init");
    Assert.NotNull(archivedRustup);
    Assert.Null(archivedRustup!.RefreshAfter);

    // The sparse index and its config.json revalidate on upstream's own max-age=600.
    foreach (var indexPath in new[]
             {
               "/index.crates.io/config.json",
               "/index.crates.io/se/rd/serde",
               "/index.crates.io/1/a",
               "/packages.jetbrains.team/crates/p/ij/crates-central-mirror/se/rd/serde",
             })
    {
      var index = profile.Match(indexPath);
      Assert.NotNull(index);
      Assert.Equal(TimeSpan.FromMinutes(10), index!.RefreshAfter);
    }

    // Nothing here redirects: cargo takes the download host from config.json, so no endpoint on
    // these prefixes carries a query string that would have to ride along.
    Assert.DoesNotContain(profile.Rules, rule => rule.Redirect);
  }

  [Fact]
  public void Cargo_Eternal_Rules_Do_Not_Swallow_A_Rolling_Pointer()
  {
    // static.rust-lang.org keeps rolling pointers beside the immutable artifacts, and they are what
    // a plain `rustup update` reads. Catching one in an eternal rule pins the toolchain silently -
    // and the profile-less prefix this replaces did exactly that, serving max-age=31536000.
    var profile = CargoProfile();

    foreach (var pointer in new[]
             {
               "/static.rust-lang.org/dist/channel-rust-stable.toml",
               "/static.rust-lang.org/dist/channel-rust-stable.toml.sha256",
               "/static.rust-lang.org/dist/channel-rust-nightly.toml",
               "/static.rust-lang.org/rustup/release-stable.toml",
               "/static.rust-lang.org/rustup/dist/x86_64-unknown-linux-gnu/rustup-init",
             })
    {
      var rule = profile.Match(pointer);
      Assert.NotNull(rule);
      Assert.Equal(TimeSpan.FromMinutes(10), rule!.RefreshAfter);
    }
  }

  [Fact]
  public void Pypi_Profile_Rules_Resolve_By_Endpoint_Kind()
  {
    // Mirrors the shipped pypi profile ordering (appsettings.json). PyPI has rejected the re-upload of
    // a filename since 2018, so every distribution shape is eternal; the PEP 503/691 index is
    // append-only apart from `yanked` and revalidates on upstream's own max-age=600.
    var profile = PypiProfile();

    foreach (var filePath in new[]
             {
               // files.pythonhosted.org, modern hash-sharded layout, and the PEP 658 sidecar beside
               // it - that doubled extension is why .metadata needs a rule of its own.
               "/files.pythonhosted.org/packages/76/c6/c88e154df9c4/idna-3.10-py3-none-any.whl",
               "/files.pythonhosted.org/packages/76/c6/c88e154df9c4/idna-3.10-py3-none-any.whl.metadata",
               // Legacy layouts, still served: no hash shard, and one keyed by python version.
               "/files.pythonhosted.org/packages/source/D/Django/Django-1.0.tar.gz",
               "/files.pythonhosted.org/packages/2.7/s/six/six-1.16.0-py2.py3-none-any.whl",
               // The long tail the /packages/ segment absorbs and an extension list would not.
               "/files.pythonhosted.org/packages/a1/b2/setuptools-0.6c11-py2.6.egg",
               "/files.pythonhosted.org/packages/a1/b2/pywin32-306-cp312-win_amd64.exe",
               "/files.pythonhosted.org/packages/a1/b2/numpy-1.0.tar.bz2",
               // A Space feed serves its files off /download/{name}/{version}/, nowhere near
               // /packages/, so only the extension rules can reach them.
               "/pypi/p/ij/pypi-mirror/download/idna/3.10/idna-3.10-py3-none-any.whl",
               "/packages.jetbrains.team/pypi/p/ij/pypi-mirror/download/idna/0.2/idna-0.2.tar.gz",
             })
    {
      var file = profile.Match(filePath);
      Assert.NotNull(file);
      Assert.Null(file!.RefreshAfter);
      Assert.False(file.Redirect);
      Assert.False(file.VaryByAccept);
    }

    // The simple index revalidates, and negotiates: one URL, PEP 691 JSON or HTML by Accept, each with
    // an ETag of its own. The slash-less form is covered because a client that reaches the proxy
    // directly can ask for the bare prefix and the prefix match allows it - most of our load arrives
    // that way, over PrivateLink, rather than through the redirector, which 404s an alias with nothing
    // after it, for pypi.org exactly as it does for repo1.maven.org.
    foreach (var indexPath in new[]
             {
               "/pypi.org/simple/",
               "/pypi.org/simple/idna/",
               "/pypi.org/simple",
               "/pypi/p/ij/pypi-mirror/simple/idna/",
               "/packages.jetbrains.team/pypi/p/ij/pypi-mirror/simple",
             })
    {
      var index = profile.Match(indexPath);
      Assert.NotNull(index);
      Assert.Equal(TimeSpan.FromMinutes(10), index!.RefreshAfter);
      Assert.True(index.VaryByAccept);
    }

    // The legacy JSON API is not proxied today, but it is what the catch-all is for: a short window,
    // and nothing to negotiate on.
    var legacyJson = profile.Match("/pypi.org/pypi/idna/json");
    Assert.NotNull(legacyJson);
    Assert.Equal(TimeSpan.FromMinutes(10), legacyJson!.RefreshAfter);
    Assert.False(legacyJson.VaryByAccept);

    // Nothing here redirects, and a redirect is the only way a query string could ride along. This
    // profile also carries private Space feeds, so a client must never be bounced to an origin it
    // holds no credential for - the reason the docker catch-all is a window and not a redirect.
    Assert.DoesNotContain(profile.Rules, rule => rule.Redirect);
  }

  [Fact]
  public void Pypi_Eternal_Rules_Do_Not_Swallow_A_Simple_Index()
  {
    // The index rule is first for this reason alone, and reordering the list is what this test catches.
    // /packages/ is a mid-path segment rather than an end anchor, so it matches any alias that happens
    // to contain it - and a growing index cached forever is the one mistake there is no recovering from
    // short of a cache wipe. Keeping /simple ahead of it makes that structurally impossible instead of
    // merely unlikely: for the aliases shipped today, and for whatever a Space project is named later.
    var profile = PypiProfile();

    foreach (var indexPath in new[]
             {
               // A Space project or repository literally named `packages`. The eternal segment rule
               // does match these strings; only rule order keeps the index off an eternal entry.
               "/pypi/p/ij/packages/simple/idna/",
               "/pypi/p/packages/pypi-mirror/simple/",
               // The aliases shipped today. /packages.jetbrains.team is safe on its own - a dot, not a
               // slash - which is exactly the kind of accident worth pinning rather than re-deriving.
               "/packages.jetbrains.team/pypi/p/ij/pypi-mirror/simple/idna/",
               "/pypi.org/simple/idna/",
             })
    {
      var index = profile.Match(indexPath);
      Assert.NotNull(index);
      Assert.Equal(TimeSpan.FromMinutes(10), index!.RefreshAfter);
    }
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

  // The shipped cargo profile, kept in the same order as CachingProxy/appsettings.json.
  private static CachingProfile CargoProfile() => new()
  {
    Rules =
    [
      new CachingRule { Pattern = @"\.crate$" },
      new CachingRule { Pattern = "/crates/[^/]+/[^/]+/download$" },
      new CachingRule { Pattern = "/files/[^/]+/[^/]+$" },
      new CachingRule { Pattern = @"/dist/\d{4}-\d{2}-\d{2}/" },
      new CachingRule { Pattern = @"/dist/channel-rust-\d" },
      new CachingRule { Pattern = "/rustup/archive/" },
      new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromMinutes(10) },
    ]
  };

  // The shipped pypi profile, kept in the same order as CachingProxy/appsettings.json.
  private static CachingProfile PypiProfile() => new()
  {
    Rules =
    [
      new CachingRule { Pattern = "/simple(/|$)", RefreshAfter = TimeSpan.FromMinutes(10), VaryByAccept = true },
      new CachingRule { Pattern = "/packages/" },
      new CachingRule { Pattern = @"\.(?:whl|egg|tgz|zip|exe|msi)$" },
      new CachingRule { Pattern = @"\.tar\.(?:gz|bz2|xz|Z)$" },
      new CachingRule { Pattern = @"\.metadata$" },
      new CachingRule { Pattern = ".", RefreshAfter = TimeSpan.FromMinutes(10) },
    ]
  };
}
