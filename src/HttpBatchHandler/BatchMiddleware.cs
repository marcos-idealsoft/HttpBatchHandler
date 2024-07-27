﻿using HttpBatchHandler.Events;
using HttpBatchHandler.Multipart;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Features.Authentication;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace HttpBatchHandler
{
    public class BatchMiddleware
    {
        private readonly IHttpContextFactory _factory;
        private readonly RequestDelegate _next;
        private readonly BatchMiddlewareOptions _options;

        public BatchMiddleware(RequestDelegate next, IHttpContextFactory factory, BatchMiddlewareOptions options)
        {
            _next = next;
            _factory = factory;
            _options = options;
        }


        public Task Invoke(HttpContext httpContext)
        {
            if (!httpContext.Request.Path.Equals(_options.Match))
            {
                return _next.Invoke(httpContext);
            }

            return InvokeBatchAsync(httpContext);
        }

        private FeatureCollection CreateDefaultFeatures(IFeatureCollection input)
        {
            var output = new FeatureCollection();
            output.Set(input.Get<IServiceProvidersFeature>());
            output.Set(input.Get<IHttpRequestIdentifierFeature>());
            output.Set(input.Get<IAuthenticationFeature>());
            output.Set(input.Get<IHttpAuthenticationFeature>());
            output.Set<IItemsFeature>(new ItemsFeature()); // per request?
            return output;
        }

        private async Task InvokeBatchAsync(HttpContext httpContext)
        {
            if (!httpContext.Request.IsMultiPartBatchRequest())
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync("Invalid Content-Type.").ConfigureAwait(false);
                return;
            }

            var boundary = httpContext.Request.GetMultipartBoundary();
            if (string.IsNullOrEmpty(boundary))
            {
                httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                await httpContext.Response.WriteAsync("Invalid boundary in Content-Type.").ConfigureAwait(false);
                return;
            }

            var startContext = new BatchStartContext
            {
                Request = httpContext.Request
            };
            var cancellationToken = httpContext.RequestAborted;
            await _options.Events.BatchStartAsync(startContext, cancellationToken).ConfigureAwait(false);
            Exception exception = null;
            var abort = false;
            var reader = new MultipartReader(boundary, httpContext.Request.Body) { BodyLengthLimit = long.MaxValue, HeadersCountLimit = 32, HeadersLengthLimit = 1024 * 32 };
            // PathString.StartsWithSegments that we use requires the base path to not end in a slash.
            var pathBase = httpContext.Request.PathBase;
            if (pathBase.HasValue && pathBase.Value.EndsWith("/"))
            {
                pathBase = new PathString(pathBase.Value.Substring(0, pathBase.Value.Length - 1));
            }

            using (var writer = new MultipartWriter("batch", Guid.NewGuid().ToString()))
            {
                try
                {
                    HttpApplicationRequestSection section;

                    List<Task<HttpApplicationMultipart>> tasks = new();

                    while ((section = await reader
                               .ReadNextHttpApplicationRequestSectionAsync(pathBase, httpContext.Request.IsHttps, cancellationToken)
                               .ConfigureAwait(false)) != null)
                    {
                        writer.Add(await ProcessSection(httpContext, section, startContext, cancellationToken));
                    }
                }
                catch (Exception ex)
                {
                    exception = ex;
                }
                finally
                {
                    var endContext = new BatchEndContext
                    {
                        Exception = exception,
                        State = startContext.State,
                        IsAborted = abort,
                        Response = httpContext.Response
                    };
                    if (endContext.Exception != null)
                    {
                        endContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    }

                    await _options.Events.BatchEndAsync(endContext, cancellationToken).ConfigureAwait(false);

                    if (!endContext.IsHandled)
                    {
                        httpContext.Response.Headers.Add(HeaderNames.ContentType, writer.ContentType);
                        await writer.CopyToAsync(httpContext.Response.Body, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }

        private async Task<HttpApplicationMultipart> ProcessSection(HttpContext httpContext, HttpApplicationRequestSection section, BatchStartContext startContext, CancellationToken cancellationToken)
        {
            httpContext.RequestAborted.ThrowIfCancellationRequested();

            var preparationContext = new BatchRequestPreparationContext
            {
                RequestFeature = section.RequestFeature,
                Features = CreateDefaultFeatures(httpContext.Features),
                State = startContext.State
            };
            await _options.Events.BatchRequestPreparationAsync(preparationContext, cancellationToken)
                .ConfigureAwait(false);
            using (var state =
                new RequestState(section.RequestFeature, _factory, preparationContext.Features))
            {
                using (httpContext.RequestAborted.Register(state.AbortRequest))
                {
                    var executedContext = new BatchRequestExecutedContext
                    {
                        Request = state.Context.Request,
                        State = startContext.State
                    };
                    try
                    {
                        var executingContext = new BatchRequestExecutingContext
                        {
                            Request = state.Context.Request,
                            State = startContext.State
                        };
                        await _options.Events
                            .BatchRequestExecutingAsync(executingContext, cancellationToken)
                            .ConfigureAwait(false);
                        await _next.Invoke(state.Context).ConfigureAwait(false);
                        var response = await state.ResponseTaskAsync().ConfigureAwait(false);
                        executedContext.Response = state.Context.Response;
                        return new HttpApplicationMultipart(response);
                    }
                    catch (Exception ex)
                    {
                        state.Abort(ex);
                        executedContext.Exception = ex;
                        throw;
                    }
                    finally
                    {
                        await _options.Events.BatchRequestExecutedAsync(executedContext, cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }
        }
    }
}