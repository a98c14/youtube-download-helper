using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using YoutubeDownloadHelper.Engine.Hosting;

namespace YoutubeDownloadHelper.Engine.Tests.Hosting;

public sealed class RedactionTests
{
    private const string CookieValue = "AKEyXzX-sEcReT_cOoKiE";
    private const string SapisidValue = "Zy9_sapisid-SECRET/A";

    private static readonly ApiToken Token = new();

    private static readonly string NetscapeCookies =
        "# Netscape HTTP Cookie File\n"
        + $".youtube.com\tTRUE\t/\tTRUE\t1893456000\tSAPISID\t{SapisidValue}\n"
        + $"#HttpOnly_.youtube.com\tTRUE\t/\tTRUE\t0\tLOGIN_INFO\t{CookieValue}\n";

    [Theory]
    [InlineData("token {0} in the middle")]
    [InlineData("Authorization: Bearer {0}")]
    [InlineData("authorization={0}")]
    [InlineData("Bearer {0}")]
    [InlineData("bearer {0}")]
    public void Token_is_redacted(string format)
    {
        var text = string.Format(CultureInfo.InvariantCulture, format, Token.Value);

        var redacted = new LogRedactor(Token).Redact(text);

        Assert.DoesNotContain(Token.Value, redacted, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA")]
    [InlineData("Bearer some-other.token_value", "some-other.token_value")]
    [InlineData("Cookie: SID=" + CookieValue + "; HSID=abc", CookieValue)]
    [InlineData("cookie: SID=" + CookieValue, CookieValue)]
    [InlineData("Set-Cookie: SAPISID=" + SapisidValue + "; Path=/; Secure", SapisidValue)]
    [InlineData("headers: {'Cookie': 'SID=" + CookieValue + "'}", CookieValue)]
    public void Credentials_and_cookie_headers_are_redacted(string text, string secret)
    {
        var redacted = new LogRedactor(Token).Redact(text);

        Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Netscape_cookie_values_are_redacted_and_the_rest_is_kept()
    {
        var redacted = new LogRedactor(Token).Redact(NetscapeCookies);

        Assert.DoesNotContain(CookieValue, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain(SapisidValue, redacted, StringComparison.Ordinal);
        Assert.Contains($".youtube.com\tTRUE\t/\tTRUE\t1893456000\tSAPISID\t{LogRedactor.Mask}", redacted, StringComparison.Ordinal);
        Assert.Contains($"LOGIN_INFO\t{LogRedactor.Mask}", redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Downloading item 42 to C:\\Users\\İlkay\\Downloads\\video.mp4")]
    [InlineData("--cookies C:\\data\\work\\42\\cookies.txt")]
    [InlineData("Cookies are stored")]
    [InlineData("")]
    public void Ordinary_text_is_unchanged(string text) =>
        Assert.Equal(text, new LogRedactor(Token).Redact(text));

    [Fact]
    public void Redaction_does_not_depend_on_the_current_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            var redacted = new LogRedactor(Token).Redact("AUTHORIZATION: Bearer x\nCOOKIE: SID=" + CookieValue);
            Assert.DoesNotContain(CookieValue, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain("Bearer x", redacted, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Enricher_redacts_every_property_before_the_sink()
    {
        var sink = new CollectingSink();
        using (var logger = new LoggerConfiguration().Enrich.With(new RedactingEnricher(new LogRedactor(Token))).WriteTo.Sink(sink).CreateLogger())
        {
            logger.Information("Request with {Header}", "Authorization: Bearer " + Token.Value);
            logger.Information("Token is {Value}", Token.Value);
            logger.Information("Writing {Authorization}", "anything at all");
            logger.Information("Cookies {Cookies}", new[] { "SID=" + CookieValue });
            logger.Information("Jar {@Jar}", new { Domain = ".youtube.com", Cookie = CookieValue, Nested = new { Value = Token.Value } });
            logger.Information("Map {Map}", new Dictionary<string, string> { ["token"] = "plain-but-sensitive", ["other"] = "Cookie: SID=" + CookieValue });
            logger.Information("Lines {Lines}", [NetscapeCookies]);
            logger.Information("Item {ItemId} at {Path}", 42, "C:\\İndirgeç\\a.mp4");
        }

        var rendered = string.Join("\n", sink.Events.Select(e => e.RenderMessage(CultureInfo.InvariantCulture)));

        foreach (var secret in (string[])[Token.Value, CookieValue, SapisidValue, "anything at all", "plain-but-sensitive"])
        {
            Assert.DoesNotContain(secret, rendered, StringComparison.Ordinal);
        }

        Assert.Contains("Item 42 at \"C:\\İndirgeç\\a.mp4\"", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Engine_log_file_is_redacted()
    {
        await using var engine = await TestEngine.StartAsync();
        var logger = engine.App.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Test");
        var token = engine.Token.Value;

#pragma warning disable CA1848, CA2254 // Plain logging calls are what the rest of the code base might write.
        logger.LogWarning("Header {Authorization}", "Bearer " + token);
        logger.LogWarning("Raw {Text}", "token=" + token);
        logger.LogWarning("Cookies file {Content}", NetscapeCookies);
#pragma warning restore CA1848, CA2254

        var logsDir = engine.Paths.LogsDir;
        await engine.StopAsync();
        await engine.App.DisposeAsync(); // flushes and closes the file

        var file = Assert.Single(Directory.GetFiles(logsDir, "engine-*.log"));
        Assert.Matches(@"engine-\d{8}\.log$", file);
        var content = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        Assert.Contains("Header", content, StringComparison.Ordinal);
        Assert.Contains(LogRedactor.Mask, content, StringComparison.Ordinal);
        foreach (var secret in (string[])[token, CookieValue, SapisidValue])
        {
            Assert.DoesNotContain(secret, content, StringComparison.Ordinal);
        }
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
