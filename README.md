# A caching proxy for immutable artifacts

[![JetBrains team project](http://jb.gg/badges/team.svg)](https://confluence.jetbrains.com/display/ALL/JetBrains+on+GitHub) [![Build Status](https://github.com/JetBrains/artifacts-caching-proxy/actions/workflows/dotnet.yml/badge.svg)](https://github.com/JetBrains/artifacts-caching-proxy/actions)

## Building

This is a standard .NET Core project. 

- Install [.NET Core SDK](https://www.microsoft.com/net)
- Open .sln in your favourite IDE or run `dotnet build`

We recommend [JetBrains Rider](https://www.jetbrains.com/rider)

## Running

```
dotnet run --project CachingProxy/CachingProxy.csproj -- LocalCachePath=/tmp Prefixes:0=/plugins.gradle.org/m2 Prefixes:1=/repo1.maven.org/maven2
```

Then try `curl -v http://127.0.0.1:5000/plugins.gradle.org/m2/de/undercouch/gradle-download-task/3.4.2/gradle-download-task-3.4.2.pom`