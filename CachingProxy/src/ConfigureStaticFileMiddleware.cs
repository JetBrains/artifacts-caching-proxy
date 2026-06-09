using System.Net.Mime;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;

namespace JetBrains.CachingProxy;

public class ConfigureStaticFileMiddleware(CacheFileProvider cacheFileProvider, RemoteProxy remoteProxy) : IConfigureOptions<StaticFileOptions>
{
  void IConfigureOptions<StaticFileOptions>.Configure(StaticFileOptions options)
  {
    options.FileProvider = cacheFileProvider;
    options.ServeUnknownFileTypes = true;
    options.DefaultContentType = MediaTypeNames.Application.Octet;
    options.HttpsCompression = HttpsCompressionMode.DoNotCompress;
    options.ContentTypeProvider = new FileExtensionContentTypeProvider();
    options.OnPrepareResponse = ctx =>
    {
      var contentEncoding = cacheFileProvider.GetContentEncoding(ctx.File);
      if (contentEncoding != null)
        ctx.Context.Response.Headers.ContentEncoding = contentEncoding;

      remoteProxy.MarkStatus(ctx.Context, CachingProxyStatus.HIT);
      remoteProxy.AddEternalCachingControl(ctx.Context);
    };
  }
}
