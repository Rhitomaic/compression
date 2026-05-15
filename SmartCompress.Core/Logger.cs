using System.Text;

namespace SmartCompress.Core;

public sealed class Logger : IDisposable
{
    private StreamWriter? _writer;

    public void Init(string outDir)
    {
        Dispose();
        Directory.CreateDirectory(outDir);
        _writer = new StreamWriter(
            Path.Combine(outDir, "compress.log"),
            append: true,
            Encoding.UTF8);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _writer.WriteLine($"\n{new string('=', 52)}\n  {stamp}\n{new string('=', 52)}");
        _writer.Flush();
    }

    public void Write(string text)
    {
        _writer?.WriteLine(text);
        _writer?.Flush();
    }

    public void Dispose()
    {
        _writer?.Close();
        _writer = null;
    }
}
