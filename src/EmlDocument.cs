using System.Text;
using System.Text.RegularExpressions;
using MimeKit;

namespace WinEML;

/// <summary>
/// A parsed .eml file, reduced to exactly what the viewer needs to render.
/// All MimeKit interaction is contained here so the UI stays dumb and fast.
/// </summary>
internal sealed class EmlDocument
{
    public string FilePath { get; }
    public string From { get; }
    public string To { get; }
    public string Cc { get; }
    public string ReplyTo { get; }
    public string Subject { get; }
    public string Date { get; }

    /// <summary>Plain-text body, shown instantly. Never null (may be empty).</summary>
    public string TextBody { get; }

    /// <summary>
    /// HTML body with inline (cid:) images already embedded as data URIs, or null
    /// if the message has no HTML part.
    /// </summary>
    public string? HtmlBody { get; }

    public IReadOnlyList<EmlAttachment> Attachments { get; }

    private EmlDocument(
        string filePath, string from, string to, string cc, string replyTo,
        string subject, string date, string textBody, string? htmlBody,
        IReadOnlyList<EmlAttachment> attachments)
    {
        FilePath = filePath;
        From = from;
        To = to;
        Cc = cc;
        ReplyTo = replyTo;
        Subject = subject;
        Date = date;
        TextBody = textBody;
        HtmlBody = htmlBody;
        Attachments = attachments;
    }

    public static EmlDocument Load(string path)
    {
        MimeMessage message;
        using (var stream = UntouchedFile.OpenRead(path))
            message = MimeMessage.Load(stream);

        string date = message.Date == DateTimeOffset.MinValue
            ? string.Empty
            : message.Date.LocalDateTime.ToString("ddd, d MMM yyyy  h:mm tt");

        string? html = message.HtmlBody;
        if (!string.IsNullOrEmpty(html))
        {
            html = EmbedInlineImages(message, html!);
            html = InjectCsp(html);
        }

        string text = message.TextBody ?? string.Empty;

        var attachments = new List<EmlAttachment>();
        foreach (var entity in message.Attachments)
            attachments.Add(EmlAttachment.From(entity));

        return new EmlDocument(
            filePath: path,
            from: FormatAddresses(message.From),
            to: FormatAddresses(message.To),
            cc: FormatAddresses(message.Cc),
            replyTo: FormatAddresses(message.ReplyTo),
            subject: message.Subject ?? "(no subject)",
            date: date,
            textBody: text,
            htmlBody: html,
            attachments: attachments);
    }

    private static string FormatAddresses(InternetAddressList list)
        => list.Count == 0 ? string.Empty : list.ToString();

    // The one CSP for rendered mail: only locally-embedded (data:) resources and
    // inline styles. Anything remote — scripts, images, fonts, stylesheets, frames,
    // beacons — is refused by the renderer itself. Delivered twice: as the response
    // *header* when MainForm serves the document (authoritative — immune to malformed
    // markup), and as a meta tag injected below (defense-in-depth).
    internal const string CspPolicy =
        "default-src 'none'; " +
        "img-src data:; " +
        "style-src 'unsafe-inline' data:; " +
        "font-src data:; " +
        "media-src data:; " +
        "base-uri 'none'; " +
        "form-action 'none'";

    private const string CspMeta =
        "<meta http-equiv=\"Content-Security-Policy\" content=\"" + CspPolicy + "\">";

    private static string InjectCsp(string html)
    {
        // Insert the CSP meta as early as possible inside <head> so it governs the
        // whole document. Fall back gracefully for malformed/partial HTML.
        int head = FindOpenTag(html, "head");
        if (head >= 0)
        {
            int close = html.IndexOf('>', head);
            if (close >= 0) return html.Insert(close + 1, "\n" + CspMeta);
        }

        int htmlTag = FindOpenTag(html, "html");
        if (htmlTag >= 0)
        {
            int close = html.IndexOf('>', htmlTag);
            if (close >= 0) return html.Insert(close + 1, "<head>" + CspMeta + "</head>");
        }

        return CspMeta + html;
    }

    /// <summary>
    /// Find an opening tag like &lt;head&gt; or &lt;head ...&gt; case-insensitively,
    /// without matching look-alikes such as &lt;header&gt;.
    /// </summary>
    private static int FindOpenTag(string html, string tag)
    {
        int i = 0;
        string needle = "<" + tag;
        while ((i = html.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            int after = i + needle.Length;
            if (after >= html.Length) return -1;
            char c = html[after];
            if (c is '>' or ' ' or '\t' or '\r' or '\n' or '/') return i;
            i = after;
        }
        return -1;
    }

    // Matches a cid: reference up to the first delimiter (quote, whitespace, ), >).
    private static readonly Regex CidReference =
        new("cid:([^\"'\\s)>]+)", RegexOptions.IgnoreCase);

    /// <summary>
    /// Replace every cid: reference in the HTML with a self-contained data: URI so
    /// the rendered view needs zero network access (private + instant). Matching is
    /// case-insensitive and URL-decoded, and only referenced parts are decoded.
    /// </summary>
    internal static string EmbedInlineImages(MimeMessage message, string html)
    {
        // 1) Which content-ids does the HTML actually reference? (URL-decoded.)
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in CidReference.Matches(html))
            referenced.Add(DecodeCid(m.Groups[1].Value));
        if (referenced.Count == 0)
            return html;

        // 2) Decode only the parts that are referenced, into a cid -> data: map.
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in message.BodyParts.OfType<MimePart>())
        {
            if (part.Content is null || string.IsNullOrEmpty(part.ContentId))
                continue;
            string cid = part.ContentId.Trim('<', '>');
            if (!referenced.Contains(cid) || map.ContainsKey(cid))
                continue;

            using var ms = new MemoryStream();
            part.Content.DecodeTo(ms);
            string b64 = Convert.ToBase64String(ms.ToArray());
            map[cid] = $"data:{part.ContentType.MimeType};base64,{b64}";
        }
        if (map.Count == 0)
            return html;

        // 3) Single case-insensitive pass; unresolved cids are left untouched
        //    (and will simply be blocked, never fetched).
        return CidReference.Replace(html, m =>
            map.TryGetValue(DecodeCid(m.Groups[1].Value), out var uri) ? uri : m.Value);
    }

    private static string DecodeCid(string raw)
    {
        try { return Uri.UnescapeDataString(raw); }
        catch { return raw; }
    }
}

/// <summary>A savable attachment, keeping a handle to its MimeKit entity.</summary>
internal sealed class EmlAttachment
{
    private readonly MimeEntity _entity;

    public string FileName { get; }
    public long Size { get; }

    private EmlAttachment(MimeEntity entity, string fileName, long size)
    {
        _entity = entity;
        FileName = fileName;
        Size = size;
    }

    public static EmlAttachment From(MimeEntity entity)
    {
        string name;
        long size = 0;

        if (entity is MimePart part)
        {
            name = part.FileName ?? "attachment";
            size = ApproxSize(part); // cheap: never decodes the payload at open time
        }
        else if (entity is MessagePart)
        {
            name = "message.eml";
        }
        else
        {
            name = "attachment";
        }

        return new EmlAttachment(entity, name, size);
    }

    /// <summary>
    /// Estimate an attachment's size without decoding it (keeps open fast and
    /// memory flat). Prefers the declared Content-Disposition size; otherwise
    /// uses the raw encoded length, adjusted for base64's ~33% inflation.
    /// </summary>
    private static long ApproxSize(MimePart part)
    {
        if (part.ContentDisposition?.Size is long declared && declared >= 0)
            return declared;

        long encoded = part.Content?.Stream?.Length ?? 0;
        return part.ContentTransferEncoding == ContentEncoding.Base64
            ? encoded * 3 / 4
            : encoded;
    }

    public void SaveTo(string path)
    {
        using var stream = File.Create(path);
        if (_entity is MimePart part)
            part.Content?.DecodeTo(stream);
        else if (_entity is MessagePart msg)
            msg.Message?.WriteTo(stream);
    }
}
