using Microsoft.AspNetCore.Http;

namespace RazorDbManager;

internal sealed class RazorDbNoStoreFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store, max-age=0";
        context.HttpContext.Response.Headers.Pragma = "no-cache";
        context.HttpContext.Response.Headers.Expires = "0";
        return await next(context);
    }
}
