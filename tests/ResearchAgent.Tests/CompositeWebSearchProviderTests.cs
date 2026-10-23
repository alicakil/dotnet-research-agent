using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using WebApi.Services.Ai;
using Xunit;

namespace Nebula.UnitTests.WebApi.Services;

/// <summary>
/// Arama sağlayıcı zincirinin semantiği. Gerçek durum bunu doğurdu: Serper anahtarı 403 verirken
/// kanıt toplamanın durmaması, ama arızanın da yutulmaması gerekiyor. Ayrıca PARALEL çağrı olmamalı
/// — aynı kanıt için iki kez ödemek istemiyoruz.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Category", "CareerDeepResearch")]
public class CompositeWebSearchProviderTests
{
    private static CompositeWebSearchProvider Build(params IWebSearchProvider[] providers) =>
        new(providers, NullLogger<CompositeWebSearchProvider>.Instance);

    [Fact]
    public async Task SearchAsync_FirstProviderSucceeds_SecondIsNotCalled()
    {
        var cheap = new StubProvider(configured: true, results: 2);
        var expensive = new StubProvider(configured: true, results: 5);

        var outcome = await Build(cheap, expensive).SearchAsync("q", 10);

        outcome.Results.Should().HaveCount(2);
        outcome.Failed.Should().BeFalse();
        expensive.Calls.Should().Be(0, "ucuz sağlayıcı sonuç döndüyse pahalıya para ödenmemeli");
    }

    [Fact]
    public async Task SearchAsync_FirstFails_FallsBackToSecond()
    {
        var broken = new StubProvider(configured: true, fails: true);   // Serper 403 senaryosu
        var backup = new StubProvider(configured: true, results: 3);

        var outcome = await Build(broken, backup).SearchAsync("q", 10);

        outcome.Failed.Should().BeFalse();
        outcome.Results.Should().HaveCount(3);
        broken.Calls.Should().Be(1);
        backup.Calls.Should().Be(1);
    }

    [Fact]
    public async Task SearchAsync_UnconfiguredProvidersAreSkipped()
    {
        var off = new StubProvider(configured: false, results: 9);
        var on = new StubProvider(configured: true, results: 1);

        var outcome = await Build(off, on).SearchAsync("q", 10);

        outcome.Results.Should().HaveCount(1);
        off.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_AllFail_ReportsFailureSoHumansNotice()
    {
        var outcome = await Build(
            new StubProvider(configured: true, fails: true),
            new StubProvider(configured: true, fails: true)).SearchAsync("q", 10);

        outcome.Failed.Should().BeTrue();
        outcome.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SearchAsync_AllEmpty_IsNotAFailure()
    {
        // Zayıf dijital iz NORMAL (ölçtük) — arıza olarak raporlanmamalı.
        var outcome = await Build(
            new StubProvider(configured: true, results: 0),
            new StubProvider(configured: true, results: 0)).SearchAsync("q", 10);

        outcome.Failed.Should().BeFalse();
        outcome.Results.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_NoProviderConfigured_ReturnsEmptyQuietly()
    {
        var outcome = await Build(new StubProvider(configured: false)).SearchAsync("q", 10);

        outcome.Failed.Should().BeFalse();
        outcome.Results.Should().BeEmpty();
    }

    [Fact]
    public void IsConfigured_TrueWhenAnyProviderIsConfigured()
    {
        Build(new StubProvider(false), new StubProvider(true)).IsConfigured.Should().BeTrue();
        Build(new StubProvider(false), new StubProvider(false)).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task SearchAsync_PassesTheCountryToEveryProvider()
    {
        var broken = new CountryStub(fails: true);
        var backup = new CountryStub(fails: false);

        await Build(broken, backup).SearchAsync("dermatologist Dublin", 5, "IE");

        broken.Country.Should().Be("IE");
        backup.Country.Should().Be("IE", "yedek sağlayıcı da aynı ülkeden aramalı (VIT-1308)");
    }

    [Fact]
    public async Task SearchAsync_ProviderWithoutCountrySupportSearchesWithoutIt()
    {
        var plain = new StubProvider(configured: true, results: 2);

        var outcome = await Build(plain).SearchAsync("dermatologist Dublin", 5, "IE");

        outcome.Results.Should().HaveCount(2);
        plain.Calls.Should().Be(1);
    }

    private sealed class CountryStub(bool fails) : IWebSearchProvider
    {
        public string? Country { get; private set; }
        public bool IsConfigured => true;

        public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default) =>
            SearchAsync(query, maxResults, null, ct);

        public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, string? countryCode, CancellationToken ct = default)
        {
            Country = countryCode;
            return Task.FromResult(fails
                ? WebSearchOutcome.Fail("HTTP 429")
                : WebSearchOutcome.Ok([new WebSearchResult("t", "s", "https://example.ie/1")]));
        }
    }

    private sealed class StubProvider(bool configured, int results = 0, bool fails = false) : IWebSearchProvider
    {
        public int Calls { get; private set; }
        public bool IsConfigured => configured;

        public Task<WebSearchOutcome> SearchAsync(string query, int maxResults, CancellationToken ct = default)
        {
            Calls++;
            if (fails) return Task.FromResult(WebSearchOutcome.Fail("HTTP 403"));
            return Task.FromResult(WebSearchOutcome.Ok(
                Enumerable.Range(0, results)
                    .Select(i => new WebSearchResult($"t{i}", $"s{i}", $"https://example.com/{i}"))
                    .ToList()));
        }
    }
}
