# A caching proxy for immutable artifacts

[![JetBrains team project](http://jb.gg/badges/team.svg)](https://confluence.jetbrains.com/display/ALL/JetBrains+on+GitHub) [![Build Status](https://github.com/JetBrains/artifacts-caching-proxy/actions/workflows/dotnet.yml/badge.svg)](https://github.com/JetBrains/artifacts-caching-proxy/actions)

## Building

This is a standard .NET Core project. 

- Install [.NET Core SDK](https://www.microsoft.com/net)
- Open .sln in your favourite IDE or run `dotnet build`

We recommend [JetBrains Rider](https://www.jetbrains.com/rider)

## Running

```
dotnet run --project CachingProxy/CachingProxy.csproj -- LocalCachePath=/tmp Prefixes:0:Prefix=/plugins.gradle.org/m2 Prefixes:1:Prefix=/repo1.maven.org/maven2
```

Then try `curl -v http://127.0.0.1:5000/plugins.gradle.org/m2/de/undercouch/gradle-download-task/3.4.2/gradle-download-task-3.4.2.pom`

## Prefixes

`Prefixes:N:Prefix` is `/<path>`, served from `https://<path>/`, or `/<alias>=<upstream>` to serve another upstream
(`https://` is implied unless the upstream names its scheme).

A prefix may be a template with whole-segment `{name}` placeholders, as in an ASP.NET route:
`/github.com/{owner}/{repo}/releases/download` serves every repository's release assets, each from its own upstream
URL. The alias and the upstream must name the same placeholders, and none may be in the upstream host. A value
must be one path segment of `A-Z a-z 0-9 . _ ~ -` and not `.` or `..`; anything else is a 400. A literal prefix
inside a template's space wins over it.

A template gets the `UpstreamAuth` credential whose `UrlPrefixes` cover its literal head, the part before the first
placeholder, and is gated like any credentialed prefix. It serves whatever that credential can read under the
head, so keep the head as narrow as the credential's intended reach (`github.com/JetBrains/{repo}`, not
`github.com/{owner}/{repo}`). A request that resolves into another entry's scope is a 400. A template takes no
`DefaultNamespace` and no profile that rewrites URLs.