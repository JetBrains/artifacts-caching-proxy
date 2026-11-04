using System;
using System.Collections.Generic;
using System.IO;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Amazon.Runtime.SharedInterfaces;
using Xunit;

namespace JetBrains.CachingProxy.Tests;

/// <summary>
/// A deployment off AWS (k8s-eqx) reaches CodeArtifact by web identity: a projected ServiceAccount token plus
/// <c>AWS_ROLE_ARN</c> / <c>AWS_WEB_IDENTITY_TOKEN_FILE</c>, picked up by the SDK's default chain. Without
/// AWSSDK.SecurityToken in the build that only fails on the first token mint, in production, so it is pinned
/// here. No STS call: the chain returns the credentials object without exchanging the token.
/// <para>Run alone: the chain reads process-wide environment variables.</para>
/// </summary>
[Collection(nameof(WebIdentityCredentialsTest))]
[CollectionDefinition(nameof(WebIdentityCredentialsTest), DisableParallelization = true)]
public class WebIdentityCredentialsTest
{
  private const string RoleArn = "arn:aws:iam::840632698608:role/codeartifact-reader-eqx";

  [Fact]
  public void The_Default_Chain_Picks_Web_Identity_From_The_Environment()
  {
    var dir = Directory.CreateTempSubdirectory("web-identity-");
    var tokenFile = Path.Combine(dir.FullName, "token");
    File.WriteAllText(tokenFile, "header.payload.signature");

    // Everything the chain consults before web identity is blanked, so a developer's own keys or profiles
    // cannot win; the config files point at paths that do not exist.
    var overrides = new Dictionary<string, string?>
    {
      ["AWS_ACCESS_KEY_ID"] = null,
      ["AWS_SECRET_ACCESS_KEY"] = null,
      ["AWS_SESSION_TOKEN"] = null,
      ["AWS_PROFILE"] = null,
      ["AWS_DEFAULT_PROFILE"] = null,
      ["AWS_CONFIG_FILE"] = Path.Combine(dir.FullName, "config"),
      ["AWS_SHARED_CREDENTIALS_FILE"] = Path.Combine(dir.FullName, "credentials"),
      ["AWS_ROLE_ARN"] = RoleArn,
      ["AWS_WEB_IDENTITY_TOKEN_FILE"] = tokenFile,
    };
    var saved = new Dictionary<string, string?>();
    foreach (var (name, value) in overrides)
    {
      saved[name] = Environment.GetEnvironmentVariable(name);
      Environment.SetEnvironmentVariable(name, value);
    }

    try
    {
      // A fresh resolver: the shared default instance (static GetCredentials) caches what it resolved first.
      var credentials = Assert.IsType<AssumeRoleWithWebIdentityCredentials>(
        new DefaultAWSCredentialsIdentityResolver().ResolveIdentity(null));
      Assert.Equal(RoleArn, credentials.RoleArn);
      Assert.Equal(tokenFile, credentials.WebIdentityTokenFile);
    }
    finally
    {
      foreach (var (name, value) in saved)
        Environment.SetEnvironmentVariable(name, value);
      dir.Delete(recursive: true);
    }
  }

  // The SDK loads STS by these names when the credentials are first used (ServiceClientHelpers).
  [Fact]
  public void The_Sts_Client_The_Sdk_Loads_Is_Deployed()
  {
    var sts = Type.GetType("Amazon.SecurityToken.AmazonSecurityTokenServiceClient, AWSSDK.SecurityToken");
    Assert.NotNull(sts);
    Assert.True(typeof(ICoreAmazonSTS).IsAssignableFrom(sts));
  }
}
