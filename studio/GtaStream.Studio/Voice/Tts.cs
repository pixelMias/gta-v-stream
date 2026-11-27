using System.Diagnostics;
using System.Net.Http.Json;

namespace GtaStream.Studio.Voice;

public sealed record Speech(byte[] Audio, string ContentType, string Engine);

/// <summary>Turns one line of the AI figure into audio. Null = no voice right now (the text still shows as subtitle).</summary>
public interface ITts
{
    Task<Speech?> Speak(string text, CancellationToken ct);
}

public sealed class NoTts : ITts
{
    public Task<Speech?> Speak(string text, CancellationToken ct) => Task.FromResult<Speech?>(null);
}

/// <summary>Premium voice (concept, section 4b: ElevenLabs Flash, about 0.05 $ per 1,000 characters).</summary>
public sealed class ElevenLabsTts(HttpClient http, string apiKey, string voiceId, string model) : ITts
{
    public async Task<Speech?> Speak(string text, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=mp3_44100_128");
        req.Headers.Add("xi-api-key", apiKey);
        req.Content = JsonContent.Create(new { text, model_id = model, language_code = "de" });
        using var res = await http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            Console.WriteLine($"[stimme] ElevenLabs HTTP {(int)res.StatusCode}");
            return null;
        }
        return new Speech(await res.Content.ReadAsByteArrayAsync(ct), "audio/mpeg", "elevenlabs");
    }
}

/// <summary>Local voice on the CPU (Piper, e.g. Thorsten). Free, used at night and as fallback.</summary>
public sealed class PiperTts(string piperPath, string voiceModel) : ITts
{
    public async Task<Speech?> Speak(string text, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"gtastream-{Guid.NewGuid():n}.wav");
        try
        {
            var psi = new ProcessStartInfo(piperPath)
            {
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(voiceModel);
            psi.ArgumentList.Add("--output_file"); psi.ArgumentList.Add(file);
            using var p = Process.Start(psi)!;
            await p.StandardInput.WriteLineAsync(text.AsMemory(), ct);
            p.StandardInput.Close();
            _ = p.StandardOutput.ReadToEndAsync(ct);
            var err = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            if (p.ExitCode != 0 || !File.Exists(file))
            {
                Console.WriteLine($"[stimme] Piper Fehler {p.ExitCode}: {(await err).Trim()}");
                return null;
            }
            return new Speech(await File.ReadAllBytesAsync(file, ct), "audio/wav", "piper");
        }
        finally
        {
            try { File.Delete(file); } catch (IOException) { }
        }
    }
}

/// <summary>
/// Premium voice during the given local hours (concept: ElevenLabs about 4 h in the evening), local voice otherwise
/// and whenever the premium voice fails.
/// </summary>
public sealed class HybridTts(ITts? premium, ITts? local, int fromHour, int toHour, TimeProvider time) : ITts
{
    public bool PremiumNow()
    {
        var h = time.GetLocalNow().Hour;
        return fromHour <= toHour ? h >= fromHour && h < toHour : h >= fromHour || h < toHour;
    }

    public async Task<Speech?> Speak(string text, CancellationToken ct)
    {
        if (premium is not null && PremiumNow())
        {
            try
            {
                var s = await premium.Speak(text, ct);
                if (s is not null) return s;
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { Console.WriteLine($"[stimme] Premium-Stimme nicht erreichbar: {e.Message}"); }
        }
        return local is null ? null : await local.Speak(text, ct);
    }

    /// <summary>"18-22" → (18, 22); anything else → evening default.</summary>
    public static (int From, int To) ParseHours(string? s)
    {
        var parts = (s ?? "").Split('-', StringSplitOptions.TrimEntries);
        return parts.Length == 2 && int.TryParse(parts[0], out var a) && int.TryParse(parts[1], out var b) && a is >= 0 and <= 24 && b is >= 0 and <= 24
            ? (a, b) : (18, 22);
    }
}
