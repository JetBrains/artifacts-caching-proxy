using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Xunit;

namespace JetBrains.CachingProxy.Tests
{
    public class CachingProxyTest: IDisposable
    {
        private readonly TestServer myServer;
        private readonly string myTempDirectory;

        public CachingProxyTest()
        {
            myTempDirectory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            Directory.CreateDirectory(myTempDirectory);

            var config = new CachingProxyConfig()
            {
                LocalCachePath = myTempDirectory,
                Prefixes = new List<string> { "/repo1.maven.org/maven2" }
            };
            
            var builder = new WebHostBuilder()
                .Configure(app =>
                    {
                        app.UseMiddleware<CachingProxy>(config);
                    }
                );

            myServer = new TestServer(builder);
        }

        [Fact]
        public async void Should_Redirect_Permanently()
        {
            var client = myServer.CreateClient();
            
            var response = await client
                .GetAsync("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar");
            var bytes = await response.Content.ReadAsByteArrayAsync();

            Console.WriteLine(response);
            // Console.WriteLine(Encoding.UTF8.GetString(bytes));
            
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(11541, bytes.Length);

            var response2 = await client
                .GetAsync("/repo1.maven.org/maven2/org/apache/ant/ant-xz/1.10.5/ant-xz-1.10.5.jar");
            var bytes2 = await response2.Content.ReadAsByteArrayAsync();

            Console.WriteLine(response2);
            // Console.WriteLine(Encoding.UTF8.GetString(bytes));
            
            Assert.Equal(HttpStatusCode.OK, response2.StatusCode);
            Assert.Equal(11541, bytes2.Length);
        }

        public void Dispose()
        {
            myServer?.Dispose();
            Directory.Delete(myTempDirectory, true);
        }
    }
}