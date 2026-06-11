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

      // Serve the original upstream Content-Type when we stored one; otherwise leave the
      // extension-derived value the static-file middleware already set.
      var storedContentType = cacheFileProvider.GetStoredContentType(ctx.File);
      if (storedContentType != null)
        ctx.Context.Response.ContentType = storedContentType;

      remoteProxy.SetStatusHeader(ctx.Context, CachingProxyStatus.HIT);
      ctx.Context.Response.Headers.CacheControl = RemoteProxy.OurEternalCachingHeader;
    };
  }
}
