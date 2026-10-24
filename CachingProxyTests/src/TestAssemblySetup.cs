using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

public static class TestAssemblySetup
{
  // Every WebApplication.CreateBuilder() watches appsettings*.json, and the test hosts are stopped but not
  // disposed, so on Linux the watchers pile up until inotify's 128-instance limit fails whichever test is next.
  [ModuleInitializer]
  public static void DisableConfigReload() =>
    Environment.SetEnvironmentVariable("DOTNET_hostBuilder__reloadConfigOnChange", "false");
}

public class TestAssemblySetupTest
{
  [Fact]
  public void Test_Hosts_Do_Not_Watch_Their_Config_Files()
  {
    var builder = WebApplication.CreateBuilder();

    var jsonSources = ((IConfigurationBuilder)builder.Configuration).Sources.OfType<JsonConfigurationSource>().ToList();

    Assert.NotEmpty(jsonSources);
    Assert.All(jsonSources, source => Assert.False(source.ReloadOnChange, source.Path));
  }
}
