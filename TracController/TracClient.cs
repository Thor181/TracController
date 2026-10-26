using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace TracController;

internal sealed record VpnSite(string Name, string? Gateway, string? Status)
{
    public bool IsConnected =>
        Status != null
        && Status.Contains("connected", StringComparison.OrdinalIgnoreCase)
        && !Status.Contains("disconnected", StringComparison.OrdinalIgnoreCase)
        && !Status.Contains("not connected", StringComparison.OrdinalIgnoreCase);

    public bool IsConnecting =>
        Status != null && Status.Contains("connecting", StringComparison.OrdinalIgnoreCase);
}

internal sealed record TracResult(int ExitCode, string Output, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

internal sealed partial class TracClient(string tracPath)
{
    // Не даём фоновому опросу статуса пересекаться с подключением/отключением
    private readonly SemaphoreSlim _lock = new(1, 1);

    public string TracPath { get; } = tracPath;

    public async Task<IReadOnlyList<VpnSite>> GetSitesAsync(CancellationToken ct = default)
    {
        var result = await RunLockedAsync(["info"], TimeSpan.FromSeconds(30), ct);
        if (result.TimedOut)
            throw new TracException("trac info не ответил за 30 секунд", result.Output);
        var sites = ParseInfo(result.Output);
        if (sites.Count == 0 && result.ExitCode != 0)
            throw new TracException("trac info завершился с ошибкой", result.Output);
        return sites;
    }

    public Task<TracResult> ConnectAsync(string site, bool useGui, CancellationToken ct = default) =>
        RunLockedAsync([useGui ? "connectgui" : "connect", "-s", site], TimeSpan.FromMinutes(3), ct);

    public Task<TracResult> DisconnectAsync(CancellationToken ct = default) =>
        RunLockedAsync(["disconnect"], TimeSpan.FromSeconds(60), ct);

    /// <summary>
    /// Разбирает вывод "trac info". Типичный формат:
    /// <code>
    /// Conn Office VPN:
    ///         gateway: vpn.example.com (IP 192.0.2.1)
    ///         type: user
    ///         status: Connected
    /// </code>
    /// </summary>
    public static List<VpnSite> ParseInfo(string output)
    {
        var sites = new List<VpnSite>();
        string? name = null;
        Dictionary<string, string> props = new(StringComparer.OrdinalIgnoreCase);

        void Flush()
        {
            if (name == null)
                return;
            props.TryGetValue("gateway", out var gateway);
            props.TryGetValue("status", out var status);
            sites.Add(new VpnSite(name, gateway, status));
        }

        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var header = ConnHeader().Match(line);
            if (header.Success)
            {
                Flush();
                name = header.Groups[1].Value.Trim();
                props = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            if (name == null)
                continue;
            var prop = PropertyLine().Match(line);
            if (prop.Success)
                props.TryAdd(prop.Groups[1].Value.Trim(), prop.Groups[2].Value.Trim());
        }
        Flush();
        return sites;
    }

    [GeneratedRegex(@"^\s*Conn\s+(.+?):\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConnHeader();

    [GeneratedRegex(@"^\s+([^:]+?):\s*(.*)$")]
    private static partial Regex PropertyLine();

    private async Task<TracResult> RunLockedAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            return await RunAsync(args, timeout, ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<TracResult> RunAsync(string[] args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(TracPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = Path.GetDirectoryName(TracPath)!,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi) ?? throw new TracException("Не удалось запустить trac.exe", "");
        // Если trac спросит логин/пароль в консоли — пусть сразу получит EOF, а не висит
        process.StandardInput.Close();

        // Читаем сырые байты: кодировка вывода зависит от версии trac, определяем её сами
        var stdout = ReadAllBytesAsync(process.StandardOutput.BaseStream, ct);
        var stderr = ReadAllBytesAsync(process.StandardError.BaseStream, ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = !ct.IsCancellationRequested;
            try { process.Kill(entireProcessTree: true); } catch { /* уже завершился */ }
            if (!timedOut)
                throw;
        }

        var output = DecodeOutput(await stdout) + DecodeOutput(await stderr);
        return new TracResult(timedOut ? -1 : process.ExitCode, output.Trim(), timedOut);
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>
    /// Определяет кодировку вывода trac.exe: UTF-16, UTF-8 или однобайтовая (OEM, например cp866,
    /// либо ANSI, например cp1251). Из однобайтовых выбирается та, в которой текст больше похож на кириллицу.
    /// </summary>
    public static string DecodeOutput(byte[] bytes)
    {
        if (bytes.Length == 0)
            return "";

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 LE без BOM: в ASCII-тексте каждый второй байт нулевой
        if (bytes.Length >= 4 && bytes.Length % 2 == 0 && bytes.Where((_, i) => i % 2 == 1).Count(b => b == 0) > bytes.Length / 4)
            return Encoding.Unicode.GetString(bytes);

        if (bytes.All(b => b < 0x80))
            return Encoding.ASCII.GetString(bytes);

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // не UTF-8 — значит однобайтовая кодировка
        }

        var culture = CultureInfo.CurrentCulture.TextInfo;
        var candidates = new[] { culture.OEMCodePage, culture.ANSICodePage, 866, 1251 }
            .Distinct()
            .Select(cp => Encoding.GetEncoding(cp).GetString(bytes));
        return candidates.MaxBy(ScoreText)!;
    }

    private static int ScoreText(string text)
    {
        int score = 0;
        foreach (var ch in text)
        {
            if (ch is >= 'А' and <= 'я' or 'Ё' or 'ё')
                score += 2;
            else if (ch is >= '─' and <= '▟') // псевдографика — признак неверной кодировки
                score -= 3;
            else if (ch >= 0x80 && !char.IsLetterOrDigit(ch) && !char.IsWhiteSpace(ch))
                score -= 1;
        }
        return score;
    }
}

internal sealed class TracException(string message, string output) : Exception(message)
{
    public string Output { get; } = output;
}
