using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

var app = new NoosphereSoundpad();
return app.Run(args);

internal sealed class NoosphereSoundpad
{
    private const int SampleRate = 32000;
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmSysKeyDown = 0x0104;
    private const int VkMenu = 0x12;
    private const uint SoundAsync = 0x0001;
    private const uint SoundFilename = 0x00020000;
    private const uint SoundSync = 0x0000;
    private const uint PmRemove = 0x0001;

    private const string LetterOrder = "abcdefghijklmn\u00f1opqrstuvwxyz";
    private const string SymbolOrder = "!\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
    private const string SpanishExtras =
        "\u00f1\u00d1\u00e7\u00c7\u00e1\u00e9\u00ed\u00f3\u00fa\u00c1\u00c9\u00cd\u00d3\u00da\u00fc\u00dc\u00a1\u00bf\u00ba\u00aa";

    private static readonly HashSet<string> AllowedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "WindowsTerminal",
        "wt",
        "OpenConsole",
        "conhost",
        "cmd",
        "powershell",
        "pwsh",
    };

    private static readonly HashSet<string> AllowedWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "ConsoleWindowClass",
    };

    private static readonly string CacheDir = Path.Combine(AppContext.BaseDirectory, ".cache");

    private static readonly Dictionary<char, double[]> VowelFormants = new()
    {
        ['a'] = [800, 1150, 2900],
        ['e'] = [400, 1700, 2600],
        ['i'] = [300, 2200, 3000],
        ['o'] = [450, 800, 2830],
        ['u'] = [325, 700, 2530],
        ['\u00e1'] = [820, 1180, 2920],
        ['\u00e9'] = [420, 1720, 2620],
        ['\u00ed'] = [320, 2220, 3020],
        ['\u00f3'] = [470, 830, 2850],
        ['\u00fa'] = [340, 720, 2550],
        ['\u00fc'] = [350, 760, 2580],
    };

    private static readonly Dictionary<char, string> SpanishNames = new()
    {
        ['\u00f1'] = "enye",
        ['\u00d1'] = "enye upper",
        ['\u00e7'] = "ce trencada",
        ['\u00c7'] = "ce trencada upper",
        ['\u00e1'] = "a acute",
        ['\u00e9'] = "e acute",
        ['\u00ed'] = "i acute",
        ['\u00f3'] = "o acute",
        ['\u00fa'] = "u acute",
        ['\u00c1'] = "a acute upper",
        ['\u00c9'] = "e acute upper",
        ['\u00cd'] = "i acute upper",
        ['\u00d3'] = "o acute upper",
        ['\u00da'] = "u acute upper",
        ['\u00fc'] = "u diaeresis",
        ['\u00dc'] = "u diaeresis upper",
        ['\u00a1'] = "opening exclamation",
        ['\u00bf'] = "opening question",
        ['\u00ba'] = "ordinal masculine",
        ['\u00aa'] = "ordinal feminine",
    };

    private readonly HookProc _hookProc;
    private readonly object _playLock = new();

    private bool _enabled = true;
    private nint _hookHandle;

    public NoosphereSoundpad()
    {
        _hookProc = KeyboardHookProc;
    }

    public int Run(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Directory.CreateDirectory(CacheDir);

        if (args.Length >= 2 && args[0] == "--emit")
        {
            var key = args[1][0];
            var result = EnsureAndPlay(key, playAsync: false);
            Console.WriteLine(Describe(result));
            Console.WriteLine(result.Path);
            return 0;
        }

        if (args.Length == 1 && args[0] == "--warm-cache")
        {
            foreach (var key in WarmCharacters())
            {
                EnsureWave(key);
            }

            Console.WriteLine($"Warmed cache in {CacheDir}");
            return 0;
        }

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            UninstallHook();
            Environment.Exit(0);
        };

        PrintBanner();
        InstallHook();

        try
        {
            MessageLoop();
            return 0;
        }
        finally
        {
            UninstallHook();
        }
    }

    private void InstallHook()
    {
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
        if (_hookHandle == 0)
        {
            throw new InvalidOperationException($"Failed to install keyboard hook. Win32 error {Marshal.GetLastWin32Error()}.");
        }
    }

    private void UninstallHook()
    {
        if (_hookHandle != 0)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = 0;
        }
    }

    private static void MessageLoop()
    {
        while (true)
        {
            while (PeekMessage(out var message, 0, 0, 0, PmRemove))
            {
                if (message.message == 0x0012)
                {
                    return;
                }

                TranslateMessage(in message);
                DispatchMessage(in message);
            }

            Thread.Sleep(10);
        }
    }

    private nint KeyboardHookProc(int nCode, nuint wParam, nint lParam)
    {
        if (nCode < 0)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        var msg = unchecked((int)wParam);
        if (msg is not WmKeyDown and not WmSysKeyDown)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!TryReadKbdStruct(lParam, out var kb))
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!IsTerminalForeground())
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (IsToggleChord(kb.vkCode))
        {
            _enabled = !_enabled;
            Console.WriteLine(_enabled ? "[sound on]" : "[sound off]");
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (!_enabled)
        {
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        if (TryTranslateKey(kb.vkCode, kb.scanCode, msg == WmSysKeyDown, out var ch) && ShouldSound(ch))
        {
            var result = EnsureAndPlay(ch, playAsync: true);
            Console.WriteLine(Describe(result));
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private static bool TryReadKbdStruct(nint lParam, out KbdLlHookStruct kb)
    {
        try
        {
            kb = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            return true;
        }
        catch
        {
            kb = default;
            return false;
        }
    }

    private static bool IsToggleChord(uint vkCode)
    {
        return vkCode == 'N' && (GetAsyncKeyState(VkMenu) & 0x8000) != 0;
    }

    private static bool ShouldSound(char ch)
    {
        return !char.IsControl(ch);
    }

    private static bool IsTerminalForeground()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0)
        {
            return false;
        }

        var className = new StringBuilder(128);
        _ = GetClassName(hwnd, className, className.Capacity);
        if (AllowedWindowClasses.Contains(className.ToString()))
        {
            return true;
        }

        _ = GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return AllowedProcesses.Contains(process.ProcessName);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryTranslateKey(uint vkCode, uint scanCode, bool isSysKey, out char ch)
    {
        ch = default;
        var keyboardState = new byte[256];
        if (!GetKeyboardState(keyboardState))
        {
            return false;
        }

        if ((GetAsyncKeyState(VkMenu) & 0x8000) != 0 || isSysKey)
        {
            keyboardState[VkMenu] |= 0x80;
        }

        if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
        {
            keyboardState[0x10] |= 0x80;
        }

        if ((GetAsyncKeyState(0x11) & 0x8000) != 0)
        {
            keyboardState[0x11] |= 0x80;
        }

        keyboardState[(int)vkCode] |= 0x80;

        var buffer = new char[8];
        var layout = GetKeyboardLayout(0);
        var count = ToUnicodeEx(vkCode, scanCode, keyboardState, buffer, buffer.Length, 0, layout);
        if (count <= 0)
        {
            return false;
        }

        ch = buffer[0];
        return true;
    }

    private static void PrintBanner()
    {
        Console.WriteLine("Terminal Noosphere Soundpad");
        Console.WriteLine("Mechanicus cathedral bank with richer reverb");
        Console.WriteLine("Background watcher: only reacts when a terminal window is foreground");
        Console.WriteLine("Letters: organ glyphs | vowels: choir vowels | numbers: bell-organ numerals");
        Console.WriteLine("Spanish keys: \\u00f1 \\u00d1 \\u00e7 \\u00c7 \\u00e1 \\u00e9 \\u00ed \\u00f3 \\u00fa \\u00c1 \\u00c9 \\u00cd \\u00d3 \\u00da \\u00fc \\u00dc \\u00a1 \\u00bf \\u00ba \\u00aa");
        Console.WriteLine("ASCII symbols: common punctuation mapped to ritual accents");
        Console.WriteLine("Alt+N toggles sound | Ctrl+C exits");
        Console.WriteLine();
    }

    private PlaybackResult EnsureAndPlay(char ch, bool playAsync)
    {
        var path = EnsureWave(ch);
        var profile = BuildProfile(ch);
        var flags = (playAsync ? SoundAsync : SoundSync) | SoundFilename;
        lock (_playLock)
        {
            PlaySound(path, 0, flags);
        }

        return new PlaybackResult(ch, profile.Label, profile.Frequency, path);
    }

    private static IEnumerable<char> WarmCharacters()
    {
        const string ascii = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 !\"#$%&'()*+,-./:;<=>?@[\\]^_`{|}~";
        return (ascii + SpanishExtras).Distinct();
    }

    private static string EnsureWave(char ch)
    {
        var path = WavePath(ch);
        if (File.Exists(path))
        {
            return path;
        }

        var profile = BuildProfile(ch);
        var samples = Synthesize(profile);
        WriteWave(path, samples);
        return path;
    }

    private static string WavePath(char ch) => Path.Combine(CacheDir, $"u{(int)ch:x4}.wav");

    private static string Describe(PlaybackResult result)
    {
        return $"{Printable(result.Character)} -> {result.Label} @ {result.Frequency:F1} Hz [{Path.GetFileName(result.Path)}]";
    }

    private static string Printable(char ch)
    {
        return SpanishNames.TryGetValue(ch, out var name) ? name : ch switch
        {
            ' ' => "space",
            '\t' => "tab",
            _ => ch.ToString(),
        };
    }

    private static KeyProfile BuildProfile(char input)
    {
        var lower = char.ToLowerInvariant(input);
        if (char.IsLetter(input))
        {
            return BuildLetterProfile(input, lower);
        }

        if (char.IsDigit(input))
        {
            return BuildDigitProfile(input);
        }

        return BuildSymbolProfile(input);
    }

    private static KeyProfile BuildLetterProfile(char original, char lower)
    {
        var index = LetterOrder.IndexOf(lower);
        if (index < 0)
        {
            index = Math.Abs(lower) % LetterOrder.Length;
        }

        var octaveLift = index / 7;
        var scale = new[] { 0, 2, 3, 5, 7, 8, 10 };
        var semitone = scale[index % scale.Length] + (octaveLift * 12);
        var baseMidi = char.IsUpper(original) ? 62 : 55;
        var freq = MidiToHz(baseMidi + semitone);
        var isVowel = VowelFormants.ContainsKey(lower);
        var isAccentedVowel = "\u00e1\u00e9\u00ed\u00f3\u00fa\u00fc".Contains(lower);
        var label = isVowel
            ? (isAccentedVowel ? "accented choir vowel" : "choir vowel")
            : (lower == '\u00f1' ? "enye organ glyph" : "organ consonant");

        return new KeyProfile(
            original,
            freq,
            1.05,
            isVowel ? 0.28 : 0.18,
            0.42,
            isVowel ? 0.90 : 0.76,
            label,
            KeyFamily.VowelLetter,
            lower);
    }

    private static KeyProfile BuildDigitProfile(char ch)
    {
        var digit = ch - '0';
        var scale = new[] { 0, 2, 3, 5, 7, 8, 10, 12, 14, 15 };
        var freq = MidiToHz(36 + scale[digit]);
        return new KeyProfile(ch, freq, 0.88, 0.01, 0.44, 0.92, "cathedral numeral", KeyFamily.Digit, ch);
    }

    private static KeyProfile BuildSymbolProfile(char ch)
    {
        if (ch == ' ')
        {
            return new KeyProfile(ch, MidiToHz(45), 0.36, 0.005, 0.18, 0.55, "rest pulse", KeyFamily.Space, ch);
        }

        const string lowerPunctuation = ",.;:'\"";
        const string sharpPunctuation = "!?\u00a1\u00bf";
        const string bracketPunctuation = "()[]{}<>";
        const string slashPunctuation = "/\\|-_";
        const string mathPunctuation = "+*=#%&$@^~`";

        var idx = SymbolOrder.IndexOf(ch);
        if (idx < 0)
        {
            idx = ((int)ch) & 15;
        }

        var freq = MidiToHz(47 + (idx % 10));
        var family = KeyFamily.SymbolAccent;
        var label = "ritual accent";
        var duration = 0.52;

        if (lowerPunctuation.Contains(ch))
        {
            family = KeyFamily.SymbolWhisper;
            label = "scribe whisper";
            duration = 0.42;
        }
        else if (sharpPunctuation.Contains(ch))
        {
            family = KeyFamily.SymbolStrike;
            label = "alarm strike";
            duration = 0.58;
        }
        else if (bracketPunctuation.Contains(ch))
        {
            family = KeyFamily.SymbolGate;
            label = "gate clang";
            duration = 0.66;
        }
        else if (slashPunctuation.Contains(ch))
        {
            family = KeyFamily.SymbolSweep;
            label = "servo sweep";
            duration = 0.48;
        }
        else if (mathPunctuation.Contains(ch))
        {
            family = KeyFamily.SymbolSeal;
            label = "seal strike";
            duration = 0.62;
        }

        if (ch is '\u00ba' or '\u00aa')
        {
            family = KeyFamily.SymbolSeal;
            label = "ordinal seal";
            freq = MidiToHz(59);
        }

        return new KeyProfile(ch, freq, duration, 0.008, 0.24, 0.72, label, family, ch);
    }

    private static short[] Synthesize(KeyProfile profile)
    {
        var frameCount = (int)(SampleRate * profile.DurationSeconds);
        var dry = new float[frameCount];
        var rng = new Random(profile.Source + ((int)profile.Frequency));

        for (var i = 0; i < frameCount; i++)
        {
            var t = i / (double)SampleRate;
            var env = Envelope(t, profile.DurationSeconds, profile.AttackSeconds, profile.ReleaseSeconds);
            var vibrato = 1.0 + (0.004 * Math.Sin(2.0 * Math.PI * 5.3 * t));
            var sample = profile.Family switch
            {
                KeyFamily.VowelLetter => VowelSignal(profile, t, vibrato),
                KeyFamily.Digit => DigitSignal(profile, t, vibrato, rng),
                KeyFamily.SymbolWhisper => WhisperSignal(profile, t, rng),
                KeyFamily.SymbolStrike => StrikeSignal(profile, t, rng),
                KeyFamily.SymbolGate => GateSignal(profile, t),
                KeyFamily.SymbolSweep => SweepSignal(profile, t),
                KeyFamily.SymbolSeal => SealSignal(profile, t),
                KeyFamily.Space => SpaceSignal(profile, t),
                _ => AccentSignal(profile, t),
            };

            dry[i] = (float)(sample * env * profile.Gain);
        }

        var wet = ApplyCathedralReverb(dry);
        var pcm = new short[wet.Length];
        for (var i = 0; i < wet.Length; i++)
        {
            var limited = Math.Clamp(wet[i], -1.0f, 1.0f);
            pcm[i] = (short)(limited * short.MaxValue);
        }

        return pcm;
    }

    private static float[] ApplyCathedralReverb(float[] input)
    {
        var output = new float[input.Length];
        var combs = new[]
        {
            new CombFilter(911, 0.74f, 0.30f),
            new CombFilter(1237, 0.71f, 0.28f),
            new CombFilter(1553, 0.69f, 0.25f),
            new CombFilter(2137, 0.66f, 0.22f),
        };

        for (var i = 0; i < input.Length; i++)
        {
            var dry = input[i];
            var sum = 0.0f;
            foreach (var comb in combs)
            {
                sum += comb.Process(dry);
            }

            output[i] = dry * 0.70f + sum * 0.32f;
        }

        var allpassA = new AllPassFilter(241, 0.55f);
        var allpassB = new AllPassFilter(113, 0.45f);
        for (var i = 0; i < output.Length; i++)
        {
            output[i] = allpassB.Process(allpassA.Process(output[i]));
        }

        var lp = 0.0f;
        for (var i = 0; i < output.Length; i++)
        {
            lp = (0.82f * lp) + (0.18f * output[i]);
            output[i] = output[i] * 0.78f + lp * 0.22f;
        }

        return output;
    }

    private static double VowelSignal(KeyProfile profile, double t, double vibrato)
    {
        var freq = profile.Frequency * vibrato;
        var organ = 0.78 * Organ(freq, t);
        var formants = VowelFormants.TryGetValue(profile.KeyIdentity, out var fs) ? fs : VowelFormants['a'];
        var choir = 0.0;
        choir += 0.10 * Math.Sin(2.0 * Math.PI * formants[0] * t);
        choir += 0.06 * Math.Sin(2.0 * Math.PI * formants[1] * t);
        choir += 0.03 * Math.Sin(2.0 * Math.PI * formants[2] * t);
        choir += 0.32 * Math.Sin(2.0 * Math.PI * freq * t);
        choir += 0.16 * Math.Sin(2.0 * Math.PI * freq * 2.0 * t);
        return organ + choir;
    }

    private static double DigitSignal(KeyProfile profile, double t, double vibrato, Random rng)
    {
        var freq = profile.Frequency * vibrato;
        var bell = 0.75 * Math.Exp(-3.1 * t) * (
            0.85 * Math.Sin(2.0 * Math.PI * freq * t) +
            0.32 * Math.Sin(2.0 * Math.PI * freq * 2.63 * t) +
            0.17 * Math.Sin(2.0 * Math.PI * freq * 4.11 * t));
        var organ = 0.38 * Organ(freq * 0.5, t);
        var noise = (rng.NextDouble() - 0.5) * 0.02 * Math.Exp(-6.0 * t);
        return bell + organ + noise;
    }

    private static double WhisperSignal(KeyProfile profile, double t, Random rng)
    {
        var hiss = ((rng.NextDouble() * 2.0) - 1.0) * 0.10 * Math.Exp(-7.0 * t);
        var tone = 0.22 * Math.Sin(2.0 * Math.PI * profile.Frequency * 1.5 * t);
        return tone + hiss;
    }

    private static double StrikeSignal(KeyProfile profile, double t, Random rng)
    {
        var burst = 0.9 * Math.Exp(-10.0 * t) * (((rng.NextDouble() * 2.0) - 1.0) * 0.7);
        var chime = 0.52 * Math.Exp(-4.0 * t) * Math.Sin(2.0 * Math.PI * profile.Frequency * 2.0 * t);
        var body = 0.28 * Organ(profile.Frequency, t);
        return burst + chime + body;
    }

    private static double GateSignal(KeyProfile profile, double t)
    {
        var body = 0.62 * Organ(profile.Frequency * 0.75, t);
        var clang = 0.36 * Math.Exp(-3.2 * t) * Math.Sin(2.0 * Math.PI * profile.Frequency * 3.4 * t);
        return body + clang;
    }

    private static double SweepSignal(KeyProfile profile, double t)
    {
        var glideFreq = profile.Frequency * (0.7 + (0.7 * t / profile.DurationSeconds));
        var glide = 0.46 * Math.Sin(2.0 * Math.PI * glideFreq * t);
        var servo = 0.18 * Math.Sin(2.0 * Math.PI * 32.0 * t);
        return glide + servo + (0.21 * Organ(profile.Frequency, t));
    }

    private static double SealSignal(KeyProfile profile, double t)
    {
        var pulse = Math.Sin(2.0 * Math.PI * profile.Frequency * t);
        pulse = Math.Sign(pulse) * Math.Pow(Math.Abs(pulse), 0.45);
        var sub = 0.22 * Math.Sin(2.0 * Math.PI * profile.Frequency * 0.5 * t);
        return (0.45 * pulse) + sub + (0.24 * Organ(profile.Frequency * 1.5, t));
    }

    private static double SpaceSignal(KeyProfile profile, double t)
    {
        return 0.28 * Math.Exp(-9.0 * t) * Math.Sin(2.0 * Math.PI * profile.Frequency * t);
    }

    private static double AccentSignal(KeyProfile profile, double t)
    {
        return (0.30 * Organ(profile.Frequency, t)) +
            (0.18 * Math.Exp(-5.0 * t) * Math.Sin(2.0 * Math.PI * profile.Frequency * 2.1 * t));
    }

    private static double Organ(double freq, double t)
    {
        var drift = 0.18 * Math.Sin(2.0 * Math.PI * 0.34 * t);
        return
            (0.78 * Math.Sin(2.0 * Math.PI * (freq + drift) * t)) +
            (0.34 * Math.Sin(2.0 * Math.PI * (freq * 2.0 + drift) * t)) +
            (0.20 * Math.Sin(2.0 * Math.PI * (freq * 3.0 + drift) * t)) +
            (0.09 * Math.Sin(2.0 * Math.PI * (freq * 4.0) * t)) +
            (0.06 * Math.Sin(2.0 * Math.PI * (freq * 0.5) * t));
    }

    private static double Envelope(double t, double duration, double attack, double release)
    {
        if (t < attack)
        {
            return t / Math.Max(attack, 1e-6);
        }

        var sustainStart = duration - release;
        if (t > sustainStart)
        {
            return Math.Max(0.0, (duration - t) / Math.Max(release, 1e-6));
        }

        return 1.0;
    }

    private static double MidiToHz(int midi)
    {
        return 440.0 * Math.Pow(2.0, (midi - 69) / 12.0);
    }

    private static void WriteWave(string path, short[] samples)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        var dataSize = samples.Length * sizeof(short);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVE"));
        writer.Write(Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        foreach (var sample in samples)
        {
            writer.Write(sample);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, HookProc lpfn, nint hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nuint wParam, nint lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool GetKeyboardState(byte[] lpKeyState);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint wVirtKey,
        uint wScanCode,
        byte[] lpKeyState,
        char[] pwszBuff,
        int cchBuff,
        uint wFlags,
        nint dwhkl);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out Msg lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(in Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(in Msg lpMsg);

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(string pszSound, nint hmod, uint fdwSound);

    private delegate nint HookProc(int nCode, nuint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public Point pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }
}

internal enum KeyFamily
{
    VowelLetter,
    Digit,
    SymbolAccent,
    SymbolWhisper,
    SymbolStrike,
    SymbolGate,
    SymbolSweep,
    SymbolSeal,
    Space,
}

internal sealed record KeyProfile(
    char Source,
    double Frequency,
    double DurationSeconds,
    double AttackSeconds,
    double ReleaseSeconds,
    double Gain,
    string Label,
    KeyFamily Family,
    char KeyIdentity);

internal sealed record PlaybackResult(char Character, string Label, double Frequency, string Path);

internal sealed class CombFilter(int delay, float feedback, float damping)
{
    private readonly float[] _buffer = new float[delay];
    private int _index;
    private float _filterStore;

    public float Process(float input)
    {
        var output = _buffer[_index];
        _filterStore = (output * (1.0f - damping)) + (_filterStore * damping);
        _buffer[_index] = input + (_filterStore * feedback);
        _index = (_index + 1) % _buffer.Length;
        return output;
    }
}

internal sealed class AllPassFilter(int delay, float feedback)
{
    private readonly float[] _buffer = new float[delay];
    private int _index;

    public float Process(float input)
    {
        var buffered = _buffer[_index];
        var output = -input + buffered;
        _buffer[_index] = input + (buffered * feedback);
        _index = (_index + 1) % _buffer.Length;
        return output;
    }
}
