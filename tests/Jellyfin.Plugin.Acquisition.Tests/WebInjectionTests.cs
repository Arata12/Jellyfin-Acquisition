using System.Text;
using MediaBrowser.Controller;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Jellyfin.Plugin.Acquisition.Web;

namespace Jellyfin.Plugin.Acquisition.Tests;

public class WebInjectionTests : IDisposable
{
    private readonly string _webDir;
    private readonly Mock<IServerApplicationPaths> _mockPaths;
    private readonly Mock<ILogger<WebInjectionMiddleware>> _mockLogger;

    public WebInjectionTests()
    {
        _webDir = Path.Combine(Path.GetTempPath(), "Web_Test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_webDir);

        _mockPaths = new Mock<IServerApplicationPaths>();
        _mockPaths.Setup(p => p.WebPath).Returns(_webDir);
        _mockLogger = new Mock<ILogger<WebInjectionMiddleware>>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_webDir))
        {
            try { Directory.Delete(_webDir, true); } catch { }
        }
    }

    [Fact]
    public async Task WebInjection_InjectsScriptBeforeClosingBodyTag()
    {
        var indexPath = Path.Combine(_webDir, "index.html");
        await File.WriteAllTextAsync(indexPath, "<!DOCTYPE html><html><head><title>Jellyfin</title></head><body><h1>Welcome</h1></body></html>");

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/web/index.html";
        context.Response.Body = new MemoryStream();

        var wasNextCalled = false;
        RequestDelegate next = ctx =>
        {
            wasNextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new WebInjectionMiddleware(next, _mockPaths.Object, _mockLogger.Object);
        await middleware.InvokeAsync(context);

        Assert.False(wasNextCalled);
        Assert.Equal(200, context.Response.StatusCode);
        // A cached index.html would keep loading the client script of an older plugin build
        Assert.Equal("no-cache", context.Response.Headers.CacheControl.ToString());

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();

        Assert.Matches(@"Acquisition/web/acquisition\.js\?v=\d+\.\d+\.\d+\.\d+-[0-9a-f]{12}", responseText);
        Assert.Matches(@"Acquisition/web/acquisition\.css\?v=\d+\.\d+\.\d+\.\d+-[0-9a-f]{12}", responseText);
        Assert.Contains("</body></html>", responseText);
    }

    [Fact]
    public async Task WebInjection_RespectsBaseUrl()
    {
        var indexPath = Path.Combine(_webDir, "index.html");
        await File.WriteAllTextAsync(indexPath, "<html><body></body></html>");

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.PathBase = "/jellyfin";
        context.Request.Path = "/web/index.html";
        context.Response.Body = new MemoryStream();

        var middleware = new WebInjectionMiddleware(_ => Task.CompletedTask, _mockPaths.Object, _mockLogger.Object);
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();

        Assert.Matches("src=\"/jellyfin/Acquisition/web/acquisition\\.js\\?v=\\d+\\.\\d+\\.\\d+\\.\\d+-[0-9a-f]{12}\"", responseText);
        Assert.Matches("href=\"/jellyfin/Acquisition/web/acquisition\\.css\\?v=\\d+\\.\\d+\\.\\d+\\.\\d+-[0-9a-f]{12}\"", responseText);
    }

    [Fact]
    public async Task WebInjection_ConfiguredBaseUrl_InjectsAcquisitionBaseUrlScript()
    {
        var indexPath = Path.Combine(_webDir, "index.html");
        await File.WriteAllTextAsync(indexPath, "<html><body><h1>Jellyfin</h1></body></html>");

        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/web/index.html";
        context.Response.Body = new MemoryStream();

        var mockConfig = new Mock<MediaBrowser.Controller.Configuration.IServerConfigurationManager>();
        mockConfig.Setup(c => c.GetConfiguration("network")).Returns(new { BaseUrl = "/customprefix" });

        var middleware = new WebInjectionMiddleware(_ => Task.CompletedTask, _mockPaths.Object, _mockLogger.Object, mockConfig.Object);
        await middleware.InvokeAsync(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        var responseText = new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEnd();

        Assert.Contains("<script>window.__ACQUISITION_BASE_URL__ = \"/customprefix\";", responseText);
        Assert.Matches("href=\"/customprefix/Acquisition/web/acquisition\\.css\\?v=\\d+\\.\\d+\\.\\d+\\.\\d+-[0-9a-f]{12}\"", responseText);
        Assert.Matches("src=\"/customprefix/Acquisition/web/acquisition\\.js\\?v=\\d+\\.\\d+\\.\\d+\\.\\d+-[0-9a-f]{12}\"", responseText);
    }

    [Fact]
    public async Task WebInjection_NonIndexRoute_PassesThroughToNext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = "/Items/123/Images/Primary";

        var wasNextCalled = false;
        RequestDelegate next = ctx =>
        {
            wasNextCalled = true;
            return Task.CompletedTask;
        };

        var middleware = new WebInjectionMiddleware(next, _mockPaths.Object, _mockLogger.Object);
        await middleware.InvokeAsync(context);

        Assert.True(wasNextCalled);
    }
}
