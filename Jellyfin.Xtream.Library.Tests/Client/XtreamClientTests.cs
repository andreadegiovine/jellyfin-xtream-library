// Copyright (C) 2024  Roland Breitschaft

// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.

// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.

// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using FluentAssertions;
using Jellyfin.Xtream.Library.Client;
using Jellyfin.Xtream.Library.Tests.Helpers;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Jellyfin.Xtream.Library.Tests.Client;

public class XtreamClientTests : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly Mock<ILogger<XtreamClient>> _mockLogger;
    private readonly XtreamClient _client;

    public XtreamClientTests()
    {
        _httpClient = new HttpClient();
        _mockLogger = new Mock<ILogger<XtreamClient>>();
        _client = new XtreamClient(_httpClient, _mockLogger.Object);
    }

    public void Dispose()
    {
        // HttpClient is managed by factory in production; dispose directly in tests
        _httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    #region UpdateUserAgent Tests

    [Fact]
    public void UpdateUserAgent_NullAgent_SetsDefaultAgent()
    {
        _client.UpdateUserAgent(null);

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_EmptyString_SetsDefaultAgent()
    {
        _client.UpdateUserAgent(string.Empty);

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_WhitespaceString_SetsDefaultAgent()
    {
        _client.UpdateUserAgent("   ");

        var userAgent = _httpClient.DefaultRequestHeaders.UserAgent.ToString();
        userAgent.Should().Contain("Jellyfin.Xtream.Library");
    }

    [Fact]
    public void UpdateUserAgent_CustomString_SetsCustomAgent()
    {
        const string customAgent = "CustomPlayer/1.0";

        _client.UpdateUserAgent(customAgent);

        _httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out var values);
        values.Should().Contain(customAgent);
    }

    [Fact]
    public void UpdateUserAgent_CalledTwice_ClearsPreviousAgent()
    {
        _client.UpdateUserAgent("FirstAgent/1.0");
        _client.UpdateUserAgent("SecondAgent/1.0");

        _httpClient.DefaultRequestHeaders.TryGetValues("User-Agent", out var values);
        var agentList = values?.ToList();
        agentList.Should().NotBeNull();
        agentList!.Should().HaveCount(1);
        agentList.Should().Contain("SecondAgent/1.0");
    }

    #endregion

    #region Credential-free URL Tests

    [Fact]
    public async Task GetVodCategoryAsync_EmptyCredentials_OmitsCredentialsFromUrl()
    {
        Uri? capturedUri = null;
        var handler = new CapturingHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new XtreamClient(new HttpClient(handler), _mockLogger.Object);
        client.UpdateUserAgent(null);
        var connectionInfo = new ConnectionInfo("http://test.example.com", string.Empty, string.Empty);

        await client.GetVodCategoryAsync(connectionInfo, CancellationToken.None);

        capturedUri.Should().NotBeNull();
        capturedUri!.Query.Should().NotContain("username=");
        capturedUri.Query.Should().NotContain("password=");
        capturedUri.Query.Should().Contain("action=get_vod_categories");
    }

    [Fact]
    public async Task GetVodCategoryAsync_WithCredentials_IncludesCredentialsInUrl()
    {
        Uri? capturedUri = null;
        var handler = new CapturingHttpMessageHandler(request =>
        {
            capturedUri = request.RequestUri;
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
        });
        var client = new XtreamClient(new HttpClient(handler), _mockLogger.Object);
        client.UpdateUserAgent(null);
        var connectionInfo = new ConnectionInfo("http://test.example.com", "myuser", "mypass");

        await client.GetVodCategoryAsync(connectionInfo, CancellationToken.None);

        capturedUri.Should().NotBeNull();
        capturedUri!.Query.Should().Contain("username=myuser");
        capturedUri.Query.Should().Contain("password=mypass");
    }

    #endregion

    #region ConnectionInfo Tests

    [Fact]
    public void ConnectionInfo_Constructor_SetsProperties()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.BaseUrl.Should().Be("http://example.com");
        info.UserName.Should().Be("user");
        info.Password.Should().Be("pass");
    }

    [Fact]
    public void ConnectionInfo_ToString_MasksPassword()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.ToString().Should().Be("http://example.com user:***");
    }

    [Fact]
    public void ConnectionInfo_PropertiesAreMutable()
    {
        var info = new ConnectionInfo("http://example.com", "user", "pass");

        info.BaseUrl = "http://new.com";
        info.UserName = "newuser";
        info.Password = "newpass";

        info.BaseUrl.Should().Be("http://new.com");
        info.UserName.Should().Be("newuser");
        info.Password.Should().Be("newpass");
    }

    #endregion

    #region Timeout Tests

    [Fact]
    public void Timeout_DefaultsToFiveMinutes()
    {
        _client.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task GetVodCategoryAsync_ResponseSlowerThanTimeout_ThrowsTimeoutException()
    {
        var handler = new DelayingHttpMessageHandler(_ => TimeSpan.FromSeconds(30));
        using var httpClient = new HttpClient(handler);
        var client = new XtreamClient(httpClient, _mockLogger.Object)
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 0,
        };
        client.UpdateUserAgent(null);
        var connectionInfo = new ConnectionInfo("http://test.example.com", "user", "secret");

        var act = () => client.GetVodCategoryAsync(connectionInfo, CancellationToken.None);

        // A bare TaskCanceledException is what users see today and it reads as "cancelled",
        // not "too slow". The timeout must surface as a timeout.
        await act.Should().ThrowAsync<TimeoutException>();
        handler.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task GetVodCategoryAsync_TimesOutThenSucceeds_RetriesAndReturnsContent()
    {
        // First two attempts exceed the timeout, the third answers immediately.
        var handler = new DelayingHttpMessageHandler(
            attempt => attempt <= 2 ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        using var httpClient = new HttpClient(handler);
        var client = new XtreamClient(httpClient, _mockLogger.Object)
        {
            Timeout = TimeSpan.FromMilliseconds(50),
            MaxRetries = 3,
            RetryDelayMs = 1,
        };
        client.UpdateUserAgent(null);
        var connectionInfo = new ConnectionInfo("http://test.example.com", "user", "secret");

        var result = await client.GetVodCategoryAsync(connectionInfo, CancellationToken.None);

        result.Should().BeEmpty();
        handler.Attempts.Should().Be(3);
    }

    [Fact]
    public async Task GetVodCategoryAsync_CallerCancels_DoesNotRetry()
    {
        using var cts = new CancellationTokenSource();
        var handler = new DelayingHttpMessageHandler(_ => TimeSpan.FromSeconds(30), onRequest: cts.Cancel);
        using var httpClient = new HttpClient(handler);
        var client = new XtreamClient(httpClient, _mockLogger.Object)
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxRetries = 3,
            RetryDelayMs = 1,
        };
        client.UpdateUserAgent(null);
        var connectionInfo = new ConnectionInfo("http://test.example.com", "user", "secret");

        var act = () => client.GetVodCategoryAsync(connectionInfo, cts.Token);

        // Caller-driven cancellation must propagate, not be retried as if it were a timeout.
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.Attempts.Should().Be(1);
    }

    #endregion

    private sealed class CapturingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private sealed class DelayingHttpMessageHandler(Func<int, TimeSpan> delayForAttempt, Action? onRequest = null)
        : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            onRequest?.Invoke();

            var delay = delayForAttempt(attempt);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json"),
            };
        }
    }
}
