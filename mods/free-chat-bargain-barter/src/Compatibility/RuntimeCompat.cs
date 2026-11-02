using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace AIWorldPawnshop;

internal static class RuntimeCompat
{
    public static void Require(object? value, string name)
    {
        if (value is null)
            throw new ArgumentNullException(name);
    }

    public static string[] SplitTerms(string? value) =>
        (value ?? string.Empty)
            .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.Trim())
            .Where(term => term.Length > 0)
            .ToArray();

    public static byte[] HashSha256(byte[] value)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(value);
    }

    public static string ToHex(byte[] value) => BitConverter.ToString(value).Replace("-", string.Empty);

    public static string SingleLine(string value) =>
        value.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

    public static int NewWorldSeed() => BitConverter.ToInt32(Guid.NewGuid().ToByteArray(), 0) & int.MaxValue;

    public static void MoveReplace(string source, string destination)
    {
        if (File.Exists(destination))
            File.Replace(source, destination, null);
        else
            File.Move(source, destination);
    }

    public static async Task<string> ReadBodyAsync(HttpContent content, CancellationToken cancellationToken)
    {
        using var source = await content.ReadAsStreamAsync().ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, 81920, cancellationToken).ConfigureAwait(false);
        buffer.Position = 0;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try { encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        using var reader = new StreamReader(buffer, encoding, true);
        return reader.ReadToEnd();
    }
}
