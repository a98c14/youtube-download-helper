using System.Text;

namespace YoutubeDownloadHelper.FakeYtDlp;

/// <summary>
/// Writes lines the way <c>yt-dlp.exe</c> does: <c>\n</c> endings on both streams, flushed per line, UTF-8 with
/// <c>--encoding utf-8</c> and otherwise cp1252 with the characters cp1252 lacks silently dropped (spike #33).
/// </summary>
internal sealed class FakeConsole
{
    private readonly Stream _out = Console.OpenStandardOutput();
    private readonly Stream _err = Console.OpenStandardError();
    private readonly Encoding _encoding;

    public FakeConsole(bool utf8)
    {
        if (utf8)
        {
            _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        }
        else
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _encoding = Encoding.GetEncoding(1252, new EncoderReplacementFallback(""), DecoderFallback.ReplacementFallback);
        }
    }

    public void Out(string line) => Write(_out, line);

    public void Err(string line) => Write(_err, line);

    private void Write(Stream stream, string line)
    {
        stream.Write(_encoding.GetBytes(line + "\n"));
        stream.Flush();
    }
}
