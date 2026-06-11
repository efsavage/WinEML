using MimeKit;
using WinEML;
using Xunit;

namespace WinEML.Tests;

public class EmlDocumentTests
{
    // Writes a MimeMessage to a temp .eml, loads it through the real pipeline,
    // and cleans up.
    private static EmlDocument Load(
        MimeMessage message, Func<string, bool>? isImageHostAllowed = null, bool allowAllImagesOnce = false)
    {
        string path = Path.Combine(Path.GetTempPath(), $"wineml_test_{Guid.NewGuid():N}.eml");
        try
        {
            message.WriteTo(path);
            return EmlDocument.Load(path, isImageHostAllowed, allowAllImagesOnce);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    private static MimeMessage Base()
    {
        var m = new MimeMessage();
        m.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        m.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        m.Subject = "Test";
        return m;
    }

    private static MimePart InlineImage(string contentId)
        => new("image", "png")
        {
            ContentId = contentId,
            Content = new MimeContent(new MemoryStream(new byte[] { 1, 2, 3, 4, 5 })),
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Inline),
        };

    private static MimeMessage HtmlWith(string html, params MimePart[] inlineParts)
    {
        var m = Base();
        if (inlineParts.Length == 0)
        {
            m.Body = new TextPart("html") { Text = html };
        }
        else
        {
            var related = new MultipartRelated { new TextPart("html") { Text = html } };
            foreach (var p in inlineParts) related.Add(p);
            m.Body = related;
        }
        return m;
    }

    // ---- body selection ----

    [Fact]
    public void PlainTextOnly_HasTextBody_NoHtml()
    {
        var m = Base();
        m.Body = new TextPart("plain") { Text = "hello world" };

        var doc = Load(m);

        Assert.Null(doc.HtmlBody);
        Assert.Contains("hello world", doc.TextBody);
    }

    [Fact]
    public void HtmlOnly_HasHtmlBody()
    {
        var doc = Load(HtmlWith("<p>hi</p>"));

        Assert.NotNull(doc.HtmlBody);
        Assert.Contains("<p>hi</p>", doc.HtmlBody);
    }

    // ---- CSP ----

    [Fact]
    public void Csp_IsInjectedIntoHead()
    {
        var doc = Load(HtmlWith("<html><head><title>x</title></head><body>hi</body></html>"));

        Assert.NotNull(doc.HtmlBody);
        Assert.Contains("Content-Security-Policy", doc.HtmlBody);
        Assert.Contains("default-src 'none'", doc.HtmlBody);
        // Injected right after <head>, before existing head content.
        int head = doc.HtmlBody!.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
        int csp = doc.HtmlBody.IndexOf("Content-Security-Policy", StringComparison.OrdinalIgnoreCase);
        int title = doc.HtmlBody.IndexOf("<title>", StringComparison.OrdinalIgnoreCase);
        Assert.True(head < csp && csp < title);
    }

    [Fact]
    public void Csp_AddedEvenWithoutHeadTag()
    {
        var doc = Load(HtmlWith("<body>just a body</body>"));
        Assert.Contains("Content-Security-Policy", doc.HtmlBody!);
    }

    [Fact]
    public void Csp_HtmlTagWithoutHead_GetsSynthesizedHead()
    {
        // Case-insensitive <HTML> match; a <head> is created right after it.
        var doc = Load(HtmlWith("<HTML><BODY>hi</BODY></HTML>"));

        int htmlTag = doc.HtmlBody!.IndexOf("<HTML>", StringComparison.Ordinal);
        int csp = doc.HtmlBody.IndexOf("Content-Security-Policy", StringComparison.Ordinal);
        int body = doc.HtmlBody.IndexOf("<BODY>", StringComparison.Ordinal);
        Assert.True(htmlTag >= 0 && htmlTag < csp && csp < body);
    }

    [Fact]
    public void Csp_HeaderElement_IsNotMistakenForHead()
    {
        // A fragment with a <header> element but no <head>/<html>: the policy
        // must be prefixed, never injected inside the look-alike tag.
        var doc = Load(HtmlWith("<header>nav</header><p>hi</p>"));

        Assert.StartsWith("<meta http-equiv=\"Content-Security-Policy\"", doc.HtmlBody!);
        Assert.DoesNotContain("<header>\n<meta", doc.HtmlBody!);
    }

    [Fact]
    public void Csp_MetaTagCarriesTheDocumentPolicy()
    {
        // The meta tag and the response header MainForm serves both come from
        // doc.Csp — this pins the meta side to it.
        var doc = Load(HtmlWith("<p>hi</p>"));
        Assert.Contains(doc.Csp, doc.HtmlBody!);
        Assert.Contains("img-src data:;", doc.Csp);
        Assert.Contains("default-src 'none'", doc.Csp);
    }

    // ---- remote image policy ----

    [Fact]
    public void RemoteImages_StrictByDefault_AndHostsAreReported()
    {
        var doc = Load(HtmlWith(
            "<img src=\"https://cdn.example.com/a.png\"><IMG SRC='http://tracker.evil.example/p.gif'>"));

        Assert.Equal(new[] { "cdn.example.com", "tracker.evil.example" }, doc.RemoteImageHosts);
        Assert.Contains("img-src data:;", doc.Csp); // nothing remote in the policy
    }

    [Fact]
    public void RemoteImages_AllowedHost_AppearsInCsp_HttpsOnly()
    {
        var doc = Load(HtmlWith(
            "<img src=\"https://cdn.example.com/a.png\"><img src=\"https://other.example.org/b.png\">"),
            isImageHostAllowed: h => h == "cdn.example.com");

        Assert.Contains("img-src data: https://cdn.example.com https://*.cdn.example.com;", doc.Csp);
        Assert.DoesNotContain("other.example.org", doc.Csp);
        Assert.DoesNotContain("http:", doc.Csp.Replace("https:", ""));
    }

    [Fact]
    public void RemoteImages_OneShot_AllowsAllImageSchemes()
    {
        var doc = Load(HtmlWith("<img src=\"https://cdn.example.com/a.png\">"),
            allowAllImagesOnce: true);

        Assert.Contains("img-src data: https: http:;", doc.Csp);
    }

    [Fact]
    public void RemoteImages_InlineCidAndDataUris_AreNotReportedAsRemote()
    {
        var doc = Load(HtmlWith("<img src=\"cid:logo\">", InlineImage("logo")));
        Assert.Empty(doc.RemoteImageHosts);
    }

    // ---- inline image (cid:) embedding ----

    [Fact]
    public void InlineImage_ExactCase_IsEmbedded()
    {
        var doc = Load(HtmlWith("<img src=\"cid:logo\">", InlineImage("logo")));

        Assert.Contains("data:image/png;base64,", doc.HtmlBody!);
        Assert.DoesNotContain("cid:logo", doc.HtmlBody!);
    }

    [Fact]
    public void InlineImage_DifferentCase_IsEmbedded()
    {
        // HTML references CID in a different case than the part's Content-Id.
        var doc = Load(HtmlWith("<img src=\"cid:LOGO\">", InlineImage("logo")));

        Assert.Contains("data:image/png;base64,", doc.HtmlBody!);
        Assert.DoesNotContain("cid:LOGO", doc.HtmlBody!);
    }

    [Fact]
    public void InlineImage_UrlEncodedCid_IsEmbedded()
    {
        // Content-Id with an '@' referenced as %40 in the HTML.
        var doc = Load(HtmlWith("<img src=\"cid:foo%40bar\">", InlineImage("foo@bar")));

        Assert.Contains("data:image/png;base64,", doc.HtmlBody!);
        Assert.DoesNotContain("cid:foo%40bar", doc.HtmlBody!);
    }

    [Fact]
    public void InlineImage_UnresolvedCid_IsLeftIntact()
    {
        // Referenced cid has no matching part — must not throw, must not invent data.
        var doc = Load(HtmlWith("<img src=\"cid:missing\">"));

        Assert.Contains("cid:missing", doc.HtmlBody!);
        Assert.DoesNotContain("data:image", doc.HtmlBody!);
    }

    // ---- attachment sizing (must not over- or under-report wildly) ----

    [Fact]
    public void Attachment_UsesDeclaredDispositionSize()
    {
        var m = Base();
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "see attached" } };
        var att = new MimePart("application", "octet-stream")
        {
            FileName = "data.bin",
            Content = new MimeContent(new MemoryStream(new byte[1000])),
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment)
            {
                FileName = "data.bin",
                Size = 1000,
            },
        };
        mixed.Add(att);
        m.Body = mixed;

        var doc = Load(m);

        var single = Assert.Single(doc.Attachments);
        Assert.Equal("data.bin", single.FileName);
        Assert.Equal(1000, single.Size);
    }

    [Fact]
    public void Attachment_EstimatesSize_WithoutDeclaredSize()
    {
        var m = Base();
        var mixed = new Multipart("mixed") { new TextPart("plain") { Text = "see attached" } };
        var payload = new byte[5000];
        var att = new MimePart("application", "octet-stream")
        {
            FileName = "blob.bin",
            Content = new MimeContent(new MemoryStream(payload)),
            ContentTransferEncoding = ContentEncoding.Base64,
            ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment)
            {
                FileName = "blob.bin",
            },
        };
        mixed.Add(att);
        m.Body = mixed;

        var doc = Load(m);

        var single = Assert.Single(doc.Attachments);
        // Estimated from the base64 length; should be in the right ballpark.
        Assert.InRange(single.Size, 4500, 5500);
    }

    [Fact]
    public void Headers_AreExposed()
    {
        var doc = Load(HtmlWith("<p>hi</p>"));
        Assert.Contains("alice@example.com", doc.From);
        Assert.Contains("bob@example.com", doc.To);
        Assert.Equal("Test", doc.Subject);
    }

    [Fact]
    public void ReplyTo_IsExposed()
    {
        var m = HtmlWith("<p>hi</p>");
        m.ReplyTo.Add(new MailboxAddress("Mallory", "mallory@evil.example"));

        var doc = Load(m);

        Assert.Contains("mallory@evil.example", doc.ReplyTo);
    }

    // ---- body decoding ----

    [Fact]
    public void QuotedPrintableBody_IsDecoded()
    {
        var m = Base();
        var part = new TextPart("plain");
        part.SetText(System.Text.Encoding.UTF8, "café crème — naïve résumé");
        part.ContentTransferEncoding = ContentEncoding.QuotedPrintable;
        m.Body = part;

        var doc = Load(m);

        Assert.Contains("café crème — naïve résumé", doc.TextBody);
        Assert.DoesNotContain("=C3=A9", doc.TextBody); // raw QP must not leak through
    }

    [Fact]
    public void MultipartAlternative_ExposesBothBodies()
    {
        var m = Base();
        m.Body = new MultipartAlternative
        {
            new TextPart("plain") { Text = "plain version" },
            new TextPart("html") { Text = "<p>html version</p>" },
        };

        var doc = Load(m);

        Assert.Contains("plain version", doc.TextBody);
        Assert.Contains("<p>html version</p>", doc.HtmlBody!);
    }

    // ---- nested message/rfc822 ----

    [Fact]
    public void NestedMessage_IsListedAndSavesAsParsableEml()
    {
        var inner = Base();
        inner.Subject = "Inner message";
        inner.Body = new TextPart("plain") { Text = "inner body" };

        var outer = Base();
        outer.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "see forwarded message" },
            new MessagePart
            {
                Message = inner,
                ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment),
            },
        };

        var doc = Load(outer);

        var att = Assert.Single(doc.Attachments);
        Assert.Equal("message.eml", att.FileName);

        string path = Path.Combine(Path.GetTempPath(), $"wineml_test_{Guid.NewGuid():N}.eml");
        try
        {
            att.SaveTo(path);
            var roundTripped = MimeMessage.Load(path);
            Assert.Equal("Inner message", roundTripped.Subject);
            Assert.Contains("inner body", roundTripped.TextBody);
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    // ---- the file must be left untouched ----

    [Fact]
    public void Load_DoesNotModifyTheFile()
    {
        var m = HtmlWith("<p>hi</p>", InlineImage("logo"));
        string path = Path.Combine(Path.GetTempPath(), $"wineml_test_{Guid.NewGuid():N}.eml");
        try
        {
            m.WriteTo(path);
            byte[] bytesBefore = File.ReadAllBytes(path);
            DateTime writeBefore = File.GetLastWriteTimeUtc(path);
            DateTime accessBefore = File.GetLastAccessTimeUtc(path);

            EmlDocument.Load(path);

            // Read timestamps before re-reading content — our own ReadAllBytes
            // below would bump the access time and mask the result.
            DateTime accessAfter = File.GetLastAccessTimeUtc(path);
            DateTime writeAfter = File.GetLastWriteTimeUtc(path);

            // UntouchedFile suppresses even the OS last-access bump.
            Assert.Equal(accessBefore, accessAfter);
            Assert.Equal(writeBefore, writeAfter);
            Assert.Equal(bytesBefore, File.ReadAllBytes(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }

    // ---- attachment save ----

    [Fact]
    public void Attachment_SaveTo_RoundTripsExactBytes()
    {
        var payload = new byte[] { 0, 1, 2, 250, 251, 252, 253, 254, 255 };
        var m = Base();
        m.Body = new Multipart("mixed")
        {
            new TextPart("plain") { Text = "see attached" },
            new MimePart("application", "octet-stream")
            {
                FileName = "data.bin",
                Content = new MimeContent(new MemoryStream(payload)),
                ContentTransferEncoding = ContentEncoding.Base64,
                ContentDisposition = new MimeKit.ContentDisposition(MimeKit.ContentDisposition.Attachment)
                {
                    FileName = "data.bin",
                },
            },
        };

        var doc = Load(m);
        var att = Assert.Single(doc.Attachments);

        string path = Path.Combine(Path.GetTempPath(), $"wineml_test_{Guid.NewGuid():N}.bin");
        try
        {
            att.SaveTo(path);
            Assert.Equal(payload, File.ReadAllBytes(path));
        }
        finally
        {
            try { File.Delete(path); } catch { /* ignore */ }
        }
    }
}
