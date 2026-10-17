using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Net.Http.Headers;

namespace SharpRail.Host.Remote;

internal static class BrowserAssets
{
    internal static void Map(WebApplication app, string root)
    {
        var files = new PhysicalFileProvider(Path.GetFullPath(root));
        app.Lifetime.ApplicationStopped.Register(files.Dispose);
        var contentTypes = new FileExtensionContentTypeProvider();
        foreach (var extension in new[] { ".dll", ".pdb", ".dat", ".blat", ".symbols" }) contentTypes.Mappings[extension] = "application/octet-stream";
        app.Use(async (context, next) =>
        {
            context.Response.Headers["Cross-Origin-Opener-Policy"] = "same-origin";
            context.Response.Headers["Cross-Origin-Embedder-Policy"] = "require-corp";
            await next(context);
        });
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
        app.Use(async (context, next) =>
        {
            var original = context.Request.Path;
            if (original.Value?.EndsWith(".br", StringComparison.OrdinalIgnoreCase) == true || original.Value?.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) == true)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            if (!(HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method)) ||
                !contentTypes.TryGetContentType(original.Value ?? "", out var contentType))
            {
                await next(context);
                return;
            }
            var source = files.GetFileInfo(original.Value!);
            if (!source.Exists || source.IsDirectory)
            {
                await next(context);
                return;
            }
            context.Response.Headers.Vary = HeaderNames.AcceptEncoding;
            var encodings = context.Request.GetTypedHeaders().AcceptEncoding;
            double Quality(string name, double fallback) => encodings?.FirstOrDefault(value => value.Value.Equals(name, StringComparison.OrdinalIgnoreCase))?.Quality ??
                (encodings?.Any(value => value.Value.Equals(name, StringComparison.OrdinalIgnoreCase)) == true ? 1 : fallback);
            var wildcard = Quality("*", 0);
            var identity = Quality("identity", wildcard == 0 && encodings?.Any(value => value.Value.Equals("*")) == true ? 0 : 1);
            var identityPreference = encodings?.Any(value => value.Value.Equals("identity", StringComparison.OrdinalIgnoreCase)) == true ? identity : 0;
            var candidates = new[] { (Name: "br", Suffix: ".br", Quality: Quality("br", wildcard)), (Name: "gzip", Suffix: ".gz", Quality: Quality("gzip", wildcard)) };
            var selected = candidates.OrderByDescending(value => value.Quality).FirstOrDefault(value =>
            {
                if (value.Quality <= 0 || value.Quality < identityPreference) return false;
                var sidecar = files.GetFileInfo(original.Value + value.Suffix);
                return sidecar.Exists && sidecar.LastModified >= source.LastModified;
            });
            if (selected.Name is null && identity <= 0)
            {
                context.Response.StatusCode = StatusCodes.Status406NotAcceptable;
                return;
            }
            if (selected.Name is not null)
            {
                context.Request.Path = original + selected.Suffix;
                context.Response.Headers.ContentEncoding = selected.Name;
                context.Response.ContentType = contentType;
            }
            try { await next(context); }
            finally { context.Request.Path = original; }
        });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = files, ContentTypeProvider = new CompressedContentTypes(contentTypes) });
    }

    private sealed class CompressedContentTypes(IContentTypeProvider inner) : IContentTypeProvider
    {
        public bool TryGetContentType(string subpath, out string contentType)
        {
            if (subpath.EndsWith(".br", StringComparison.Ordinal) || subpath.EndsWith(".gz", StringComparison.Ordinal)) subpath = subpath[..^3];
            return inner.TryGetContentType(subpath, out contentType!);
        }
    }
}