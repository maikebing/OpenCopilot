using System;
using System.Net;
using System.Net.Http;
using Xunit;
using OpenCopilot.Http;

namespace OpenCopilot.Tests.Http
{
    public class HttpClientFactoryTests
    {
        [Fact]
        public void Create_WithNullProxyUrl_ReturnsPlainHttpClient()
        {
            using var client = HttpClientFactory.Create(null, TimeSpan.FromSeconds(10));

            Assert.NotNull(client);
            Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        }

        [Fact]
        public void Create_WithEmptyProxyUrl_ReturnsPlainHttpClient()
        {
            using var client = HttpClientFactory.Create(string.Empty, TimeSpan.FromSeconds(10));

            Assert.NotNull(client);
            Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        }

        [Fact]
        public void Create_WithWhitespaceProxyUrl_ReturnsPlainHttpClient()
        {
            using var client = HttpClientFactory.Create("   ", TimeSpan.FromSeconds(10));

            Assert.NotNull(client);
        }

        [Fact]
        public void Create_WithProxyUrl_ReturnsClientWithProxy()
        {
            // Just verify a client is returned without throwing;
            // actual proxy routing is tested at integration level.
            using var client = HttpClientFactory.Create("http://127.0.0.1:7890", TimeSpan.FromSeconds(30));

            Assert.NotNull(client);
            Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        }

        [Fact]
        public void Create_ConvenienceOverload_DefaultsTo120SecondTimeout()
        {
            using var client = HttpClientFactory.Create(null);

            Assert.Equal(TimeSpan.FromSeconds(120), client.Timeout);
        }

        [Fact]
        public void Create_ConvenienceOverload_WithProxy_DefaultsTo120SecondTimeout()
        {
            using var client = HttpClientFactory.Create("http://127.0.0.1:7890");

            Assert.Equal(TimeSpan.FromSeconds(120), client.Timeout);
        }

        [Fact]
        public void Create_MultipleCallsWithSameArgs_ReturnSeparateInstances()
        {
            using var a = HttpClientFactory.Create(null, TimeSpan.FromSeconds(10));
            using var b = HttpClientFactory.Create(null, TimeSpan.FromSeconds(10));

            Assert.NotSame(a, b);
        }
    }
}
