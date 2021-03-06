// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Specialized;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace HttpStress
{
    public class StressServer : IDisposable
    {
        // Header indicating expected response content length to be returned by the server
        public const string ExpectedResponseContentLength = "Expected-Response-Content-Length";

        private EventListener? _eventListener;
        private readonly IWebHost _webHost;

        public string ServerUri { get; }

        public StressServer(Configuration configuration)
        {
            ServerUri = configuration.ServerUri;
            (string scheme, string hostname, int port) = ParseServerUri(configuration.ServerUri);
            IWebHostBuilder host = new WebHostBuilder();

            host = host.ConfigureServices(service => {
                service.AddRouting();
            })

            // Use Kestrel, and configure it for HTTPS with a self-signed test certificate.
            .UseKestrel(ko =>
            {
                // conservative estimation based on https://github.com/aspnet/AspNetCore/blob/caa910ceeba5f2b2c02c47a23ead0ca31caea6f0/src/Servers/Kestrel/Core/src/Internal/Http2/Http2Stream.cs#L204
                ko.Limits.MaxRequestLineSize = Math.Max(ko.Limits.MaxRequestLineSize, configuration.MaxRequestUriSize + 100);
                ko.Limits.MaxRequestHeaderCount = Math.Max(ko.Limits.MaxRequestHeaderCount, configuration.MaxRequestHeaderCount);
                ko.Limits.MaxRequestHeadersTotalSize = Math.Max(ko.Limits.MaxRequestHeadersTotalSize, configuration.MaxRequestHeaderTotalSize);

                ko.Limits.Http2.MaxStreamsPerConnection = configuration.ServerMaxConcurrentStreams ?? ko.Limits.Http2.MaxStreamsPerConnection;
                ko.Limits.Http2.MaxFrameSize = configuration.ServerMaxFrameSize ?? ko.Limits.Http2.MaxFrameSize;
                ko.Limits.Http2.InitialConnectionWindowSize = configuration.ServerInitialConnectionWindowSize ?? ko.Limits.Http2.InitialConnectionWindowSize;
                ko.Limits.Http2.MaxRequestHeaderFieldSize = configuration.ServerMaxRequestHeaderFieldSize ?? ko.Limits.Http2.MaxRequestHeaderFieldSize;

                switch (hostname)
                {
                    case "+":
                    case "*":
                        ko.ListenAnyIP(port, ConfigureListenOptions);
                        break;
                    default:
                        IPAddress iPAddress = Dns.GetHostAddresses(hostname).First();
                        ko.Listen(iPAddress, port, ConfigureListenOptions);
                        break;

                }

                void ConfigureListenOptions(ListenOptions listenOptions)
                {
                    if (scheme == "https")
                    {
                        // Create self-signed cert for server.
                        using (RSA rsa = RSA.Create())
                        {
                            var certReq = new CertificateRequest("CN=contoso.com", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                            certReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
                            certReq.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                            certReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
                            X509Certificate2 cert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddMonths(-1), DateTimeOffset.UtcNow.AddMonths(1));
                            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                            {
                                cert = new X509Certificate2(cert.Export(X509ContentType.Pfx));
                            }
                            listenOptions.UseHttps(cert);
                        }
                    }
                    else
                    {
                        listenOptions.Protocols =
                            configuration.HttpVersion == new Version(2, 0) ?
                            HttpProtocols.Http2 :
                            HttpProtocols.Http1;
                    }
                }
            })


            // Output only warnings and errors from Kestrel

            .ConfigureLogging(log => log.AddFilter("Microsoft.AspNetCore", level => configuration.LogAspNet ? level >= LogLevel.Warning : false))
                // Set up how each request should be handled by the server.
                .Configure(app =>
                {
                    var routeBuilder = new RouteBuilder(app);
                    MapRoutes(routeBuilder);
                    app.UseRouter(routeBuilder.Build());
                });

            // Handle command-line arguments.
            _eventListener =
                configuration.LogPath == null ?
                null :
                (configuration.LogPath == "console" ?
                    (EventListener)new ConsoleHttpEventListener() :
                    (EventListener)new LogHttpEventListener(configuration.LogPath));

            SetUpJustInTimeLogging();

            _webHost = host.Build();
            _webHost.Start();
        }

        private static void MapRoutes(RouteBuilder builder)
        {
            builder.MapGet("/", async context =>
            {
                await context.Response.WriteAsync("ok");
            });
            builder.MapGet("/get", async context =>
            {
                // Get requests just send back the requested content.
                string content = CreateResponseContent(context);
                await context.Response.WriteAsync(content);
            });
            builder.MapGet("/slow", async context =>
            {
                // Sends back the content a character at a time.
                string content = CreateResponseContent(context);

                for (int i = 0; i < content.Length; i++)
                {
                    await context.Response.WriteAsync(content[i].ToString());
                    await context.Response.Body.FlushAsync();
                }
            });
            builder.MapGet("/headers", async context =>
            {
                (string name, StringValues values)[] headersToEcho =
                        context.Request.Headers
                        .Where(h => h.Key.StartsWith("header-"))
                        // kestrel does not seem to be splitting comma separated header values, handle here
                        .Select(h => (h.Key, new StringValues(h.Value.SelectMany(v => v.Split(',')).Select(x => x.Trim()).ToArray())))
                        .ToArray();

                foreach ((string name, StringValues values) in headersToEcho)
                {
                    context.Response.Headers.Add(name, values);
                }

                // send back a checksum of all the echoed headers
                ulong checksum = CRC.CalculateHeaderCrc(headersToEcho);
                AppendChecksumHeader(context.Response.Headers, checksum);

                await context.Response.WriteAsync("ok");

                if (context.Response.SupportsTrailers())
                {
                    // just add variations of already echoed headers as trailers
                    foreach ((string name, StringValues values) in headersToEcho)
                    {
                        context.Response.AppendTrailer(name + "-trailer", values);
                    }
                }

            });
            builder.MapGet("/variables", async context =>
            {
                NameValueCollection nameValueCollection = HttpUtility.ParseQueryString(context.Request.QueryString.Value!);

                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < nameValueCollection.Count; i++)
                {
                    sb.Append(nameValueCollection[$"Var{i}"]);
                }

                await context.Response.WriteAsync(sb.ToString());
            });
            builder.MapGet("/abort", async context =>
            {
                // Server writes some content, then aborts the connection
                string content = CreateResponseContent(context);
                await context.Response.WriteAsync(content.Substring(0, content.Length / 2));
                context.Abort();
            });
            builder.MapPost("/", async context =>
            {
                // Post echos back the requested content, first buffering it all server-side, then sending it all back.
                var s = new MemoryStream();
                await context.Request.Body.CopyToAsync(s);

                ulong checksum = CRC.CalculateCRC(s.ToArray());
                AppendChecksumHeader(context.Response.Headers, checksum);

                s.Position = 0;
                await s.CopyToAsync(context.Response.Body);
            });
            builder.MapPost("/duplex", async context =>
            {
                // Echos back the requested content in a full duplex manner.
                ArrayPool<byte> bufferPool = ArrayPool<byte>.Shared;

                byte[] buffer = bufferPool.Rent(512);
                ulong hashAcc = CRC.InitialCrc;
                int read;

                try
                {
                    while ((read = await context.Request.Body.ReadAsync(buffer, 0, buffer.Length)) != 0)
                    {
                        hashAcc = CRC.update_crc(hashAcc, buffer, read);
                        await context.Response.Body.WriteAsync(buffer, 0, read);
                    }
                }
                finally
                {
                    bufferPool.Return(buffer);
                }

                hashAcc = CRC.InitialCrc ^ hashAcc;

                if (context.Response.SupportsTrailers())
                {
                    context.Response.AppendTrailer("crc32", hashAcc.ToString());
                }
            });
            builder.MapPost("/duplexSlow", async context =>
            {
                // Echos back the requested content in a full duplex manner, but one byte at a time.
                var buffer = new byte[1];
                ulong hashAcc = CRC.InitialCrc;
                while ((await context.Request.Body.ReadAsync(buffer, 0, buffer.Length)) != 0)
                {
                    hashAcc = CRC.update_crc(hashAcc, buffer, buffer.Length);
                    await context.Response.Body.WriteAsync(buffer, 0, buffer.Length);
                }

                hashAcc = CRC.InitialCrc ^ hashAcc;

                if (context.Response.SupportsTrailers())
                {
                    context.Response.AppendTrailer("crc32", hashAcc.ToString());
                }
            });
            builder.MapVerb("HEAD", "/", context =>
           {
               // Just set the max content length on the response.
               string content = CreateResponseContent(context);
               context.Response.Headers.ContentLength = content.Length;
               return Task.CompletedTask;
           });
            builder.MapPut("/", async context =>
            {
                // Read the full request but don't send back a response body.
                await context.Request.Body.CopyToAsync(Stream.Null);
            });
        }

        private static void AppendChecksumHeader(IHeaderDictionary headers, ulong checksum)
        {
            headers.Add("crc32", checksum.ToString());
        }

        public void Dispose()
        {
            _webHost.Dispose();
            _eventListener?.Dispose();
        }

        private void SetUpJustInTimeLogging()
        {
            if (_eventListener == null && !Console.IsInputRedirected)
            {
                // If no command-line requested logging, enable the user to press 'L' to enable logging to the console
                // during execution, so that it can be done just-in-time when something goes awry.
                new Thread(() =>
                {
                    while (true)
                    {
                        if (Console.ReadKey(intercept: true).Key == ConsoleKey.L)
                        {
                            Console.WriteLine("Enabling console event logger");
                            _eventListener = new ConsoleHttpEventListener();
                            break;
                        }
                    }
                })
                { IsBackground = true }.Start();
            }
        }

        private static (string scheme, string hostname, int port) ParseServerUri(string serverUri)
        {
            try
            {
                var uri = new Uri(serverUri);
                return (uri.Scheme, uri.Host, uri.Port);
            }
            catch (UriFormatException)
            {
                // Simple uri parser: used to parse values valid in Kestrel
                // but not representable by the System.Uri class, e.g. https://+:5050
                Match m = Regex.Match(serverUri, "^(?<scheme>https?)://(?<host>[^:/]+)(:(?<port>[0-9]+))?");

                if (!m.Success) throw;

                string scheme = m.Groups["scheme"].Value;
                string hostname = m.Groups["host"].Value;
                int port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value) : (scheme == "https" ? 443 : 80);
                return (scheme, hostname, port);
            }
        }

        private static string CreateResponseContent(Microsoft.AspNetCore.Http.HttpContext ctx)
        {
            return ServerContentUtils.CreateStringContent(GetExpectedContentLength());

            int GetExpectedContentLength()
            {
                if (ctx.Request.Headers.TryGetValue(ExpectedResponseContentLength, out StringValues values) &&
                    values.Count == 1 &&
                    int.TryParse(values[0], out int result))
                {
                    return result;
                }

                throw new Exception($"Could not parse {ExpectedResponseContentLength} header");
            }
        }
    }

    public static class ServerContentUtils
    {
        // deterministically generate ascii string of given length
        public static string CreateStringContent(int contentSize) =>
            new String(
                Enumerable
                    .Range(0, contentSize)
                    .Select(i => (char)(i % 128))
                    .ToArray());

        // used for validating content on client side
        public static bool IsValidServerContent(string input)
        {
            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] != i % 128)
                    return false;
            }

            return true;
        }
    }
}
