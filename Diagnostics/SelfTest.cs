#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SimTelemetry.Analysis;
using SimTelemetry.Engineer;
using SimTelemetry.Recording;
using SimTelemetry.Tracks;

namespace SimTelemetry.Diagnostics;

/// <summary>Sólo en Debug: comprobaciones rápidas de la lógica sin interfaz (SimTelemetry.exe --selftest).</summary>
internal static class SelfTest
{
    public static int Run(string folder)
    {
        var fails = new List<string>();
        void Check(bool ok, string what) { if (!ok) fails.Add(what); }

        // Biblioteca: nombres que usan los juegos.
        Check(TrackDatabase.Find("nurburgring")?.Name == "Nürburgring GP", "ACC nurburgring → GP");
        Check(TrackDatabase.Find("nurburgring_24h")?.LengthKm > 25, "ACC nurburgring_24h → 24h");
        Check(TrackDatabase.Find("ks_nordschleife")?.Name == "Nürburgring Nordschleife", "AC ks_nordschleife");
        Check(TrackDatabase.Find("Hungaroring")?.Name == "Hungaroring", "ACC Hungaroring");
        Check(TrackDatabase.Find("spa")?.Name == "Spa-Francorchamps", "ACC spa");
        Check(TrackDatabase.Find("ks_brands_hatch")?.Name == "Brands Hatch", "AC brands hatch");
        Check(TrackDatabase.Find("Pista #42") == null, "Forza sin nombre");

        // Lectura de vueltas: puntos anteriores a la línea (5093 m y luego 0 m) pasan a negativo.
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "lap_wrap.csv");
        File.WriteAllText(path, "# game=Test\n# length=4100\ntime,distance,speed_kmh,throttle,brake,clutch,steering,handbrake,gear,rpm,lat_g,lon_g\n" +
                                "0.00,5093.2,190,1,0,0,0,0,4,8000,0,0\n0.02,0.0,190,1,0,0,0,0,4,8000,0,0\n0.04,1.1,190,1,0,0,0,0,4,8000,0,0\n");
        var lap = LapFile.Read(path);
        Check(lap.Distance[0] < 0 && lap.Distance[0] > -5 && lap.Distance[2] > 1, $"vuelta con wrap: {string.Join(", ", lap.Distance)}");

        // Ingeniero: cómo dice los tiempos.
        Check(RaceEngineer.SpeakTime(123.07) == "2, 03, 1", "SpeakTime 2:03.07 → " + RaceEngineer.SpeakTime(123.07));
        Check(RaceEngineer.SpeakDelta(0.34) == "3 décimas", "SpeakDelta 0,34");
        Check(RaceEngineer.SpeakDelta(1.26) == "1,3 segundos", "SpeakDelta 1,26 → " + RaceEngineer.SpeakDelta(1.26));
        Check(EngineerAudio.Tone(1000, 0.1, 0.5).Length == 44 + 2205 * 2, "WAV de 0,1 s");
        string call = RaceEngineer.CallText(new BrakeCue(1, 400, 500, 0.91, 2, 240, 80));
        Check(call == "Frena fuerte, 90 por ciento, segunda", "aviso de frenada → " + call);
        call = RaceEngineer.CallText(new BrakeCue(2, 900, 950, 0.32, 5, 210, 195));
        Check(call == "Toque de freno, 30 por ciento, quinta", "aviso de toque → " + call);

        // Vuelta ideal: con dos vueltas, cada mitad la mejor de las dos.
        LapData Synthetic(double firstHalf, double secondHalf)
        {
            var t = new List<double>(); var d = new List<double>();
            for (int i = 0; i <= 100; i++)
            {
                d.Add(i * 10);
                t.Add(i <= 50 ? firstHalf * i / 50 : firstHalf + secondHalf * (i - 50) / 50);
            }
            return new LapData { Time = t.ToArray(), Distance = d.ToArray() };
        }
        double ideal = TrackGuide.ComputeIdealLap(new[] { Synthetic(30, 32), Synthetic(31, 30) });
        Check(Math.Abs(ideal - 60) < 0.01, $"vuelta ideal 60 s → {ideal:0.000}");

        // Tacto de pedales: un juego que satura el freno al 80 % del recorrido.
        var feel = new Input.PedalFeelCurve();
        for (int i = 0; i <= 100; i++)
            for (int n = 0; n < 20; n++)
                Input.PedalFeel.Learn(feel, true, i / 100f, Math.Min(1f, i / 80f));
        Check(Input.PedalFeel.Ready(feel, true) && !Input.PedalFeel.Ready(feel, false), "curva lista sólo para el freno");
        Check(Math.Abs(Input.PedalFeel.Map(feel, true, 0.4f) - 0.5f) < 0.03f, $"40 % de pedal → {Input.PedalFeel.Map(feel, true, 0.4f):0.00} (se esperaba 0,50)");
        Check(Math.Abs(Input.PedalFeel.FullAt(feel, true) - 0.79f) < 0.03f, $"100 % al {Input.PedalFeel.FullAt(feel, true):0.00} del recorrido (se esperaba ~0,79)");
        Check(Input.PedalFeel.Map(feel, false, 0.4f) == 0.4f, "acelerador sin medir: tal cual");

        // Coches: categoría y nombre legible.
        Check(CarCatalog.Category("Assetto Corsa Competizione", "porsche_991ii_gt3_cup") == "Porsche Cup", "ACC Cup");
        Check(CarCatalog.Category("Assetto Corsa Competizione", "bmw_m4_gt4") == "GT4", "ACC GT4");
        Check(CarCatalog.Category("Assetto Corsa Competizione", "porsche_991ii_gt3_r") == "GT3", "ACC GT3");
        Check(CarCatalog.Category("EA SPORTS WRC", "Citroën C3 Rally2 (WRC2)") == "WRC2", "WRC clase");
        Check(CarCatalog.DisplayName("porsche_991ii_gt3_r") == "Porsche 991 II GT3 R", "nombre " + CarCatalog.DisplayName("porsche_991ii_gt3_r"));
        Check(CarCatalog.DisplayName("mercedes_amg_gt3_evo") == "Mercedes-AMG GT3 Evo", "nombre " + CarCatalog.DisplayName("mercedes_amg_gt3_evo"));
        Check(CarCatalog.DisplayName("Citroën C3 Rally2 (WRC2)") == "Citroën C3 Rally2", "nombre WRC");
        Check(TrackDatabase.Rally("Asco · Rally Mediterraneo")?.Surface == "Asfalto", "rally Mediterraneo asfalto");

        // Potencial de frenada: a 1 G constante, de 100 a 50 km/h hacen falta ~29,5 m (v² / 2a).
        var g1 = DecelModel.Constant(1);
        double ideal1 = g1.IdealDistance(100, 50, attackSeconds: 0);
        Check(Math.Abs(ideal1 - 29.5) < 0.5, $"distancia ideal 1 G 100→50 → {ideal1:0.0} m (se esperaban 29,5)");
        double pot = g1.Potential(new BrakeZone { StartDistance = 0, TurnInDistance = 60, StartSpeed = 100, TurnInSpeed = 50 });
        Check(Math.Abs(pot - (60 - 29.5 - 100 / 3.6 * 0.05)) < 0.6, $"potencial → {pot:0.0} m");
        Check(double.IsNaN(DecelModel.None.Potential(new BrakeZone { TurnInDistance = 60, StartSpeed = 100, TurnInSpeed = 50 })), "sin modelo, sin potencial");

        // Rendimiento (opcional): SIMTELEMETRY_PERF_CSV=<vuelta.csv> mide la lectura y el análisis de una vuelta real.
        if (Environment.GetEnvironmentVariable("SIMTELEMETRY_PERF_CSV") is string perf && File.Exists(perf))
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LapData last = LapFile.Read(perf);
            for (int i = 0; i < 20; i++) last = LapFile.Read(perf);
            double readMs = sw.Elapsed.TotalMilliseconds / 21;
            sw.Restart();
            for (int i = 0; i < 20; i++) LapAnalyzer.Compute(last);
            Log.Info($"SELFTEST rendimiento: leer {readMs:0.0} ms · analizar {sw.Elapsed.TotalMilliseconds / 20:0.0} ms ({last.Count} muestras)");
        }

        // Análisis por curvas: misma vuelta, pero frenando 20 m antes → debe decir "frena más tarde".
        LapData Corner(double brakeAt)
        {
            var d = new List<double>(); var v = new List<double>(); var b = new List<double>(); var st = new List<double>(); var t = new List<double>();
            double time = 0;
            for (int x = 0; x <= 1200; x++)
            {
                double speed = x < brakeAt ? 200 : x < brakeAt + 100 ? 200 - 120 * (x - brakeAt) / 100 : x < 620 ? 80 : Math.Min(200, 80 + (x - 620) * 0.4);
                if (x > 0) time += 1 / (speed / 3.6);
                d.Add(x); v.Add(speed); t.Add(time);
                b.Add(x >= brakeAt && x < brakeAt + 100 ? 1 : 0);
                st.Add(x >= 560 && x < 640 ? 0.3 : 0);
            }
            int n = d.Count;
            return new LapData
            {
                Info = new LapInfo { Game = "Test" }, Time = t.ToArray(), Distance = d.ToArray(), Speed = v.ToArray(), Brake = b.ToArray(), Steering = st.ToArray(),
                Throttle = b.Select(x => 1 - x).ToArray(), Clutch = new double[n], Handbrake = new double[n], Gear = Enumerable.Repeat(3.0, n).ToArray(),
                Rpm = new double[n], LatG = new double[n], LonG = new double[n],
            };
        }
        var refLap = Corner(500);
        var myLap = Corner(480);
        var losses = LapAnalyzer.CornerLosses(myLap, LapAnalyzer.Compute(myLap), refLap, LapAnalyzer.Compute(refLap));
        Check(losses.Count == 1 && losses[0].Loss > 0.1, $"pérdida en la curva → {(losses.Count > 0 ? losses[0].Loss : double.NaN):0.000} s");
        Check(losses.Count == 1 && losses[0].Main == Problem.BrakeEarly && losses[0].Causes.FirstOrDefault()?.Contains("20 metros antes") == true,
            "causa → " + string.Join(" | ", losses.SelectMany(l => l.Causes)));
        Check(Coaching.For(Problem.BrakeEarly).ExerciseId == "threshold", "ejercicio para frenar antes");

        // Control de calidad: una vuelta limpia pasa; con un rebobinado o un salto de posición, no.
        RecordedPoint[] Points(LapData l) => Enumerable.Range(0, l.Count).Select(i => new RecordedPoint
        {
            Time = l.Time[i], Distance = l.Distance[i], Speed = (float)l.Speed[i], PosX = float.NaN, PosZ = float.NaN,
        }).ToArray();
        var clean = Points(refLap);
        clean[0].Speed = clean[1].Speed = 200; // vuelta lanzada
        Check(LapQuality.Check(clean).Count == 0, "vuelta limpia → " + string.Join(" | ", LapQuality.Check(clean)));
        var rewound = clean.ToArray();
        for (int i = 700; i < rewound.Length; i++) rewound[i].Distance -= 150; // rebobinado de 150 m
        Check(LapQuality.Check(rewound).Any(x => x.Contains("salto atrás")), "rebobinado detectado");
        var wrongLength = clean.Select(x => { x.Distance *= 1.4; return x; }).ToArray();
        Check(LapQuality.Check(wrongLength).Any(x => x.Contains("no cuadra")), "longitud mal medida detectada");
        var standing = clean.ToArray();
        standing[0].Speed = 5;
        Check(LapQuality.Check(standing).Any(x => x.Contains("parado")) && LapQuality.Check(standing, stage: true).Count == 0, "salida desde parado (no en rally)");

        // Opcional: SIMTELEMETRY_QUALITY_DIR=<carpeta> pasa el control a vueltas reales y lo escribe en el registro.
        if (Environment.GetEnvironmentVariable("SIMTELEMETRY_QUALITY_DIR") is string qdir && Directory.Exists(qdir))
            foreach (var file in Directory.EnumerateFiles(qdir, "lap_*.csv", SearchOption.AllDirectories))
            {
                var real = LapFile.Read(file);
                var issues = LapQuality.Check(Points(real));
                Log.Info($"SELFTEST calidad: {Path.GetFileName(Path.GetDirectoryName(file))}/{Path.GetFileName(file)} " +
                         $"({(real.Info.Completed ? "completa" : "incompleta")}) → {(issues.Count == 0 ? "fiable" : string.Join(" | ", issues))}");
            }

        // Pantalla del teléfono, sólo en este PC (sin aviso del cortafuegos): página, código secreto y datos en vivo.
        using (var phone = new Remote.PhoneServer(() => "{\"thr\":0.5}", System.Net.IPAddress.Loopback))
        {
            phone.Start(18090);
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            try
            {
                string page = http.GetStringAsync(phone.Url).GetAwaiter().GetResult();
                Check(page.Contains("<title>SimTelemetry</title>"), "teléfono: página");
                var wrong = http.GetAsync($"http://127.0.0.1:{phone.Port}/otro-codigo").GetAwaiter().GetResult();
                Check(wrong.StatusCode == System.Net.HttpStatusCode.NotFound, $"teléfono: sin código → {(int)wrong.StatusCode}");
                using var stream = http.GetStreamAsync(phone.Url + "/stream").GetAwaiter().GetResult();
                using var reader = new StreamReader(stream);
                string? line = reader.ReadLine();
                Check(line == "data: {\"thr\":0.5}", "teléfono: datos en vivo → " + line);
            }
            catch (Exception ex)
            {
                fails.Add("teléfono: " + ex.Message);
            }
        }

        // Perfiles de juego: cada nombre a su perfil, y la biblioteca de ACC/AC sin erratas (24 y 12 circuitos).
        Check(Telemetry.GameProfiles.For("Assetto Corsa Competizione") == Telemetry.GameProfiles.Acc, "perfil ACC");
        Check(Telemetry.GameProfiles.For("Assetto Corsa") == Telemetry.GameProfiles.Ac, "perfil AC");
        Check(Telemetry.GameProfiles.For("DiRT Rally / DiRT Rally 2.0") == Telemetry.GameProfiles.Dirt, "perfil DiRT");
        Check(Telemetry.GameProfiles.For("EA SPORTS WRC") == Telemetry.GameProfiles.Wrc, "perfil WRC");
        Check(Telemetry.GameProfiles.For("Forza Motorsport (2023)") == Telemetry.GameProfiles.Forza, "perfil Forza");
        Check(TrackDatabase.All.Count(t => TrackDatabase.InGame(t, Telemetry.GameProfiles.Acc)) == 24, "biblioteca ACC: 24 circuitos");
        Check(TrackDatabase.All.Count(t => TrackDatabase.InGame(t, Telemetry.GameProfiles.Ac)) == 12, "biblioteca AC: 12 circuitos");
        Check(!TrackDatabase.All.Any(t => TrackDatabase.InGame(t, Telemetry.GameProfiles.Wrc)), "rally sin biblioteca de circuitos");
        Check(RaceEngineer.RallyCallText(new BrakeCue(1, 400, 500, 0.91, 2, 240, 80)) == "Fuerte 90", "aviso corto de rally");

        // Voces: la velocidad clásica equivalente y que las voces modernas existen y generan audio (sin reproducirlo).
        Check(EngineerAudio.SapiRate(1.25) == 2 && EngineerAudio.SapiRate(1.0) == 0, "velocidad de voz clásica");
        try
        {
            var modern = Windows.Media.SpeechSynthesis.SpeechSynthesizer.AllVoices;
            Log.Info("SELFTEST voces modernas: " + string.Join(", ", modern.Select(v => $"{v.DisplayName} ({v.Language})")));
            var spanish = modern.FirstOrDefault(v => v.Language.StartsWith("es"));
            if (spanish != null)
            {
                using var synth = new Windows.Media.SpeechSynthesis.SpeechSynthesizer { Voice = spanish };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var stream = synth.SynthesizeTextToStreamAsync("Frena fuerte, 90 por ciento, segunda").AsTask().GetAwaiter().GetResult();
                Log.Info($"SELFTEST voz {spanish.DisplayName}: {stream.Size / 1024} KB de audio en {sw.ElapsedMilliseconds} ms");
                Check(stream.Size > 10_000, "la voz moderna genera audio");
                // Duración real de los avisos frente a la estimación (para la sincronía de las frases).
                synth.Options.SpeakingRate = 1.25;
                foreach (var text in new[] { "Frena fuerte, 90 por ciento, segunda", "Curva 2: frena más tarde. Frena fuerte, 95 por ciento, tercera", "Fuerte 90" })
                {
                    using var s = synth.SynthesizeTextToStreamAsync(text).AsTask().GetAwaiter().GetResult();
                    using var input = s.AsStreamForRead();
                    using var ms = new MemoryStream();
                    input.CopyTo(ms);
                    double real = EngineerAudio.WavSeconds(ms.ToArray());
                    double trimmed = EngineerAudio.WavSeconds(EngineerAudio.TrimSilence(ms.ToArray()));
                    Log.Info($"SELFTEST moderna «{text}»: dura {real:0.00} s, sin silencios {trimmed:0.00} s");
                    Check(trimmed > 0.3 && trimmed <= real, "recorte de silencios");
                }
                // Voz clásica (SAPI): duración real para calibrar la estimación.
                using var sapi = new System.Speech.Synthesis.SpeechSynthesizer { Rate = EngineerAudio.SapiRate(1.25) };
                foreach (var text in new[] { "Frena fuerte, 90 por ciento, segunda", "Curva 2: frena más tarde. Frena fuerte, 95 por ciento, tercera", "Fuerte 90" })
                {
                    using var ms = new MemoryStream();
                    sapi.SetOutputToWaveStream(ms);
                    sapi.Speak(text);
                    double trimmed = EngineerAudio.WavSeconds(EngineerAudio.TrimSilence(ms.ToArray()));
                    Log.Info($"SELFTEST clásica «{text}»: sin silencios {trimmed:0.00} s · estimación {EngineerAudio.EstimateSeconds(text, 1.25):0.00} s");
                    Check(Math.Abs(EngineerAudio.EstimateSeconds(text, 1.25) - 0.3 - trimmed) < 0.5, $"estimación de «{text}» (±0,5 s)");
                }
            }
        }
        catch (Exception ex)
        {
            fails.Add("voces modernas: " + ex.Message);
        }

        // Temas: con cualquier color y en oscuro o claro, el texto y los datos se leen (WCAG: 4,5:1 texto, 3:1 gráficos).
        foreach (var theme in new[] { "dark", "light" })
            foreach (var (name, hex) in UI.Palette.Choices)
            {
                UI.Palette.Apply(theme, hex);
                string id = $"tema {theme} {name}";
                void Min(double ratio, System.Windows.Media.Color fore, string what, params System.Windows.Media.Color[] backs)
                {
                    foreach (var b in backs)
                        Check(UI.Palette.Contrast(fore, b) >= ratio, $"{id}: {what} {UI.Palette.Contrast(fore, b):0.0}:1 sobre {b}");
                }
                Min(4.5, UI.Palette.Text, "texto", UI.Palette.Bg, UI.Palette.Panel, UI.Palette.PanelAlt, UI.Palette.Hover, UI.Palette.Pressed, UI.Palette.AccentSoft, UI.Palette.AccentTint);
                Min(4.5, UI.Palette.Dim, "texto tenue", UI.Palette.Bg, UI.Palette.Panel, UI.Palette.PanelAlt, UI.Palette.ListHover);
                Min(3, UI.Palette.Off, "punto apagado", UI.Palette.PanelAlt);
                Min(4.5, UI.Palette.Accent, "acento", UI.Palette.Bg, UI.Palette.Panel, UI.Palette.PanelAlt);
                foreach (var fill in new[] { UI.Palette.Accent, UI.Palette.Good, UI.Palette.Warn, UI.Palette.Bad })
                    Min(4.5, UI.Palette.OnFill, "texto sobre relleno", fill);
                foreach (var c in new[] { UI.Palette.Good, UI.Palette.Bad, UI.Palette.Warn, UI.Palette.Info, UI.Palette.Reference })
                    Min(4.5, c, "color de estado", UI.Palette.Panel);
                foreach (var c in new[] { UI.Palette.LapMine, UI.Palette.Reference, UI.Palette.Throttle, UI.Palette.Brake, UI.Palette.Clutch, UI.Palette.Steering, UI.Palette.Gear, UI.Palette.Info, UI.Palette.Warn })
                    Min(3, c, "serie de la gráfica", UI.Palette.ChartPane, UI.Palette.ChartBg);
                Min(4.5, UI.Palette.ChartText, "texto de la gráfica", UI.Palette.ChartBg, UI.Palette.ChartPane);
                Min(4.5, UI.Palette.ChartDim, "eje de la gráfica", UI.Palette.ChartBg, UI.Palette.ChartPane);
            }
        Log.Info($"SELFTEST temas: {UI.Palette.Choices.Length * 2} combinaciones de tema y color comprobadas");
        UI.Palette.Apply("dark", "");
        Check(UI.TelemetryChart.AxisStep(296, 6) == 100 && UI.TelemetryChart.AxisStep(210, 3) == 100 && UI.TelemetryChart.AxisStep(6.96, 2) == 5,
            "marcas del eje: 100 km/h, 100 % de volante, marcha de 5 en 5");

        foreach (var f in fails) Log.Error("SELFTEST: " + f);
        Log.Info(fails.Count == 0 ? "SELFTEST: todo correcto" : $"SELFTEST: {fails.Count} fallos");
        return fails.Count;
    }
}
#endif
