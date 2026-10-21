using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using SimTelemetry.Diagnostics;
using Modern = Windows.Media.SpeechSynthesis;

namespace SimTelemetry.Engineer;

/// <summary>
/// Una voz que puede usar el ingeniero: de la API clásica de Windows (SAPI, "Sabina Desktop") o de la moderna
/// (OneCore: "Raúl", "Sabina" y las que instales en Configuración → Hora e idioma → Voz).
/// </summary>
public sealed record VoiceOption(string Id, string Name, string Culture, bool IsModern)
{
    public bool Spanish => Culture.StartsWith("es", StringComparison.OrdinalIgnoreCase);

    public string Label
    {
        get
        {
            string region;
            try { region = CultureInfo.GetCultureInfo(Culture).NativeName; }
            catch (CultureNotFoundException) { region = Culture; }
            return $"{Name} · {region}" + (IsModern ? "" : " · clásica");
        }
    }
}

/// <summary>
/// Voz y pitidos del ingeniero, cada uno en su propio hilo: un pitido nunca espera detrás de una frase
/// (la voz tarda en arrancar; el pitido tiene que sonar justo en el punto de frenada).
/// </summary>
public sealed class EngineerAudio : IDisposable
{
    public enum Beep { Short, BrakeLight, BrakeMedium, BrakeHard }

    private readonly BlockingCollection<Action> _voiceQueue = new();
    private readonly BlockingCollection<(Beep Beep, int Volume, long Queued)> _beepQueue = new();
    private readonly Thread _voiceThread, _beepThread;
    private SoundPlayer[] _beeps = Array.Empty<SoundPlayer>();
    private int _volume = -1;

    // Voz clásica (SAPI).
    private System.Speech.Synthesis.SpeechSynthesizer? _sapi;
    // Voz moderna (OneCore): las frases se convierten a audio y se reproducen con MediaPlayer.
    private Modern.SpeechSynthesizer? _modern;
    private Windows.Media.Playback.MediaPlayer? _player;
    private readonly Queue<byte[]> _pending = new();
    private bool _playing;
    private ConcurrentDictionary<string, byte[]> _cache = new();

    private VoiceOption? _current;
    private double _rate = 1.25, _pitch = 1.0;

    public EngineerAudio(int volume, string voiceId, int ratePercent, int pitchPercent)
    {
        BuildBeeps(volume); // listos desde el principio: el primer pitido no espera a generarse
        _voiceThread = new Thread(RunVoice) { IsBackground = true, Name = "Ingeniero (voz)" };
        _beepThread = new Thread(RunBeeps) { IsBackground = true, Name = "Ingeniero (pitidos)", Priority = ThreadPriority.Highest };
        _voiceThread.Start();
        _beepThread.Start();
        Configure(voiceId, ratePercent, pitchPercent);
    }

    /// <summary>Voces instaladas: primero las de español (modernas antes que clásicas).</summary>
    public IReadOnlyList<VoiceOption> Voices { get; private set; } = Array.Empty<VoiceOption>();
    public VoiceOption? Current => _current;
    /// <summary>Nombre de la voz elegida (vacío si Windows no tiene voces).</summary>
    public string VoiceName => _current?.Name ?? "";
    public bool VoiceIsSpanish => _current?.Spanish == true;
    public event Action? VoicesChanged;

    /// <summary>Elige la voz ("" = la primera en español), la velocidad (100 = normal) y el tono (sólo voces modernas).</summary>
    public void Configure(string voiceId, int ratePercent, int pitchPercent) => _voiceQueue.Add(() =>
    {
        _rate = Math.Clamp(ratePercent, 50, 300) / 100.0;
        _pitch = Math.Clamp(pitchPercent, 50, 150) / 100.0;
        Voices = ListVoices();
        _current = Voices.FirstOrDefault(v => v.Id == voiceId) ?? Voices.FirstOrDefault(v => v.Spanish && !v.IsModern)
                   ?? Voices.FirstOrDefault(v => v.Spanish) ?? Voices.FirstOrDefault();
        _cache = new ConcurrentDictionary<string, byte[]>(); // otra voz u otra velocidad: las frases preparadas ya no valen
        StopSpeaking();
        try
        {
            if (_current is { IsModern: true } modern)
            {
                _modern ??= new Modern.SpeechSynthesizer();
                _modern.Voice = Modern.SpeechSynthesizer.AllVoices.First(v => "onecore:" + v.Id == modern.Id);
                _modern.Options.SpeakingRate = _rate;
                _modern.Options.AudioPitch = _pitch;
                if (_player == null)
                {
                    _player = new Windows.Media.Playback.MediaPlayer { AudioCategory = Windows.Media.Playback.MediaPlayerAudioCategory.Speech };
                    _player.MediaEnded += (_, _) => _voiceQueue.Add(PlayNext);
                    _player.MediaFailed += (_, e) => { Log.Error("Ingeniero: no se pudo reproducir la voz: " + e.ErrorMessage); _voiceQueue.Add(PlayNext); };
                }
            }
            else if (_current != null)
            {
                _sapi ??= new System.Speech.Synthesis.SpeechSynthesizer();
                _sapi.SetOutputToDefaultAudioDevice();
                _sapi.SelectVoice(_current.Id["sapi:".Length..]);
                _sapi.Rate = SapiRate(_rate);
            }
            Log.Info($"Ingeniero: voz '{_current?.Label ?? "ninguna"}' · velocidad {_rate:P0}" + (_current?.IsModern == true ? $" · tono {_pitch:P0}" : ""));
        }
        catch (Exception ex)
        {
            Log.Error("Ingeniero: no se pudo preparar la voz", ex);
            _current = null;
        }
        VoicesChanged?.Invoke();
    });

    /// <summary>Dice una frase. Con <paramref name="urgent"/> corta lo que se esté diciendo (avisos de curva).</summary>
    public void Say(string text, int volume, bool urgent = false) => _voiceQueue.Add(() =>
    {
        try
        {
            if (_current is { IsModern: true })
            {
                byte[] audio = _cache.TryGetValue(text, out var cached) ? cached : Synthesize(_modern!, text);
                if (urgent) StopSpeaking();
                _player!.Volume = Math.Clamp(volume, 0, 100) / 100.0;
                if (_playing) _pending.Enqueue(audio);
                else PlayNow(audio);
            }
            else if (_sapi != null && _current != null)
            {
                if (urgent) _sapi.SpeakAsyncCancelAll();
                _sapi.Volume = Math.Clamp(volume, 0, 100);
                _sapi.SpeakAsync(text);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Ingeniero: error al hablar", ex);
        }
    });

    /// <summary>
    /// Prepara de antemano el audio de frases que se van a decir (los avisos de cada curva): al llegar a la curva sólo hay
    /// que reproducirlo, sin esperar a la síntesis. Sólo con voces modernas (la clásica habla directamente).
    /// </summary>
    public void Prepare(IEnumerable<string> phrases)
    {
        var voice = _current;
        var cache = _cache;
        if (voice is not { IsModern: true }) return;
        var list = phrases.Distinct().Where(p => !cache.ContainsKey(p)).ToList();
        double rate = _rate, pitch = _pitch;
        Task.Run(() =>
        {
            try
            {
                // Sintetizador propio: preparar frases nunca retrasa lo que se está diciendo.
                using var synth = new Modern.SpeechSynthesizer();
                synth.Voice = Modern.SpeechSynthesizer.AllVoices.First(v => "onecore:" + v.Id == voice.Id);
                synth.Options.SpeakingRate = rate;
                synth.Options.AudioPitch = pitch;
                foreach (var p in list) cache.TryAdd(p, Synthesize(synth, p));
            }
            catch (Exception ex)
            {
                Log.Error("Ingeniero: no se pudieron preparar los avisos", ex);
            }
        });
    }

    public void Play(Beep beep, int volume) => _beepQueue.Add((beep, volume, Stopwatch.GetTimestamp()));

    /// <summary>
    /// Segundos que tarda la voz en decir una frase. Si ya está preparada (voz moderna), la duración exacta del audio;
    /// si no, una estimación según la velocidad elegida, más lo que tarda en arrancar.
    /// </summary>
    public double SpeechSeconds(string text) =>
        _current is { IsModern: true } && _cache.TryGetValue(text, out var wav) ? 0.1 + WavSeconds(wav) : EstimateSeconds(text, _rate);

    /// <summary>
    /// Estimación medida con Sabina y Raúl a velocidad 125 %: 0,07 s por letra, 0,08 s por coma y 0,35 s por punto o
    /// dos puntos (pausas de la voz), más ~0,4 s en arrancar y decir la primera sílaba.
    /// </summary>
    public static double EstimateSeconds(string text, double rate)
    {
        int commas = text.Count(c => c == ',');
        int stops = text.Count(c => c is '.' or ':' or ';' or '!' or '?');
        return 0.4 + (text.Length * 0.07 + commas * 0.08 + stops * 0.35) * 1.25 / rate;
    }

    /// <summary>Duración de un WAV PCM (cabecera RIFF: bytes por segundo y tamaño del bloque "data").</summary>
    public static double WavSeconds(byte[] wav)
    {
        if (wav.Length < 44) return 0;
        int byteRate = BitConverter.ToInt32(wav, 28);
        for (int i = 12; i + 8 <= wav.Length;)
        {
            int size = BitConverter.ToInt32(wav, i + 4);
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a')
                return byteRate > 0 ? Math.Min(size, wav.Length - i - 8) / (double)byteRate : 0;
            i += 8 + Math.Max(0, size);
        }
        return 0;
    }

    /// <summary>Velocidad de SAPI (-10..10, cada 10 puntos el triple) para un factor de velocidad (1 = normal).</summary>
    public static int SapiRate(double factor) => (int)Math.Clamp(Math.Round(10 * Math.Log(factor) / Math.Log(3)), -10, 10);

    private static List<VoiceOption> ListVoices()
    {
        var voices = new List<VoiceOption>();
        try
        {
            foreach (var v in Modern.SpeechSynthesizer.AllVoices)
                voices.Add(new VoiceOption("onecore:" + v.Id, v.DisplayName.Replace("Microsoft ", "").Replace("Raul", "Raúl"), v.Language, true));
        }
        catch (Exception ex) { Log.Error("Ingeniero: no se pudieron leer las voces modernas", ex); }
        try
        {
            using var sapi = new System.Speech.Synthesis.SpeechSynthesizer();
            foreach (var v in sapi.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo))
                voices.Add(new VoiceOption("sapi:" + v.Name, v.Name.Replace("Microsoft ", "").Replace(" Desktop", ""), v.Culture.Name, false));
        }
        catch (Exception ex) { Log.Error("Ingeniero: no se pudieron leer las voces clásicas", ex); }
        return voices.OrderBy(v => v.Spanish ? 0 : 1).ThenBy(v => v.IsModern ? 0 : 1).ThenBy(v => v.Name).ToList();
    }

    /// <summary>Texto → audio WAV con una voz moderna, sin el silencio del principio y del final.</summary>
    public static byte[] Synthesize(Modern.SpeechSynthesizer synth, string text)
    {
        using var stream = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
        using var input = stream.AsStreamForRead();
        using var ms = new MemoryStream();
        input.CopyTo(ms);
        return TrimSilence(ms.ToArray());
    }

    /// <summary>
    /// Quita el silencio del principio y del final de un WAV PCM de 16 bits: la frase empieza a sonar antes
    /// y su duración es la real (con ella se decide cuándo empezar a decirla para que acabe antes del primer pitido).
    /// </summary>
    public static byte[] TrimSilence(byte[] wav)
    {
        if (wav.Length < 44 || BitConverter.ToInt16(wav, 34) != 16) return wav;
        int channels = Math.Max(1, (int)BitConverter.ToInt16(wav, 22));
        int byteRate = BitConverter.ToInt32(wav, 28);
        int data = -1, size = 0;
        for (int i = 12; i + 8 <= wav.Length;)
        {
            int chunk = BitConverter.ToInt32(wav, i + 4);
            if (wav[i] == 'd' && wav[i + 1] == 'a' && wav[i + 2] == 't' && wav[i + 3] == 'a') { data = i + 8; size = Math.Min(chunk, wav.Length - data); break; }
            i += 8 + Math.Max(0, chunk);
        }
        if (data < 0 || byteRate <= 0) return wav;
        int frame = 2 * channels;
        const int threshold = 400; // ~ -38 dB
        int first = 0, last = size / frame - 1;
        while (first < last && Math.Abs((int)BitConverter.ToInt16(wav, data + first * frame)) < threshold) first++;
        while (last > first && Math.Abs((int)BitConverter.ToInt16(wav, data + last * frame)) < threshold) last--;
        int keep = byteRate / frame / 20; // 50 ms de margen a cada lado: que no suene cortado
        first = Math.Max(0, first - keep);
        last = Math.Min(size / frame - 1, last + keep);
        int bytes = (last - first + 1) * frame;
        if (bytes <= 0) return wav;
        var result = new byte[data + bytes];
        Array.Copy(wav, result, data);
        Array.Copy(wav, data + first * frame, result, data, bytes);
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        BitConverter.GetBytes(bytes).CopyTo(result, data - 4);
        return result;
    }

    private void PlayNow(byte[] wav)
    {
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        // DataWriter + DetachStream: escribir el audio sin cerrar el flujo (cerrar el envoltorio .NET lo cerraba también).
        var writer = new Windows.Storage.Streams.DataWriter(stream);
        writer.WriteBytes(wav);
        writer.StoreAsync().AsTask().GetAwaiter().GetResult();
        writer.DetachStream();
        stream.Seek(0);
        _player!.Source = Windows.Media.Core.MediaSource.CreateFromStream(stream, "audio/wav");
        _playing = true;
        _player.Play();
    }

    private void PlayNext()
    {
        if (_pending.Count > 0) PlayNow(_pending.Dequeue());
        else _playing = false;
    }

    private void StopSpeaking()
    {
        _pending.Clear();
        if (_player != null && _playing)
        {
            _player.Pause();
            _player.Source = null;
        }
        _playing = false;
        _sapi?.SpeakAsyncCancelAll();
    }

    private void BuildBeeps(int volume)
    {
        foreach (var b in _beeps) b.Dispose();
        double amp = Math.Clamp(volume, 0, 100) / 100.0;
        _beeps = new[]
        {
            Player(1046, 0.07, amp),  // cuenta atrás
            Player(784, 0.16, amp),   // frena suave
            Player(1318, 0.20, amp),  // frena medio
            Player(1760, 0.30, amp),  // frena fuerte
        };
        _volume = volume;
    }

    private static SoundPlayer Player(double freq, double seconds, double amp)
    {
        var p = new SoundPlayer(new MemoryStream(Tone(freq, seconds, amp)));
        p.Load();
        return p;
    }

    /// <summary>WAV PCM 16 bits mono con un tono senoidal (con rampa para que no chasquee).</summary>
    public static byte[] Tone(double freq, double seconds, double amp)
    {
        const int rate = 22050;
        int n = (int)(rate * seconds);
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1);
        w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        int ramp = rate / 200; // 5 ms
        for (int i = 0; i < n; i++)
        {
            double env = Math.Min(1, Math.Min(i, n - 1 - i) / (double)ramp);
            w.Write((short)(Math.Sin(2 * Math.PI * freq * i / rate) * amp * env * short.MaxValue * 0.8));
        }
        w.Flush();
        return ms.ToArray();
    }

    private void RunVoice()
    {
        foreach (var action in _voiceQueue.GetConsumingEnumerable())
        {
            try { action(); }
            catch (Exception ex) { Log.Error("Ingeniero: error de voz", ex); }
        }
    }

    private void RunBeeps()
    {
        foreach (var (beep, volume, queued) in _beepQueue.GetConsumingEnumerable())
        {
            try
            {
                if (_volume != volume) BuildBeeps(volume);
                _beeps[(int)beep].Play(); // asíncrono: no bloquea
#if DEBUG
                double waitMs = (Stopwatch.GetTimestamp() - queued) * 1000.0 / Stopwatch.Frequency;
                if (waitMs > 5) Log.Info($"Ingeniero[debug]: pitido {beep} esperó {waitMs:0} ms en la cola");
#endif
            }
            catch (Exception ex) { Log.Error("Ingeniero: error de pitido", ex); }
        }
    }

    public void Dispose()
    {
        _voiceQueue.CompleteAdding();
        _beepQueue.CompleteAdding();
        _voiceThread.Join(500);
        _beepThread.Join(500);
        _sapi?.Dispose();
        _modern?.Dispose();
        _player?.Dispose();
        foreach (var b in _beeps) b.Dispose();
    }
}
